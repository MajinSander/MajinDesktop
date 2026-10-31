using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MajinDesktop;

sealed class TrayApp : ApplicationContext
{
    public const int CycleId = 100;
    public const int UndoId = 101;

    public readonly Config Config;
    public readonly HashSet<int> FailedHotkeys = new(); // índices de slot (o CycleId/UndoId) que Windows no dejó registrar
    readonly NotifyIcon tray;
    readonly HotkeyWindow hotkeys;
    readonly ContextMenuStrip menu = new() { Font = Theme.Body, ShowImageMargin = false, ShowCheckMargin = true };
    readonly Osd osd = new();
    readonly Updater updater;
    SettingsForm? settings;
    OrganizerForm? organizer;
    readonly List<FileSystemWatcher> watchers = new();
    readonly HashSet<string> pendingNew = new(StringComparer.OrdinalIgnoreCase);
    readonly Timer newIconsTimer = new() { Interval = 1500 };

    public TrayApp(bool firstRun)
    {
        Config = Config.Load();
        hotkeys = new HotkeyWindow(OnHotkey, OpenSettings);
        menu.Opening += (_, _) => BuildMenu();
        tray = new NotifyIcon { Icon = Theme.AppIcon, Text = "MajinDesktop " + Application.ProductVersion, ContextMenuStrip = menu, Visible = true };
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) OpenSettings(); };
        RegisterHotkeys();
        updater = new Updater((title, detail) => osd.Flash(title, detail, null, Theme.Accent));
        updater.Start();
        _ = osd.Handle; // el vigilante del escritorio se sincroniza con esta ventana: tiene que existir ya
        newIconsTimer.Tick += (_, _) => PlaceNewIcons();
        UpdateDesktopWatcher();
        if (firstRun) OpenSettings();
        else Native.TrimMemory();
    }

    // Otra instancia pidió mostrar la ventana (llega desde un hilo del pool).
    public void PostShow() => hotkeys.PostShow();

    // ---------- Atajos ----------

    public void RegisterHotkeys()
    {
        hotkeys.UnregisterAll();
        FailedHotkeys.Clear();
        for (int i = 0; i < Config.SlotCount; i++)
            if (!hotkeys.Register(i + 1, Config.Slots[i].Hotkey)) FailedHotkeys.Add(i);
        if (!hotkeys.Register(CycleId, Config.Cycle)) FailedHotkeys.Add(CycleId);
        if (!hotkeys.Register(UndoId, Config.Undo)) FailedHotkeys.Add(UndoId);
        if (FailedHotkeys.Count > 0) Log.Info("Atajos ocupados por otra app: " + string.Join(", ", FailedHotkeys));
    }

    // Mientras se edita un atajo hay que soltarlos todos, si no Windows se "come" la combinación.
    public void SuspendHotkeys() => hotkeys.UnregisterAll();

    void OnHotkey(int id)
    {
        if (id == CycleId) Cycle();
        else if (id == UndoId) Undo();
        else if (id >= 1 && id <= Config.SlotCount) ApplySlot(id - 1);
    }

    // ---------- Acciones ----------

    public void ApplySlot(int i)
    {
        var slot = Config.Slots[i];
        string title = $"{i + 1} · {Config.SlotTitle(i)}";
        if (slot.IsEmpty)
        {
            osd.Flash(title, "Estado vacío: guarda primero el escritorio en él", null, Theme.Muted);
            return;
        }
        var mons = slot.UseDisplays ? slot.Monitors : null;
        var icons = slot.UseIcons ? slot.Icons : null;
        if (mons is not { Count: > 0 } && icons is not { Count: > 0 })
        {
            osd.Flash(title, "No tiene marcada ninguna parte (pantallas o iconos)", null, Theme.Muted);
            return;
        }
        if (Restore(title, mons, icons, slot.Monitors))
        {
            Config.Last = i;
            TrySave();
        }
        settings?.RefreshSlots();
    }

    // Vuelve a como estaba todo antes del último cambio. Pulsarlo otra vez rehace el cambio.
    public void Undo()
    {
        if (Config.Previous is not { Count: > 0 } && Config.PreviousIcons is not { Count: > 0 })
        {
            osd.Flash("Deshacer", "No hay ningún cambio que deshacer", null, Theme.Muted);
            return;
        }
        if (Restore("Deshacer", Config.Previous, Config.PreviousIcons, Config.Previous))
        {
            Config.Last = ActiveSlot() ?? -1;
            TrySave();
        }
        settings?.RefreshSlots();
    }

    // Aplica pantallas y después iconos. Antes guarda cómo estaba todo, para poder deshacerlo.
    bool Restore(string title, List<MonitorState>? mons, List<IconPos>? icons, IList<MonitorState>? preview)
    {
        var beforeMons = TrySnapshot();
        var beforeIcons = beforeMons != null && icons is { Count: > 0 } ? TryIcons(beforeMons) : null;

        var status = ApplyStatus.AlreadyActive;
        if (mons is { Count: > 0 })
        {
            status = Displays.Apply(mons, false, out string error);
            if (status == ApplyStatus.Failed)
            {
                Log.Error($"{title}: {error}");
                osd.Flash(title, error, preview, Theme.Red);
                return false;
            }
            if (status == ApplyStatus.NoMonitorsFound && icons is not { Count: > 0 })
            {
                osd.Flash(title, "Ninguna de sus pantallas está conectada ahora", preview, Theme.Red);
                return false;
            }
        }

        if (status == ApplyStatus.Applied || icons is { Count: > 0 })
        {
            Config.Previous = beforeMons;
            Config.PreviousIcons = beforeIcons;
        }

        string screens = status == ApplyStatus.Applied ? "Pantallas aplicadas" : mons is { Count: > 0 } ? "Pantallas ya estaban así" : "";
        if (icons is not { Count: > 0 })
        {
            osd.Flash(title, screens, preview, Theme.Green);
            return true;
        }
        if (status == ApplyStatus.Applied)
        {
            // Explorer recoloca los iconos a su manera tras cambiar las pantallas: se espera a que termine.
            osd.Flash(title, screens + " · colocando iconos…", preview, Theme.Green);
            PlaceIconsLater(title, icons, preview, screens);
        }
        else
        {
            var r = PlaceIcons(title, icons);
            if (r != null) osd.Flash(title, Join(screens, IconsText(r)), preview, r.Missing + r.NoMonitor > 0 ? Theme.Accent : Theme.Green);
        }
        return true;
    }

    void PlaceIconsLater(string title, List<IconPos> icons, IList<MonitorState>? preview, string screens)
    {
        int round = 0;
        var timer = new Timer { Interval = 1200 };
        timer.Tick += (_, _) =>
        {
            round++;
            var r = PlaceIcons(title, icons);
            if (round == 1 && r != null)
            {
                osd.Flash(title, Join(screens, IconsText(r)), preview, Theme.Green);
                timer.Interval = 2500; // segunda pasada por si Explorer vuelve a moverlos un poco después
                return;
            }
            timer.Stop();
            timer.Dispose();
        };
        timer.Start();
    }

    IconApplyResult? PlaceIcons(string title, List<IconPos> icons)
    {
        try
        {
            var r = Icons.Apply(icons, Displays.Snapshot());
            if (r.AutoArrangeDisabled) Log.Info("Desactivado 'Organizar iconos automáticamente' para poder colocarlos");
            return r;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Win32Exception)
        {
            Log.Error("Colocar iconos", ex);
            osd.Flash(title, "No se pudieron colocar los iconos (¿Explorer reiniciándose?)", null, Theme.Red);
            return null;
        }
    }

    static string IconsText(IconApplyResult r)
    {
        string text = r.Moved == 0 ? "iconos ya en su sitio" : $"{r.Moved} iconos recolocados";
        int skipped = r.Missing + r.NoMonitor;
        if (skipped > 0) text += $" ({skipped} ya no están o su pantalla no está)";
        if (r.AutoArrangeDisabled) text += " · quitado 'Organizar automáticamente'";
        return text;
    }

    static string Join(string a, string b) => a.Length == 0 ? char.ToUpper(b[0]) + b.Substring(1) : $"{a} · {b}";

    static List<MonitorState>? TrySnapshot()
    {
        try { return Displays.Snapshot(); }
        catch (Win32Exception ex) { Log.Error("Leer pantallas", ex); return null; }
    }

    static List<IconPos>? TryIcons(List<MonitorState> mons)
    {
        try { return Icons.Snapshot(mons); }
        catch (Exception ex) when (ex is COMException or InvalidCastException) { Log.Error("Leer iconos", ex); return null; }
    }

    public void Cycle()
    {
        var saved = Enumerable.Range(0, Config.SlotCount).Where(i => !Config.Slots[i].IsEmpty).ToList();
        if (saved.Count == 0)
        {
            osd.Flash("Sin estados", "Guarda el escritorio en un estado desde el menú de la bandeja", null, Theme.Muted);
            return;
        }
        int from = ActiveSlot() ?? Config.Last;
        int next = saved.Where(i => i > from).DefaultIfEmpty(saved[0]).First();
        ApplySlot(next);
    }

    public void SaveSlot(int i)
    {
        string title = $"{i + 1} · {Config.SlotTitle(i)}";
        try
        {
            var slot = Config.Slots[i];
            slot.Monitors = Displays.Snapshot();
            var icons = TryIcons(slot.Monitors);
            slot.Icons = icons ?? new List<IconPos>();
            Config.Last = i;
            Config.Save();
            string detail = $"Guardadas {slot.Monitors.Count} pantallas" + (icons != null ? $" y {icons.Count} iconos" : " (los iconos no se pudieron leer)");
            osd.Flash(title, detail, slot.Monitors, icons != null ? Theme.Accent : Theme.Red);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            osd.Flash("No se pudo guardar", ex.Message, null, Theme.Red);
        }
        settings?.RefreshSlots();
    }

    public void ClearSlot(int i)
    {
        Config.Slots[i].Monitors.Clear();
        Config.Slots[i].Icons.Clear();
        TrySave();
        settings?.RefreshSlots();
    }

    public void TrySave()
    {
        try { Config.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            osd.Flash("No se pudo guardar la configuración", ex.Message, null, Theme.Red);
        }
    }

    public int? ActiveSlot()
    {
        try
        {
            var now = Displays.Snapshot();
            for (int i = 0; i < Config.SlotCount; i++)
                if (Displays.Matches(now, Config.Slots[i].Monitors)) return i;
        }
        catch (Win32Exception) { }
        return null;
    }

    // ---------- Menú de la bandeja ----------

    void BuildMenu()
    {
        menu.Items.Clear();
        int? active = ActiveSlot();
        bool any = false;
        for (int i = 0; i < Config.SlotCount; i++)
        {
            if (Config.Slots[i].IsEmpty) continue;
            int n = i;
            var item = Item($"{i + 1}   {Config.SlotTitle(i)}", () => ApplySlot(n));
            item.ShortcutKeyDisplayString = Config.Slots[i].Hotkey.ToString();
            item.Checked = active == i;
            menu.Items.Add(item);
            any = true;
        }
        if (!any) menu.Items.Add(new ToolStripMenuItem("Aún no hay estados guardados") { Enabled = false });

        menu.Items.Add(new ToolStripSeparator());
        var save = Item("Guardar el escritorio actual en", null);
        for (int i = 0; i < Config.SlotCount; i++)
        {
            int n = i;
            string label = Config.Slots[i].IsEmpty ? $"{i + 1}   (vacío)" : $"{i + 1}   {Config.SlotTitle(i)}";
            save.DropDownItems.Add(Item(label, () => SaveSlot(n)));
        }
        if (save.DropDown is ToolStripDropDownMenu dd) { dd.ShowImageMargin = false; dd.ShowCheckMargin = true; dd.Font = Theme.Body; }
        menu.Items.Add(save);
        var next = Item("Siguiente estado", Cycle);
        next.ShortcutKeyDisplayString = Config.Cycle.ToString();
        next.Enabled = any;
        menu.Items.Add(next);
        var undo = Item("Deshacer último cambio", Undo);
        undo.ShortcutKeyDisplayString = Config.Undo.ToString();
        undo.Enabled = Config.Previous is { Count: > 0 } || Config.PreviousIcons is { Count: > 0 };
        menu.Items.Add(undo);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item("Organizar escritorio…", OpenOrganizer));
        var open = Item("Configurar…", OpenSettings);
        open.Font = Theme.Bold;
        menu.Items.Add(open);
        var auto = Item("Iniciar con Windows", ToggleAutoStart);
        auto.Checked = Config.AutoStart;
        menu.Items.Add(auto);
        if (updater.Available != null && !Updater.Installed)
            menu.Items.Add(Item($"Descargar la versión {updater.Available}", () => System.Diagnostics.Process.Start(Updater.ReleasesUrl)));
        else
            menu.Items.Add(Item("Buscar actualizaciones", () => updater.Check(true)));
        menu.Items.Add(Item("Salir", Quit));
    }

    static ToolStripMenuItem Item(string text, Action? onClick)
    {
        var item = new ToolStripMenuItem(text) { Padding = new Padding(2, 4, 2, 4) };
        if (onClick != null) item.Click += (_, _) => onClick();
        return item;
    }

    // ---------- Organizador ----------

    public void OpenOrganizer()
    {
        if (organizer == null || organizer.IsDisposed)
        {
            organizer = new OrganizerForm(this);
            organizer.FormClosed += (_, _) =>
            {
                organizer = null;
                Native.TrimMemory();
            };
            organizer.Show();
        }
        if (organizer.WindowState == FormWindowState.Minimized) organizer.WindowState = FormWindowState.Normal;
        organizer.Activate();
    }

    // Aplica lo que se movió en el organizador. Ctrl+Alt+Shift+Retroceso lo deshace, como cualquier cambio.
    public bool ApplyOrganizer(Dictionary<string, Point> targets)
    {
        try
        {
            var before = Icons.Snapshot(Displays.Snapshot());
            int moved = Icons.MoveTo(targets, out bool autoArrange);
            Config.Previous = null;
            Config.PreviousIcons = before;
            TrySave();
            string detail = $"{moved} iconos colocados · {Config.Undo} lo deshace";
            if (autoArrange) detail += " · quitado 'Organizar automáticamente'";
            osd.Flash("Escritorio organizado", detail, null, Theme.Green);
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Win32Exception)
        {
            Log.Error("Aplicar organizador", ex);
            osd.Flash("No se pudo organizar", ex.Message, null, Theme.Red);
            return false;
        }
    }

    // Vigila el escritorio (el tuyo y el público) para llevar los accesos nuevos a su zona.
    public void UpdateDesktopWatcher()
    {
        foreach (var w in watchers) w.Dispose();
        watchers.Clear();
        if (!Config.AutoPlaceNew || Config.Zones.Count == 0) return;
        foreach (var dir in new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                                    Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory) })
        {
            if (!Directory.Exists(dir)) continue;
            var w = new FileSystemWatcher(dir) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName, SynchronizingObject = osd };
            w.Created += (_, e) => QueueNew(e.FullPath);
            w.Renamed += (_, e) => QueueNew(e.FullPath);
            w.EnableRaisingEvents = true;
            watchers.Add(w);
        }
    }

    void QueueNew(string path)
    {
        pendingNew.Add(path);
        newIconsTimer.Stop();   // se espera a que Windows termine de poner el icono en el escritorio
        newIconsTimer.Start();
    }

    void PlaceNewIcons()
    {
        newIconsTimer.Stop();
        var paths = pendingNew.ToList();
        pendingNew.Clear();
        try
        {
            var mons = Organizer.Monitors(Displays.Snapshot());
            var items = Icons.Read(out var cell);
            var taken = items.Select(i => i.Pos).ToList();
            var targets = new Dictionary<string, Point>(StringComparer.OrdinalIgnoreCase);
            var names = new List<string>();
            foreach (var path in paths)
            {
                var item = items.FirstOrDefault(i => string.Equals(i.Key, path, StringComparison.OrdinalIgnoreCase));
                if (item.Key == null) continue;
                var zone = Organizer.ZoneFor(item.Name, Shortcuts.Inspect(path).Kind, Config.Zones);
                if (zone == null || Organizer.ZoneRect(zone, mons) is not { } rect) continue;
                taken.Remove(item.Pos);
                if (Organizer.FreeCellIn(rect, cell, taken) is not { } free) continue;
                targets[item.Key] = free;
                taken.Add(free);
                names.Add($"«{item.Name}» → {(zone.Name.Length > 0 ? zone.Name : "su zona")}");
            }
            if (targets.Count == 0) return;
            Icons.MoveTo(targets, out _);
            Log.Info("Iconos nuevos colocados: " + string.Join(", ", names));
            osd.Flash("Icono nuevo colocado", names.Count == 1 ? names[0] : $"{names.Count} iconos llevados a sus zonas", null, Theme.Accent);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Win32Exception or IOException)
        {
            Log.Error("Colocar iconos nuevos", ex);
        }
    }

    public void ToggleAutoStart()
    {
        try { Config.AutoStart = !Config.AutoStart; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            osd.Flash("No se pudo cambiar el inicio automático", ex.Message, null, Theme.Red);
        }
        settings?.RefreshFooter();
    }

    public void OpenSettings()
    {
        if (settings == null || settings.IsDisposed)
        {
            settings = new SettingsForm(this);
            settings.FormClosed += (_, _) =>
            {
                settings = null;
                RegisterHotkeys();
                Native.TrimMemory();
            };
            settings.Show();
        }
        if (settings.WindowState == FormWindowState.Minimized) settings.WindowState = FormWindowState.Normal;
        settings.Activate();
    }

    public void Quit()
    {
        settings?.Close();
        tray.Visible = false;
        tray.Dispose();
        organizer?.Close();
        foreach (var w in watchers) w.Dispose();
        newIconsTimer.Dispose();
        hotkeys.Dispose();
        updater.Dispose();
        osd.Dispose();
        ExitThread();
    }
}
