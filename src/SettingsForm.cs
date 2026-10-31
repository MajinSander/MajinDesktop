using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MajinDesktop;

sealed class SettingsForm : Form
{
    readonly TrayApp app;
    readonly LayoutView currentView = new(true);
    readonly Label currentList = new() { AutoSize = false, ForeColor = Theme.Text, Font = Theme.Body };
    readonly SlotRow[] rows = new SlotRow[Config.SlotCount];
    readonly HotkeyBox cycleBox = new();
    readonly HotkeyBox undoBox = new();
    readonly Toggle autoStart = new("Iniciar con Windows");
    readonly Label warn = new() { AutoSize = false, ForeColor = Theme.Red, Font = Theme.Small };
    readonly ToolTip tips = new() { OwnerDraw = true, InitialDelay = 300 };

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

    public SettingsForm(TrayApp app)
    {
        this.app = app;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "MajinDesktop";
        Icon = Theme.AppIcon;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(800, 852);

        tips.Draw += (_, e) =>
        {
            e.Graphics.Clear(Theme.Menu);
            TextRenderer.DrawText(e.Graphics, e.ToolTipText, Theme.Body, e.Bounds, Theme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
        };

        Add(new Label { Text = "MajinDesktop", Font = Theme.Title, ForeColor = Theme.Bright, AutoSize = true }, 72, 14);
        Add(new Label
        {
            Text = "Guarda tus pantallas y los iconos del escritorio en estados\ny cambia entre ellos con un atajo.",
            ForeColor = Theme.Muted, AutoSize = true,
        }, 74, 46);

        // --- Disposición actual ---
        var now = new Card(Theme.Panel);
        now.SetBounds(20, 80, 760, 150);
        now.Controls.Add(Caption("AHORA MISMO", 16, 12));
        currentView.SetBounds(16, 34, 300, 100);
        currentList.SetBounds(336, 36, 410, 100);
        now.Controls.Add(currentView);
        now.Controls.Add(currentList);
        Controls.Add(now);

        // --- Slots ---
        Add(Caption("ESTADOS", 20, 246), 20, 246);
        var hint = new Label
        {
            Text = "Clic en un atajo y pulsa la combinación · Supr lo quita · Esc cancela",
            ForeColor = Theme.Muted, Font = Theme.Small, AutoSize = false, TextAlign = ContentAlignment.TopRight,
        };
        hint.SetBounds(300, 246, 480, 18);
        Controls.Add(hint);
        for (int i = 0; i < Config.SlotCount; i++)
        {
            rows[i] = new SlotRow(this, i);
            rows[i].SetBounds(20, 270 + i * 58, 760, 52);
            Controls.Add(rows[i]);
        }

        // --- Pie ---
        Add(new Label { Text = "Siguiente", AutoSize = true, ForeColor = Theme.Text }, 20, 807);
        var cycleField = new Field(cycleBox);
        cycleField.SetBounds(92, 800, 170, 32);
        Controls.Add(cycleField);
        WireHotkey(cycleBox, TrayApp.CycleId);
        cycleBox.Value = app.Config.Cycle;

        Add(new Label { Text = "Deshacer", AutoSize = true, ForeColor = Theme.Text }, 286, 807);
        var undoField = new Field(undoBox);
        undoField.SetBounds(356, 800, 190, 32);
        Controls.Add(undoField);
        WireHotkey(undoBox, TrayApp.UndoId);
        undoBox.Value = app.Config.Undo;
        tips.SetToolTip(undoBox, "Vuelve a como estaban las pantallas antes del último cambio");

        autoStart.SetBounds(574, 800, 206, 32);
        autoStart.Toggled += app.ToggleAutoStart;
        Controls.Add(autoStart);

        var folder = new LinkLabel
        {
            Text = "Abrir carpeta de datos", AutoSize = false, TextAlign = ContentAlignment.TopRight,
            LinkColor = Theme.Muted, ActiveLinkColor = Theme.Bright, LinkBehavior = LinkBehavior.HoverUnderline,
        };
        folder.SetBounds(580, 20, 200, 24);
        var organize = new PillButton("Organizar escritorio…", PillButton.Kind.Primary);
        organize.SetBounds(600, 44, 180, 30);
        organize.Click += (_, _) => app.OpenOrganizer();
        Controls.Add(organize);
        folder.LinkClicked += (_, _) =>
        {
            System.IO.Directory.CreateDirectory(Config.Dir);
            System.Diagnostics.Process.Start("explorer.exe", $"\"{Config.Dir}\"");
        };
        Controls.Add(folder);

        warn.SetBounds(20, 834, 760, 16);
        Controls.Add(warn);
        ResumeLayout(false);

        RefreshSlots();
        RefreshFooter();
        RefreshHotkeyErrors();
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
    }

    void Add(Control c, int x, int y)
    {
        c.Location = new Point(x, y);
        Controls.Add(c);
    }

    static Label Caption(string text, int x, int y) => new()
    {
        Text = text, Font = new Font("Segoe UI Semibold", 8.25f), ForeColor = Theme.Muted,
        AutoSize = true, Location = new Point(x, y),
    };

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.DarkTitleBar(Handle);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        float s = DeviceDpi / 96f;
        using var icon = Theme.IconBitmap((int)(40 * s));
        e.Graphics.DrawImage(icon, 20 * s, 18 * s);
    }

    // Al salir de la ventana se vuelven a activar los atajos, por si una caja quedó armada.
    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (ActiveControl is HotkeyBox hb) hb.Disarm();
        else if (ActiveControl is Field { Box: HotkeyBox fb }) fb.Disarm();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        app.TrySave();
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        tips.Dispose();
        base.OnFormClosed(e);
    }

    void OnDisplayChanged(object? sender, EventArgs e)
    {
        if (IsHandleCreated && !IsDisposed) BeginInvoke(new Action(RefreshSlots));
    }

    // ---------- Refresco ----------

    public void RefreshSlots()
    {
        List<MonitorState> now;
        try { now = Displays.Snapshot(); }
        catch (Win32Exception) { now = new(); }
        currentView.Monitors = now;
        int icons = -1;
        try { icons = Icons.Count(); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidCastException) { }
        currentList.Text = string.Join("\n", now.Select(m =>
            $"{m.Name}  ·  {m.Width}×{m.Height}  ·  {m.RotationText}{(m.Primary ? "  ·  principal" : "")}"))
            + (icons >= 0 ? $"\n\n{icons} iconos en el escritorio" : "");
        for (int i = 0; i < rows.Length; i++)
            rows[i].Update(app.Config.Slots[i], Displays.Matches(now, app.Config.Slots[i].Monitors));
    }

    public void RefreshFooter() => autoStart.Checked = Config.AutoStart;

    void RefreshHotkeyErrors()
    {
        var bad = new List<string>();
        for (int i = 0; i < rows.Length; i++)
        {
            bool failed = app.FailedHotkeys.Contains(i);
            rows[i].Hotkey.SetError(failed);
            if (failed) bad.Add(app.Config.Slots[i].Hotkey.ToString());
        }
        bool cycleFailed = app.FailedHotkeys.Contains(TrayApp.CycleId);
        cycleBox.SetError(cycleFailed);
        if (cycleFailed) bad.Add(app.Config.Cycle.ToString());
        bool undoFailed = app.FailedHotkeys.Contains(TrayApp.UndoId);
        undoBox.SetError(undoFailed);
        if (undoFailed) bad.Add(app.Config.Undo.ToString());
        warn.Text = bad.Count == 0 ? "" : $"Otra aplicación ya usa: {string.Join(", ", bad)}. Elige otra combinación.";
    }

    // ---------- Atajos ----------

    void WireHotkey(HotkeyBox box, int id)
    {
        box.Armed += app.SuspendHotkeys;
        box.Disarmed += () => { app.RegisterHotkeys(); RefreshHotkeyErrors(); };
        box.Captured += () => OnCaptured(box.Value, id);
    }

    void OnCaptured(Hotkey hk, int id)
    {
        var cfg = app.Config;
        // Una combinación solo puede tener un dueño: si ya la usaba otro estado, se le quita.
        for (int i = 0; i < Config.SlotCount; i++)
        {
            if (i == id || !cfg.Slots[i].Hotkey.SameAs(hk)) continue;
            cfg.Slots[i].Hotkey = new Hotkey();
            rows[i].Hotkey.Value = cfg.Slots[i].Hotkey;
        }
        if (id != TrayApp.CycleId && cfg.Cycle.SameAs(hk))
        {
            cfg.Cycle = new Hotkey();
            cycleBox.Value = cfg.Cycle;
        }
        if (id != TrayApp.UndoId && cfg.Undo.SameAs(hk))
        {
            cfg.Undo = new Hotkey();
            undoBox.Value = cfg.Undo;
        }
        if (id == TrayApp.CycleId) cfg.Cycle = hk;
        else if (id == TrayApp.UndoId) cfg.Undo = hk;
        else cfg.Slots[id].Hotkey = hk;
        app.TrySave();
        app.RegisterHotkeys();
        RefreshHotkeyErrors();
    }

    // ---------- Fila de un estado ----------

    sealed class SlotRow : Card
    {
        readonly SettingsForm form;
        readonly int index;
        readonly LayoutView preview = new(false);
        readonly TextBox name = new() { MaxLength = 32 };
        public readonly HotkeyBox Hotkey = new();
        readonly PillButton save = new("Guardar actual", PillButton.Kind.Secondary);
        readonly PillButton apply = new("Aplicar", PillButton.Kind.Primary);
        readonly PillButton clear = new("✕", PillButton.Kind.Ghost);
        readonly PartToggle useScreens = new(PartToggle.Glyph.Screens);
        readonly PartToggle useIcons = new(PartToggle.Glyph.Icons);
        bool active;

        public SlotRow(SettingsForm form, int index) : base(Theme.Panel)
        {
            this.form = form;
            this.index = index;
            var app = form.app;

            preview.SetBounds(54, 8, 84, 36);
            var nameField = new Field(name);
            nameField.SetBounds(150, 10, 150, 32);
            useScreens.SetBounds(306, 10, 30, 32);
            useIcons.SetBounds(340, 10, 30, 32);
            var hotkeyField = new Field(Hotkey);
            hotkeyField.SetBounds(378, 10, 136, 32);
            save.SetBounds(524, 10, 106, 32);
            apply.SetBounds(638, 10, 72, 32);
            clear.SetBounds(716, 10, 32, 32);
            Controls.AddRange(new Control[] { preview, nameField, useScreens, useIcons, hotkeyField, save, apply, clear });

            name.Text = app.Config.Slots[index].Name;
            name.HandleCreated += (_, _) => SendMessage(name.Handle, 0x1501, (IntPtr)1, $"Estado {index + 1}"); // EM_SETCUEBANNER
            name.TextChanged += (_, _) => app.Config.Slots[index].Name = name.Text.Trim();
            name.Leave += (_, _) => app.TrySave();
            name.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; form.ActiveControl = null; app.TrySave(); } };

            Hotkey.Value = app.Config.Slots[index].Hotkey;
            form.WireHotkey(Hotkey, index);

            save.Click += (_, _) => app.SaveSlot(index);
            apply.Click += (_, _) => app.ApplySlot(index);
            clear.Click += (_, _) => app.ClearSlot(index);
            useScreens.Toggled += () => Toggle(s => s.UseDisplays = !s.UseDisplays);
            useIcons.Toggled += () => Toggle(s => s.UseIcons = !s.UseIcons);
            form.tips.SetToolTip(save, "Guarda aquí cómo están ahora tus pantallas y los iconos del escritorio");
            form.tips.SetToolTip(clear, "Vaciar este estado");
        }

        void Toggle(Action<Slot> change)
        {
            change(form.app.Config.Slots[index]);
            form.app.TrySave();
            Update(form.app.Config.Slots[index], active);
        }

        public void Update(Slot slot, bool isActive)
        {
            active = isActive;
            preview.Monitors = slot.Monitors;
            apply.Enabled = clear.Enabled = !slot.IsEmpty;
            useScreens.Enabled = slot.Monitors.Count > 0;
            useScreens.Checked = slot.UseDisplays;
            useIcons.Enabled = slot.Icons.Count > 0;
            useIcons.Checked = slot.UseIcons;
            form.tips.SetToolTip(useScreens, slot.Monitors.Count == 0 ? "Sin pantallas guardadas"
                : (slot.UseDisplays ? "Aplica las pantallas (clic para no aplicarlas)" : "No aplica las pantallas (clic para aplicarlas)"));
            form.tips.SetToolTip(useIcons, slot.Icons.Count == 0 ? "Sin iconos guardados"
                : (slot.UseIcons ? $"Coloca {slot.Icons.Count} iconos (clic para no colocarlos)" : $"No coloca los {slot.Icons.Count} iconos (clic para colocarlos)"));
            form.tips.SetToolTip(preview, slot.IsEmpty ? "Vacío"
                : string.Join("\n", slot.Monitors.Select(m => $"{m.Name}: {m.RotationText}, en ({m.X}, {m.Y})"))
                  + (slot.Icons.Count > 0 ? $"\n{slot.Icons.Count} iconos" : ""));
            Border = isActive ? Theme.Green : null;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            float s = S;
            var r = new RectangleF(14 * s, 12 * s, 28 * s, 28 * s);
            using (var b = new SolidBrush(active ? Theme.Green : Theme.Deep)) g.FillEllipse(b, r);
            TextRenderer.DrawText(g, (index + 1).ToString(), Theme.Bold, Rectangle.Round(r), Theme.Bright,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}
