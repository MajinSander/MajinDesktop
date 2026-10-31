using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MajinDesktop;

// Juegos y accesos directos: crear los que faltan desde tu biblioteca y arreglar los del escritorio en lote.
sealed class GamesForm : Form
{
    readonly TrayApp app;
    readonly Label subtitle = new() { AutoSize = true, ForeColor = Theme.Muted };
    readonly Label status = new() { AutoSize = false, ForeColor = Theme.Muted, TextAlign = ContentAlignment.MiddleLeft };
    readonly PillButton tabLibrary = new("Biblioteca", PillButton.Kind.Primary);
    readonly PillButton tabDesktop = new("Escritorio", PillButton.Kind.Secondary);
    readonly Panel libraryBar = new() { BackColor = Theme.Bg }, desktopBar = new() { BackColor = Theme.Bg };
    readonly RowList list = new();
    readonly PillButton create = new("Crear accesos", PillButton.Kind.Primary);
    readonly Toggle toZones = new("Llevar a sus zonas");
    readonly PillButton fixIcons = new("Arreglar iconos", PillButton.Kind.Secondary);
    readonly PillButton cleanNames = new("Limpiar nombres", PillButton.Kind.Secondary);
    readonly PillButton recycleBroken = new("Rotos a la papelera", PillButton.Kind.Secondary);
    readonly PillButton changeIcon = new("Cambiar icono…", PillButton.Kind.Secondary);
    readonly PillButton undo = new("Deshacer", PillButton.Kind.Ghost);
    readonly Stack<BatchStep> journal = new();
    List<Game> games = new();
    List<DeskEntry> desk = new();
    bool showingLibrary = true;

    sealed class DeskEntry
    {
        public string Path = "", Name = "";
        public ShortcutInfo Info = new();
        public bool IconMissing, BadName;
        public bool Broken => Info.Kind == ItemKind.Broken;
    }

    public GamesForm(TrayApp app)
    {
        this.app = app;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Juegos y accesos directos · MajinDesktop";
        Icon = Theme.AppIcon;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(980, 780);
        MinimumSize = new Size(820, 560);

        Controls.Add(new Label { Text = "Juegos y accesos directos", Font = Theme.Title, ForeColor = Theme.Bright, AutoSize = true, Location = new Point(20, 10) });
        subtitle.Location = new Point(22, 40);
        Controls.Add(subtitle);

        tabLibrary.SetBounds(20, 70, 120, 32);
        tabDesktop.SetBounds(146, 70, 120, 32);
        tabLibrary.Click += (_, _) => Show(true);
        tabDesktop.Click += (_, _) => Show(false);
        Controls.Add(tabLibrary);
        Controls.Add(tabDesktop);

        // Barra de la biblioteca
        libraryBar.SetBounds(20, 112, 940, 36);
        libraryBar.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        var markMissing = new PillButton("Marcar los que no tienen", PillButton.Kind.Secondary);
        markMissing.SetBounds(0, 2, 190, 32);
        markMissing.Click += (_, _) => { foreach (var r in list.Rows) r.Checked = r.Checkable; UpdateButtons(); list.Invalidate(true); };
        toZones.SetBounds(206, 4, 200, 28);
        toZones.Checked = app.Config.Zones.Any(z => z.HasRule);
        toZones.Toggled += () => toZones.Checked = !toZones.Checked;
        new ToolTip().SetToolTip(toZones, "Coloca los accesos nuevos en la zona que les toque (las zonas se crean en Organizar escritorio)");
        create.SetBounds(940 - 180, 2, 180, 32);
        create.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        create.Click += (_, _) => CreateSelected();
        libraryBar.Controls.AddRange(new Control[] { markMissing, toZones, create });
        Controls.Add(libraryBar);

        // Barra del escritorio
        desktopBar.SetBounds(20, 112, 940, 36);
        desktopBar.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        int x = 0;
        foreach (var (b, w, act) in new (PillButton, int, Action)[]
        {
            (fixIcons, 150, FixIcons), (cleanNames, 150, CleanNames), (recycleBroken, 170, RecycleBroken), (changeIcon, 140, ChangeIcon),
        })
        {
            b.SetBounds(x, 2, w, 32);
            b.Click += (_, _) => act();
            desktopBar.Controls.Add(b);
            x += w + 8;
        }
        desktopBar.Visible = false;
        Controls.Add(desktopBar);

        undo.SetBounds(980 - 20 - 110, 70, 110, 32);
        undo.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        undo.Click += (_, _) => Undo();
        Controls.Add(undo);

        list.SetBounds(20, 158, 940, 780 - 158 - 44);
        list.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        list.CheckedChanged += UpdateButtons;
        Controls.Add(list);

        status.SetBounds(20, 780 - 38, 940, 30);
        status.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(status);
        ResumeLayout(false);

        Reload();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.DarkTitleBar(Handle);
    }

    // ---------- Datos ----------

    void Reload()
    {
        Cursor = Cursors.WaitCursor;
        try
        {
            var read = Icons.Read(out _);
            desk = read.Where(i => File.Exists(i.Key) && (i.Key.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || i.Key.EndsWith(".url", StringComparison.OrdinalIgnoreCase)))
                .Select(i =>
                {
                    var info = Shortcuts.Inspect(i.Key);
                    return new DeskEntry
                    {
                        Path = i.Key, Name = i.Name, Info = info,
                        IconMissing = info.Kind == ItemKind.Steam && info.IconFile.Length > 0 && !File.Exists(info.IconFile),
                        BadName = Batch.NeedsCleanName(i.Key),
                    };
                }).OrderBy(d => Organizer.SortName(d.Name), StringComparer.CurrentCultureIgnoreCase).ToList();
            games = Library.Scan();
            Library.MatchShortcuts(games, desk.Select(d => (d.Path, d.Info)));
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Win32Exception or IOException)
        {
            Log.Error("Juegos: leer", ex);
            status.Text = "No se pudo leer el escritorio: " + ex.Message;
        }
        finally { Cursor = Cursors.Default; }
        int missing = games.Count(g => g.Shortcut == null);
        subtitle.Text = $"{games.Count} juegos instalados (Steam, Epic, GOG), {missing} sin acceso en el escritorio  ·  {desk.Count} accesos directos en el escritorio";
        Show(showingLibrary);
    }

    void Show(bool library)
    {
        showingLibrary = library;
        tabLibrary.Style = library ? PillButton.Kind.Primary : PillButton.Kind.Secondary;
        tabDesktop.Style = library ? PillButton.Kind.Secondary : PillButton.Kind.Primary;
        libraryBar.Visible = library;
        desktopBar.Visible = !library;
        list.SetRows(library ? games.Select(LibraryRow) : desk.Select(DeskRow));
        UpdateButtons();
    }

    Row LibraryRow(Game g) => new()
    {
        Tag = g,
        Title = g.Name,
        Detail = $"{g.Platform}  ·  {g.InstallDir}",
        Image = g.Shortcut != null ? ShellImages.Get(g.Shortcut) : File.Exists(g.IconFile) ? ShellImages.Get(g.IconFile) : null,
        Badge = g.Shortcut != null ? "Ya tiene acceso" : "Sin acceso",
        BadgeColor = g.Shortcut != null ? Theme.Green : Theme.Muted,
        Checkable = g.Shortcut == null,
    };

    Row DeskRow(DeskEntry d)
    {
        var issues = new List<string>();
        if (d.Broken) issues.Add("Roto");
        if (d.IconMissing) issues.Add("Icono sin descargar");
        if (d.BadName) issues.Add("Nombre a limpiar");
        return new Row
        {
            Tag = d,
            Title = d.Name,
            Detail = $"{Shortcuts.KindName(d.Info.Kind)}  ·  {(d.Info.Target.Length > 0 ? d.Info.Target : Path.GetFileName(d.Path))}",
            Image = ShellImages.Get(d.Path),
            Badge = issues.Count == 0 ? "" : string.Join(" · ", issues),
            BadgeColor = d.Broken ? Theme.Red : Theme.Accent,
            Checkable = true,
        };
    }

    void UpdateButtons()
    {
        int sel = list.Rows.Count(r => r.Checked);
        create.Text = sel == 0 ? "Crear accesos" : $"Crear {sel} accesos";
        create.Enabled = showingLibrary && sel > 0;
        var scope = DeskScope();
        fixIcons.Text = $"Arreglar iconos ({scope.Count(d => d.IconMissing)})";
        cleanNames.Text = $"Limpiar nombres ({scope.Count(d => d.BadName)})";
        recycleBroken.Text = $"Rotos a la papelera ({scope.Count(d => d.Broken)})";
        fixIcons.Enabled = scope.Any(d => d.IconMissing);
        cleanNames.Enabled = scope.Any(d => d.BadName);
        recycleBroken.Enabled = scope.Any(d => d.Broken);
        changeIcon.Enabled = !showingLibrary && sel > 0;
        undo.Enabled = journal.Count > 0 && journal.Peek().CanUndo;
        status.Text = showingLibrary
            ? "Marca los juegos y pulsa Crear: el acceso se crea en tu escritorio con el icono del juego."
            : sel > 0 ? $"{sel} marcados: los botones de arriba actúan solo sobre ellos." : "Sin marcar nada, los botones de arriba actúan sobre todo el escritorio. Marca accesos para cambiarles el icono.";
    }

    List<DeskEntry> DeskScope()
    {
        var sel = list.Rows.Where(r => r.Checked && r.Tag is DeskEntry).Select(r => (DeskEntry)r.Tag!).ToList();
        return sel.Count > 0 ? sel : desk;
    }

    // ---------- Órdenes ----------

    void Run(string what, Func<BatchStep, string> work)
    {
        var step = new BatchStep { Description = what };
        Cursor = Cursors.WaitCursor;
        string result;
        try { result = work(step); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or WebException or ArgumentException or OutOfMemoryException)
        {
            Log.Error(what, ex);
            result = $"{what}: error, {ex.Message}" + (step.Count > 0 ? $" (se hicieron {step.Count} antes del error)" : "");
        }
        finally { Cursor = Cursors.Default; }
        if (step.Count > 0) journal.Push(step);
        Log.Info($"{what}: {step.Count}");
        Reload();
        status.Text = result + (step.Count > 0 ? (step.CanUndo ? "  ·  Deshacer lo revierte." : "  ·  Se pueden recuperar desde la Papelera de reciclaje.") : "");
        status.ForeColor = result.Contains("error") ? Theme.Red : Theme.Green;
    }

    void CreateSelected()
    {
        var chosen = list.Rows.Where(r => r.Checked && r.Tag is Game).Select(r => (Game)r.Tag!).ToList();
        if (chosen.Count == 0) return;
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        bool place = toZones.Checked;
        Run("Crear accesos", step =>
        {
            var created = new List<string>();
            int downloaded = 0;
            foreach (var g in chosen)
            {
                if (g.Platform == "Steam" && g.IconHash.Length > 0 && !File.Exists(g.IconFile))
                {
                    try { g.IconFile = Library.DownloadSteamIcon(g.Id, g.IconHash, g.IconFile); downloaded++; }
                    catch (WebException ex) { Log.Error("Icono de " + g.Name, ex); g.IconFile = ""; }
                }
                string path = Library.CreateShortcut(g, desktop);
                created.Add(path);
                step.Count++;
                step.Undo.Insert(0, () => { if (File.Exists(path)) File.Delete(path); });
            }
            if (place) foreach (var p in created) app.QueueNewIcon(p);
            return $"{created.Count} accesos creados" + (downloaded > 0 ? $", {downloaded} iconos descargados de Steam" : "") + (place ? ", colocándolos en sus zonas" : "");
        });
    }

    void FixIcons()
    {
        var todo = DeskScope().Where(d => d.IconMissing).ToList();
        Run("Arreglar iconos", step =>
        {
            int ok = 0;
            foreach (var d in todo)
            {
                string id = Library.SteamAppId(d.Info.Target), hash = Path.GetFileNameWithoutExtension(d.Info.IconFile);
                if (id.Length == 0 || hash.Length == 0) continue;
                string saved = Library.DownloadSteamIcon(id, hash, d.Info.IconFile);
                if (!string.Equals(saved, d.Info.IconFile, StringComparison.OrdinalIgnoreCase)) Batch.SetIcon(d.Path, saved, 0, step);
                else { Batch.Refresh(d.Path); step.Count++; step.CanUndo = false; }
                ok++;
            }
            return $"{ok} iconos de Steam descargados";
        });
    }

    void CleanNames()
    {
        var todo = DeskScope().Where(d => d.BadName).ToList();
        Run("Limpiar nombres", step =>
        {
            foreach (var d in todo) Batch.Rename(d.Path, Batch.CleanName(Path.GetFileNameWithoutExtension(d.Path)), step);
            return $"{step.Count} nombres limpiados (sin «- Acceso directo», ™ ni ®)";
        });
    }

    void RecycleBroken()
    {
        var todo = DeskScope().Where(d => d.Broken).ToList();
        if (todo.Count == 0) return;
        string names = string.Join(", ", todo.Take(5).Select(d => d.Name)) + (todo.Count > 5 ? "…" : "");
        if (!DarkDialog.Ask(this, "Accesos rotos", $"Se mandarán {todo.Count} accesos rotos a la Papelera de reciclaje:\n{names}", "A la papelera", "Cancelar")) return;
        Run("Rotos a la papelera", step =>
        {
            Batch.Recycle(todo.Select(d => d.Path).ToList(), step);
            return $"{step.Count} accesos rotos en la papelera";
        });
    }

    void ChangeIcon()
    {
        var todo = list.Rows.Where(r => r.Checked && r.Tag is DeskEntry).Select(r => (DeskEntry)r.Tag!).ToList();
        if (todo.Count == 0) return;
        using var dlg = new OpenFileDialog
        {
            Title = "Elige el icono",
            Filter = "Iconos e imágenes|*.ico;*.exe;*.dll;*.png;*.jpg;*.jpeg;*.bmp|Todos los archivos|*.*",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        Run("Cambiar icono", step =>
        {
            string file = dlg.FileName;
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp") file = Batch.ImageToIco(file);
            foreach (var d in todo) Batch.SetIcon(d.Path, file, 0, step);
            return $"Icono cambiado en {step.Count} accesos";
        });
    }

    void Undo()
    {
        if (journal.Count == 0 || !journal.Peek().CanUndo) return;
        var step = journal.Pop();
        int errors = 0;
        foreach (var a in step.Undo)
        {
            try { a(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException) { errors++; Log.Error("Deshacer " + step.Description, ex); }
        }
        Reload();
        status.Text = $"Deshecho: {step.Description}" + (errors > 0 ? $" ({errors} no se pudieron revertir)" : "");
        status.ForeColor = errors > 0 ? Theme.Red : Theme.Green;
    }

    // ---------- Lista ----------

    sealed class Row
    {
        public object? Tag;
        public string Title = "", Detail = "", Badge = "";
        public Color BadgeColor = Theme.Muted;
        public Bitmap? Image;
        public bool Checkable = true, Checked;
    }

    // Lista dibujada a mano (con su propia barra de desplazamiento oscura).
    sealed class RowList : Control
    {
        public readonly List<Row> Rows = new();
        public event Action? CheckedChanged;
        int scroll, hover = -1;
        const int RowH = 48;

        public RowList()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.UserPaint | ControlStyles.Selectable, true);
        }

        float S => DeviceDpi / 96f;
        int H => (int)(RowH * S);
        int MaxScroll => Math.Max(0, Rows.Count * H - Height);

        public void SetRows(IEnumerable<Row> rows)
        {
            Rows.Clear();
            Rows.AddRange(rows);
            scroll = 0;
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            scroll = Math.Max(0, Math.Min(MaxScroll, scroll - e.Delta / 120 * H * 2));
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int i = (e.Y + scroll) / H;
            int h = i < Rows.Count ? i : -1;
            if (h != hover) { hover = h; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            int i = (e.Y + scroll) / H;
            if (i < 0 || i >= Rows.Count || !Rows[i].Checkable) return;
            Rows[i].Checked = !Rows[i].Checked;
            CheckedChanged?.Invoke();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            float s = S;
            g.Clear(Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            int first = scroll / H;
            for (int i = first; i < Rows.Count && (i * H - scroll) < Height; i++)
            {
                var row = Rows[i];
                int y = i * H - scroll;
                var rect = new RectangleF(0, y + 2, Width - 10 * s, H - 4);
                using (var path = Theme.Rounded(rect, 6 * s))
                using (var bg = new SolidBrush(row.Checked ? Color.FromArgb(60, Theme.Accent) : i == hover ? Theme.Hover : Theme.Panel))
                    g.FillPath(bg, path);

                float x = 12 * s;
                var box = new RectangleF(x, y + (H - 18 * s) / 2, 18 * s, 18 * s);
                using (var path = Theme.Rounded(box, 4 * s))
                using (var b = new SolidBrush(!row.Checkable ? Theme.Deep : row.Checked ? Theme.Accent : Theme.Deep))
                    g.FillPath(b, path);
                if (row.Checked)
                    using (var pen = new Pen(Color.White, 2 * s) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawLines(pen, new[] { new PointF(box.X + 4 * s, box.Y + 9 * s), new PointF(box.X + 8 * s, box.Y + 13 * s), new PointF(box.X + 14 * s, box.Y + 5 * s) });
                x += 30 * s;
                float img = 32 * s;
                if (row.Image != null) g.DrawImage(row.Image, x, y + (H - img) / 2, img, img);
                x += img + 12 * s;

                float badgeW = 0;
                if (row.Badge.Length > 0)
                {
                    var size = TextRenderer.MeasureText(row.Badge, Theme.Small);
                    badgeW = size.Width + 16 * s;
                    var br = new RectangleF(rect.Right - badgeW - 12 * s, y + (H - 22 * s) / 2, badgeW, 22 * s);
                    using (var path = Theme.Rounded(br, 11 * s))
                    using (var b = new SolidBrush(Color.FromArgb(50, row.BadgeColor))) g.FillPath(b, path);
                    TextRenderer.DrawText(g, row.Badge, Theme.Small, Rectangle.Round(br), row.BadgeColor,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                }
                int textW = (int)(rect.Right - x - badgeW - 24 * s);
                TextRenderer.DrawText(g, row.Title, Theme.Bold, new Rectangle((int)x, (int)(y + 6 * s), textW, (int)(20 * s)), Theme.Bright,
                    TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
                TextRenderer.DrawText(g, row.Detail, Theme.Small, new Rectangle((int)x, (int)(y + 25 * s), textW, (int)(18 * s)), Theme.Muted,
                    TextFormatFlags.PathEllipsis | TextFormatFlags.SingleLine);
            }

            // Barra de desplazamiento
            if (MaxScroll > 0)
            {
                float trackH = Height, thumbH = Math.Max(30 * s, trackH * Height / (Rows.Count * H));
                float thumbY = (trackH - thumbH) * scroll / MaxScroll;
                using var thumb = new SolidBrush(Theme.Line);
                using var path = Theme.Rounded(new RectangleF(Width - 6 * s, thumbY, 5 * s, thumbH), 2.5f * s);
                g.FillPath(thumb, path);
            }
            if (Rows.Count == 0)
                TextRenderer.DrawText(g, "Nada que mostrar", Theme.Body, ClientRectangle, Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }
}
