using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
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
            osd.Flash(title, "Slot vacío: guarda primero una disposición en él", null, Theme.Muted);
            return;
        }
        var before = TrySnapshot();
        var status = Displays.Apply(slot.Monitors, false, out string error);
        switch (status)
        {
            case ApplyStatus.Applied:
            case ApplyStatus.AlreadyActive:
                Config.Last = i;
                if (status == ApplyStatus.Applied && before != null) Config.Previous = before;
                TrySave();
                osd.Flash(title, status == ApplyStatus.Applied ? "Disposición aplicada" : "Ya estaba activa", slot.Monitors, Theme.Green);
                break;
            case ApplyStatus.NoMonitorsFound:
                osd.Flash(title, "Ninguna de sus pantallas está conectada ahora", slot.Monitors, Theme.Red);
                break;
            default:
                Log.Error($"Aplicar slot {i + 1}: {error}");
                osd.Flash(title, error, slot.Monitors, Theme.Red);
                break;
        }
        settings?.RefreshSlots();
    }

    // Vuelve a como estaba todo antes del último cambio. Pulsarlo otra vez rehace el cambio.
    public void Undo()
    {
        var previous = Config.Previous;
        if (previous == null || previous.Count == 0)
        {
            osd.Flash("Deshacer", "No hay ningún cambio que deshacer", null, Theme.Muted);
            return;
        }
        var before = TrySnapshot();
        var status = Displays.Apply(previous, false, out string error);
        switch (status)
        {
            case ApplyStatus.Applied:
                Config.Previous = before;
                Config.Last = ActiveSlot() ?? -1;
                TrySave();
                osd.Flash("Deshecho", "Las pantallas vuelven a estar como antes", previous, Theme.Green);
                break;
            case ApplyStatus.AlreadyActive:
                osd.Flash("Deshacer", "Las pantallas ya están así", previous, Theme.Muted);
                break;
            case ApplyStatus.NoMonitorsFound:
                osd.Flash("Deshacer", "Esas pantallas ya no están conectadas", previous, Theme.Red);
                break;
            default:
                Log.Error("Deshacer: " + error);
                osd.Flash("No se pudo deshacer", error, previous, Theme.Red);
                break;
        }
        settings?.RefreshSlots();
    }

    static List<MonitorState>? TrySnapshot()
    {
        try { return Displays.Snapshot(); }
        catch (Win32Exception ex) { Log.Error("Leer pantallas", ex); return null; }
    }

    public void Cycle()
    {
        var saved = Enumerable.Range(0, Config.SlotCount).Where(i => !Config.Slots[i].IsEmpty).ToList();
        if (saved.Count == 0)
        {
            osd.Flash("Sin slots", "Guarda una disposición desde el menú de la bandeja", null, Theme.Muted);
            return;
        }
        int from = ActiveSlot() ?? Config.Last;
        int next = saved.Where(i => i > from).DefaultIfEmpty(saved[0]).First();
        ApplySlot(next);
    }

    public void SaveSlot(int i)
    {
        try
        {
            Config.Slots[i].Monitors = Displays.Snapshot();
            Config.Last = i;
            Config.Save();
            osd.Flash($"{i + 1} · {Config.SlotTitle(i)}", "Disposición actual guardada", Config.Slots[i].Monitors, Theme.Accent);
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
        if (!any) menu.Items.Add(new ToolStripMenuItem("Aún no hay slots guardados") { Enabled = false });

        menu.Items.Add(new ToolStripSeparator());
        var save = Item("Guardar disposición actual en", null);
        for (int i = 0; i < Config.SlotCount; i++)
        {
            int n = i;
            string label = Config.Slots[i].IsEmpty ? $"{i + 1}   (vacío)" : $"{i + 1}   {Config.SlotTitle(i)}";
            save.DropDownItems.Add(Item(label, () => SaveSlot(n)));
        }
        if (save.DropDown is ToolStripDropDownMenu dd) { dd.ShowImageMargin = false; dd.ShowCheckMargin = true; dd.Font = Theme.Body; }
        menu.Items.Add(save);
        var next = Item("Siguiente slot", Cycle);
        next.ShortcutKeyDisplayString = Config.Cycle.ToString();
        next.Enabled = any;
        menu.Items.Add(next);
        var undo = Item("Deshacer último cambio", Undo);
        undo.ShortcutKeyDisplayString = Config.Undo.ToString();
        undo.Enabled = Config.Previous is { Count: > 0 };
        menu.Items.Add(undo);

        menu.Items.Add(new ToolStripSeparator());
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
        hotkeys.Dispose();
        updater.Dispose();
        osd.Dispose();
        ExitThread();
    }
}
