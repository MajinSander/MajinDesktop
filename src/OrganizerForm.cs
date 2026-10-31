using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MajinDesktop;

// Organizador visual: tu escritorio a escala para mover, alinear y ordenar iconos y repartirlos en zonas.
// Nada se toca en el escritorio real hasta pulsar "Aplicar al escritorio".
sealed class OrganizerForm : Form
{
    readonly TrayApp app;
    readonly DesktopCanvas canvas = new() { Dock = DockStyle.Fill };
    readonly Label subtitle = new() { AutoSize = true, ForeColor = Theme.Muted };
    readonly Label selection = new() { AutoSize = false, ForeColor = Theme.Text };
    readonly PillButton apply = new("Aplicar al escritorio", PillButton.Kind.Primary);
    readonly Panel zoneEditor = new() { BackColor = Theme.Panel };
    readonly TextBox zoneName = new() { MaxLength = 40 };
    readonly TextBox zoneWords = new() { MaxLength = 80 };
    readonly List<(Chip chip, ItemKind kind)> kindChips = new();
    readonly FlowLayoutPanel monitorButtons = new() { BackColor = Theme.Panel, FlowDirection = FlowDirection.TopDown, WrapContents = false };
    readonly Stack<(Dictionary<OrgItem, Point> items, List<Zone> zones)> undo = new();
    bool loadingZone;

    public OrganizerForm(TrayApp app)
    {
        this.app = app;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Organizar escritorio · MajinDesktop";
        Icon = Theme.AppIcon;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1440, 860);
        MinimumSize = new Size(1000, 680);
        KeyPreview = true;

        Controls.Add(canvas);
        Controls.Add(BuildSidePanel());
        Controls.Add(BuildHeader());

        canvas.Snap = true;
        canvas.FromRight = app.Config.FillFromRight;
        canvas.Zones = app.Config.Zones;
        canvas.BeforeChange += PushUndo;
        canvas.Changed += Refresh2;
        canvas.SelectionChanged += Refresh2;
        canvas.ZoneDrawn += OnZoneDrawn;
        ResumeLayout(false);
        Reload();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.DarkTitleBar(Handle);
    }

    protected override void OnShown(EventArgs e) { base.OnShown(e); canvas.Fit(); }

    // ---------- Construcción ----------

    Control BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 64, BackColor = Theme.Bg };
        header.Controls.Add(new Label { Text = "Organizar escritorio", Font = Theme.Title, ForeColor = Theme.Bright, AutoSize = true, Location = new Point(20, 8) });
        subtitle.Location = new Point(22, 38);
        header.Controls.Add(subtitle);
        var reload = new PillButton("Recargar", PillButton.Kind.Secondary) { Anchor = AnchorStyles.Top | AnchorStyles.Right };
        reload.SetBounds(1440 - 20 - 180 - 8 - 110, 16, 110, 34);
        reload.Click += (_, _) => { if (ConfirmDiscard()) Reload(); };
        apply.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        apply.SetBounds(1440 - 20 - 180, 16, 180, 34);
        apply.Click += (_, _) => Apply();
        header.Controls.Add(reload);
        header.Controls.Add(apply);
        return header;
    }

    Control BuildSidePanel()
    {
        var side = new Panel { Dock = DockStyle.Right, Width = 300, BackColor = Theme.Panel };
        int y = 14;
        Label Caption(string t) { var l = new Label { Text = t, Font = new Font("Segoe UI Semibold", 8.25f), ForeColor = Theme.Muted, AutoSize = true, Location = new Point(18, y) }; side.Controls.Add(l); y += 22; return l; }
        PillButton Button(string t, Action a, PillButton.Kind kind = PillButton.Kind.Secondary)
        {
            var b = new PillButton(t, kind);
            b.SetBounds(18, y, 264, 30);
            b.Click += (_, _) => a();
            side.Controls.Add(b);
            y += 36;
            return b;
        }
        // Dos botones en la misma fila.
        void Pair(string t1, Action a1, string t2, Action a2)
        {
            var b1 = new PillButton(t1, PillButton.Kind.Secondary);
            b1.SetBounds(18, y, 129, 30);
            b1.Click += (_, _) => a1();
            var b2 = new PillButton(t2, PillButton.Kind.Secondary);
            b2.SetBounds(153, y, 129, 30);
            b2.Click += (_, _) => a2();
            side.Controls.Add(b1);
            side.Controls.Add(b2);
            y += 36;
        }

        Caption("SELECCIÓN");
        selection.SetBounds(18, y, 264, 20);
        side.Controls.Add(selection);
        y += 26;
        Pair("Todo  (Ctrl+A)", SelectAll, "A la rejilla", SnapSelection);
        Pair("Por nombre", () => SortScope(Organizer.ByName), "Por tipo", () => SortScope(Organizer.ByKind));
        Button("Compactar huecos", () => SortScope((a, b) => Organizer.ByPosition(a, b, canvas.Cell, canvas.FromRight)));
        side.Controls.Add(new Label { Text = "Mover la selección a:", ForeColor = Theme.Muted, AutoSize = true, Location = new Point(18, y) });
        y += 22;
        monitorButtons.SetBounds(18, y, 264, 3 * 36);
        side.Controls.Add(monitorButtons);
        y += 3 * 36 + 2;

        var snap = new Toggle("Imán a la rejilla") { Checked = true };
        snap.SetBounds(18, y, 264, 28);
        snap.Toggled += () => { snap.Checked = !snap.Checked; canvas.Snap = snap.Checked; };
        side.Controls.Add(snap);
        y += 32;
        var fromRight = new Toggle("Llenar desde la derecha") { Checked = app.Config.FillFromRight };
        fromRight.SetBounds(18, y, 264, 28);
        fromRight.Toggled += () =>
        {
            fromRight.Checked = !fromRight.Checked;
            canvas.FromRight = app.Config.FillFromRight = fromRight.Checked;
            app.TrySave();
        };
        side.Controls.Add(fromRight);
        y += 38;

        Caption("ZONAS");
        Pair("Nueva zona", () => { canvas.DrawZoneMode = true; canvas.Focus(); canvas.Invalidate(); }, "Borrar zona", DeleteZone);
        Button("Ordenar en zonas", SortIntoZones, PillButton.Kind.Primary);
        var auto = new Toggle("Iconos nuevos a su zona") { Checked = app.Config.AutoPlaceNew };
        auto.SetBounds(18, y, 264, 28);
        auto.Toggled += () =>
        {
            auto.Checked = !auto.Checked;
            app.Config.AutoPlaceNew = auto.Checked;
            app.TrySave();
            app.UpdateDesktopWatcher();
        };
        new ToolTip().SetToolTip(auto, "Cuando aparece un acceso directo nuevo en el escritorio (por ejemplo, al instalar un juego), MajinDesktop lo coloca en la zona que le toca");
        side.Controls.Add(auto);
        y += 34;

        zoneEditor.SetBounds(0, y, 300, 300);
        BuildZoneEditor();
        side.Controls.Add(zoneEditor);
        return side;
    }

    void BuildZoneEditor()
    {
        int y = 0;
        zoneEditor.Controls.Add(new Label { Text = "ZONA SELECCIONADA", Font = new Font("Segoe UI Semibold", 8.25f), ForeColor = Theme.Muted, AutoSize = true, Location = new Point(18, y) });
        y += 22;
        var nameField = new Field(zoneName);
        nameField.SetBounds(18, y, 264, 32);
        SendCue(zoneName, "Nombre (p. ej. Juegos de Steam)");
        zoneName.TextChanged += (_, _) => EditZone(z => z.Name = zoneName.Text.Trim());
        zoneEditor.Controls.Add(nameField);
        y += 40;
        zoneEditor.Controls.Add(new Label { Text = "Qué va dentro:", ForeColor = Theme.Muted, AutoSize = true, Location = new Point(18, y) });
        y += 22;
        var flow = new FlowLayoutPanel { BackColor = Theme.Panel, Location = new Point(18, y), Size = new Size(274, 134) };
        foreach (var (text, kind) in new[]
        {
            ("Cualquier juego", ItemKind.Game), ("Steam", ItemKind.Steam), ("Epic", ItemKind.Epic), ("GOG", ItemKind.Gog),
            ("EA", ItemKind.Ea), ("Ubisoft", ItemKind.Ubisoft), ("Battle.net", ItemKind.Battlenet), ("Programas", ItemKind.App),
            ("Carpetas", ItemKind.Folder), ("Archivos", ItemKind.File), ("Web", ItemKind.Web), ("Sistema", ItemKind.System), ("Rotos", ItemKind.Broken),
        })
        {
            var chip = new Chip(text);
            var k = kind;
            chip.Toggled += () => EditZone(z => { if (chip.Checked) { if (!z.Kinds.Contains(k)) z.Kinds.Add(k); } else z.Kinds.Remove(k); });
            kindChips.Add((chip, kind));
            flow.Controls.Add(chip);
        }
        zoneEditor.Controls.Add(flow);
        y += 134;
        var wordsField = new Field(zoneWords);
        wordsField.SetBounds(18, y, 264, 32);
        SendCue(zoneWords, "Nombre contiene… (separa con comas)");
        zoneWords.TextChanged += (_, _) => EditZone(z => z.NameFilter = zoneWords.Text);
        zoneEditor.Controls.Add(wordsField);
    }

    void DeleteZone()
    {
        if (canvas.SelectedZone == null) { subtitle.Text = "Selecciona primero una zona (clic en su etiqueta)"; return; }
        PushUndo();
        app.Config.Zones.Remove(canvas.SelectedZone);
        canvas.SelectZone(null);
        app.TrySave();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

    static void SendCue(TextBox box, string text) =>
        box.HandleCreated += (_, _) => SendMessage(box.Handle, 0x1501, (IntPtr)1, text);

    // ---------- Datos ----------

    void Reload()
    {
        try
        {
            var mons = Organizer.Monitors(Displays.Snapshot());
            var read = Icons.Read(out var cell);
            canvas.Monitors = mons;
            canvas.Cell = cell;
            canvas.Items = read.Select(r => new OrgItem
            {
                Key = r.Key, Name = r.Name, Kind = Shortcuts.Inspect(r.Key).Kind, Pos = r.Pos, Original = r.Pos,
            }).ToList();
            undo.Clear();
            monitorButtons.Controls.Clear();
            foreach (var m in mons.OrderBy(m => m.Bounds.X))
            {
                var target = m;
                var b = new PillButton(m.State.Name + (m.State.Primary ? "  (principal)" : ""), PillButton.Kind.Secondary) { Size = new Size(264, 30), Margin = new Padding(0, 0, 0, 6) };
                b.Click += (_, _) => MoveSelectionTo(target);
                monitorButtons.Controls.Add(b);
            }
            canvas.Fit();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Win32Exception)
        {
            Log.Error("Organizador: leer el escritorio", ex);
            subtitle.Text = "No se pudo leer el escritorio: " + ex.Message;
        }
        Refresh2();
    }

    void Refresh2()
    {
        int moved = canvas.Items.Count(i => i.Moved);
        int sel = canvas.Items.Count(i => i.Selected);
        subtitle.Text = $"{canvas.Items.Count} iconos  ·  " + (moved == 0
            ? "sin cambios: lo que muevas aquí no toca el escritorio hasta que pulses Aplicar"
            : $"{moved} iconos movidos (punto verde), pendientes de aplicar  ·  Ctrl+Z deshace");
        selection.Text = sel == 0 ? "Nada seleccionado: las órdenes afectan a todos" : $"{sel} iconos seleccionados";
        apply.Enabled = moved > 0;

        var z = canvas.SelectedZone;
        zoneEditor.Visible = z != null;
        if (z != null)
        {
            loadingZone = true;
            zoneName.Text = z.Name;
            zoneWords.Text = z.NameFilter;
            foreach (var (chip, kind) in kindChips) chip.Checked = z.Kinds.Contains(kind);
            loadingZone = false;
        }
        canvas.Invalidate();
    }

    void EditZone(Action<Zone> change)
    {
        if (loadingZone || canvas.SelectedZone == null) return;
        change(canvas.SelectedZone);
        app.TrySave();
        canvas.Invalidate();
    }

    void OnZoneDrawn(Rectangle world)
    {
        PushUndo();
        var z = new Zone { Name = $"Zona {app.Config.Zones.Count + 1}" };
        canvas.PlaceZone(z, world);
        app.Config.Zones.Add(z);
        app.TrySave();
        canvas.SelectZone(z);
        zoneName.Focus();
        zoneName.SelectAll();
    }

    // ---------- Deshacer ----------

    void PushUndo()
    {
        var zones = app.Config.Zones.Select(z => new Zone
        {
            Name = z.Name, Monitor = z.Monitor, Edid = z.Edid, X = z.X, Y = z.Y, Width = z.Width, Height = z.Height,
            Kinds = z.Kinds.ToList(), NameFilter = z.NameFilter,
        }).ToList();
        undo.Push((canvas.Items.ToDictionary(i => i, i => i.Pos), zones));
    }

    void Undo()
    {
        if (undo.Count == 0) return;
        var (items, zones) = undo.Pop();
        foreach (var kv in items) kv.Key.Pos = kv.Value;
        app.Config.Zones.Clear();
        app.Config.Zones.AddRange(zones);
        canvas.SelectZone(null);
        app.TrySave();
        Refresh2();
    }

    // ---------- Órdenes ----------

    List<OrgItem> Scope() { var sel = canvas.Selected.ToList(); return sel.Count > 0 ? sel : canvas.Items; }

    void SelectAll()
    {
        foreach (var i in canvas.Items) i.Selected = true;
        Refresh2();
    }

    void SnapSelection()
    {
        PushUndo();
        foreach (var group in Scope().GroupBy(i => Organizer.MonitorAt(Center(i), canvas.Monitors)))
        {
            if (group.Key == null) continue;
            var area = group.Key.WorkArea;
            var taken = new HashSet<Point>(canvas.Items.Except(group).Where(i => area.Contains(Center(i))).Select(i => Organizer.Snap(i.Pos, area, canvas.Cell, canvas.FromRight)));
            var cells = Organizer.Cells(area, canvas.Cell, canvas.FromRight).ToList();
            foreach (var it in group.OrderBy(i => i.Pos.X).ThenBy(i => i.Pos.Y))
            {
                var want = Organizer.Snap(it.Pos, area, canvas.Cell, canvas.FromRight);
                if (taken.Contains(want))
                    want = cells.Where(c => !taken.Contains(c)).OrderBy(c => Sq(c.X - it.Pos.X) + Sq(c.Y - it.Pos.Y)).DefaultIfEmpty(want).First();
                it.Pos = want;
                taken.Add(want);
            }
        }
        Refresh2();
    }

    // Ordena el ámbito dentro de cada pantalla: los iconos que no entran en la orden no se mueven.
    void SortScope(Comparison<OrgItem> order)
    {
        PushUndo();
        int overflow = 0;
        foreach (var group in Scope().GroupBy(i => Organizer.MonitorAt(Center(i), canvas.Monitors)))
        {
            if (group.Key == null) continue;
            var area = group.Key.WorkArea;
            var list = group.ToList();
            list.Sort(order);
            var taken = canvas.Items.Except(list).Where(i => area.Contains(Center(i)))
                .Select(i => Organizer.Snap(i.Pos, area, canvas.Cell, canvas.FromRight)).ToList();
            overflow += Organizer.Arrange(list, area, canvas.Cell, canvas.FromRight, taken);
        }
        Refresh2();
        if (overflow > 0) subtitle.Text += $"  ·  {overflow} no cabían y se han apilado";
    }

    void MoveSelectionTo(OrgMonitor target)
    {
        var sel = canvas.Selected.ToList();
        if (sel.Count == 0) { subtitle.Text = "Selecciona primero los iconos que quieres mover (clic, Ctrl+clic o arrastrando un rectángulo)"; return; }
        PushUndo();
        var area = target.WorkArea;
        sel.Sort((a, b) => Organizer.ByPosition(a, b, canvas.Cell, false));
        var taken = canvas.Items.Except(sel).Where(i => area.Contains(Center(i)))
            .Select(i => Organizer.Snap(i.Pos, area, canvas.Cell, canvas.FromRight)).ToList();
        int overflow = Organizer.Arrange(sel, area, canvas.Cell, canvas.FromRight, taken);
        Refresh2();
        if (overflow > 0) subtitle.Text += $"  ·  {overflow} no cabían y se han apilado";
    }

    void SortIntoZones()
    {
        if (app.Config.Zones.Count == 0) { subtitle.Text = "Primero crea una zona con «Nueva zona» y dile qué va dentro"; return; }
        if (!app.Config.Zones.Any(z => z.HasRule)) { subtitle.Text = "Ninguna zona tiene regla: marca qué va dentro (tipos o palabras del nombre)"; return; }
        PushUndo();
        var (placed, overflow) = Organizer.SortIntoZones(canvas.Items, app.Config.Zones, canvas.Monitors, canvas.Cell);
        Refresh2();
        subtitle.Text += $"  ·  {placed} iconos en zonas" + (overflow > 0 ? $", {overflow} no cabían (haz la zona más grande)" : "");
    }

    Point Center(OrgItem i) => new(i.Pos.X + canvas.Cell.Width / 2, i.Pos.Y + canvas.Cell.Height / 2);
    static long Sq(long v) => v * v;

    // ---------- Aplicar ----------

    void Apply()
    {
        var targets = canvas.Items.Where(i => i.Moved).ToDictionary(i => i.Key, i => i.Pos, StringComparer.OrdinalIgnoreCase);
        if (targets.Count == 0) return;
        if (app.ApplyOrganizer(targets))
        {
            foreach (var i in canvas.Items) i.Original = i.Pos;
            undo.Clear();
            Refresh2();
        }
    }

    bool ConfirmDiscard() =>
        !canvas.Items.Any(i => i.Moved) ||
        DarkDialog.Ask(this, "Cambios sin aplicar", "Hay iconos movidos que aún no se han aplicado al escritorio. ¿Descartarlos?", "Descartar", "Volver");

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing && !ConfirmDiscard()) e.Cancel = true;
        base.OnFormClosing(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (ActiveControl is TextBox && keyData != Keys.Escape) return base.ProcessCmdKey(ref msg, keyData);
        switch (keyData)
        {
            case Keys.Control | Keys.Z: Undo(); return true;
            case Keys.Control | Keys.A: SelectAll(); return true;
            case Keys.Escape:
                canvas.DrawZoneMode = false;
                foreach (var i in canvas.Items) i.Selected = false;
                canvas.SelectZone(null);
                canvas.Focus();
                Refresh2();
                return true;
            case Keys.Left: case Keys.Right: case Keys.Up: case Keys.Down:
                Nudge(keyData);
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // Flechas: mueve la selección una celda.
    void Nudge(Keys key)
    {
        var sel = canvas.Selected.ToList();
        if (sel.Count == 0) return;
        PushUndo();
        int dx = key == Keys.Left ? -canvas.Cell.Width : key == Keys.Right ? canvas.Cell.Width : 0;
        int dy = key == Keys.Up ? -canvas.Cell.Height : key == Keys.Down ? canvas.Cell.Height : 0;
        foreach (var i in sel) i.Pos = new Point(i.Pos.X + dx, i.Pos.Y + dy);
        Refresh2();
    }
}
