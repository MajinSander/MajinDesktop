using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MajinDesktop;

// Panel con esquinas redondeadas (las esquinas se pintan con el color del padre).
class Card : Panel
{
    public int Radius = 8;
    public Color? Border;

    public Card(Color fill)
    {
        BackColor = fill;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
    }

    protected float S => DeviceDpi / 96f;

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Theme.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Radius * S);
        using (var b = new SolidBrush(BackColor)) g.FillPath(b, path);
        if (Border is { } c)
            using (var p = new Pen(c, Math.Max(1f, S))) g.DrawPath(p, path);
    }
}

// Contenedor oscuro para un TextBox sin borde, al estilo de los campos de Discord.
sealed class Field : Card
{
    public readonly TextBox Box;

    public Field(TextBox box) : base(Theme.Deep)
    {
        Radius = 4;
        Box = box;
        box.BorderStyle = BorderStyle.None;
        box.BackColor = Theme.Deep;
        box.ForeColor = Theme.Text;
        box.Font = Theme.Body;
        Controls.Add(box);
        Cursor = Cursors.IBeam;
        MouseDown += (_, _) => box.Focus();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        int pad = (int)(9 * S);
        Box.SetBounds(pad, (Height - Box.Height) / 2, Width - pad * 2, Box.Height);
    }
}

sealed class PillButton : Control
{
    public enum Kind { Primary, Secondary, Ghost }

    readonly Kind kind;
    bool hover, down;

    public PillButton(string text, Kind kind)
    {
        Text = text;
        this.kind = kind;
        Font = Theme.Bold;
        Cursor = Cursors.Hand;
        TabStop = false;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Panel);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        bool active = Enabled && (hover || down);
        Color bg = kind switch
        {
            Kind.Primary => !Enabled ? Color.FromArgb(0x3C, 0x45, 0x8F) : active ? Theme.AccentDark : Theme.Accent,
            Kind.Secondary => active ? Color.FromArgb(0x6D, 0x6F, 0x78) : Theme.Monitor,
            _ => active ? Theme.Hover : Parent?.BackColor ?? Theme.Panel,
        };
        Color fg = !Enabled ? Theme.Muted : kind == Kind.Ghost ? (active ? Theme.Red : Theme.Muted) : Color.White;
        using var path = Theme.Rounded(new RectangleF(0, 0, Width - 0.5f, Height - 0.5f), 4 * DeviceDpi / 96f);
        using (var b = new SolidBrush(bg)) g.FillPath(b, path);
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, fg,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }
}

// Interruptor tipo Discord con su texto a la derecha.
sealed class Toggle : Control
{
    bool on;
    public event Action? Toggled;

    public Toggle(string text)
    {
        Text = text;
        Font = Theme.Body;
        Cursor = Cursors.Hand;
        TabStop = false;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
    }

    public bool Checked { get => on; set { on = value; Invalidate(); } }

    protected override void OnClick(EventArgs e) { base.OnClick(e); Toggled?.Invoke(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        float s = DeviceDpi / 96f;
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float w = 40 * s, h = 24 * s, y = (Height - h) / 2;
        using (var path = Theme.Rounded(new RectangleF(0, y, w, h), h / 2))
        using (var b = new SolidBrush(on ? Theme.Green : Color.FromArgb(0x80, 0x84, 0x8E)))
            g.FillPath(b, path);
        float k = h - 6 * s;
        g.FillEllipse(Brushes.White, on ? w - k - 3 * s : 3 * s, y + 3 * s, k, k);
        TextRenderer.DrawText(g, Text, Font, new Rectangle((int)(w + 10 * s), 0, Width, Height), Theme.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }
}

sealed class LayoutView : Control
{
    IList<MonitorState> monitors = Array.Empty<MonitorState>();
    readonly bool labels;

    public LayoutView(bool labels)
    {
        this.labels = labels;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
    }

    public IList<MonitorState> Monitors { get => monitors; set { monitors = value; Invalidate(); } }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Panel);
        Theme.DrawLayout(e.Graphics, ClientRectangle, monitors, labels);
    }
}

// Caja que captura una combinación de teclas. Se "arma" al hacer clic; mientras está armada
// la app suelta sus atajos globales para que Windows deje llegar las teclas aquí.
sealed class HotkeyBox : TextBox
{
    Hotkey value = new();
    bool armed;
    public bool Error;
    public event Action? Armed, Disarmed, Captured;

    [DllImport("user32.dll")] static extern bool HideCaret(IntPtr hWnd);

    public HotkeyBox()
    {
        ReadOnly = true;
        ShortcutsEnabled = false;
        TextAlign = HorizontalAlignment.Center;
        Cursor = Cursors.Hand;
        BackColor = Theme.Deep;
    }

    public Hotkey Value { get => value; set { this.value = value; Render(); } }

    Field? Host => Parent as Field;

    void Render(string? pending = null)
    {
        Text = pending ?? (armed ? "Pulsa la combinación…" : value.IsEmpty ? "Sin atajo" : value.ToString());
        ForeColor = armed ? Theme.Bright : value.IsEmpty ? Theme.Muted : Theme.Text;
        if (Host is { } h)
        {
            h.Border = armed ? Theme.Accent : Error ? Theme.Red : null;
            h.Invalidate();
        }
        if (Focused) HideCaret(Handle);
    }

    public void SetError(bool error) { Error = error; Render(); }

    void Arm()
    {
        if (armed) return;
        armed = true;
        Armed?.Invoke();
        Render();
    }

    public void Disarm()
    {
        if (!armed) return;
        armed = false;
        Render();
        Disarmed?.Invoke();
    }

    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Arm(); }
    protected override void OnEnter(EventArgs e) { base.OnEnter(e); Arm(); }
    protected override void OnLeave(EventArgs e) { Disarm(); base.OnLeave(e); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); HideCaret(Handle); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); SelectionLength = 0; HideCaret(Handle); }

    // Si sueltas los modificadores sin pulsar ninguna tecla, vuelve a "Pulsa la combinación…".
    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (armed && e.Modifiers == 0 && !Down(0x5B) && !Down(0x5C)) Render();
    }

    static bool Down(int vk) => (Native.GetKeyState(vk) & 0x8000) != 0;

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        Keys key = keyData & Keys.KeyCode;
        Keys mods = keyData & Keys.Modifiers;
        bool win = Down(0x5B) || Down(0x5C);
        if (key == Keys.Tab && mods == 0 && !win) return base.ProcessCmdKey(ref msg, keyData);
        if (!armed) return base.ProcessCmdKey(ref msg, keyData);

        var hk = new Hotkey
        {
            Ctrl = (mods & Keys.Control) != 0, Alt = (mods & Keys.Alt) != 0,
            Shift = (mods & Keys.Shift) != 0, Win = win,
        };
        if (key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin)
        {
            Render(hk.Modifiers == 0 ? null : PendingMods(hk) + "…");
            return true;
        }
        if (hk.Modifiers == 0 && key is Keys.Escape) { Disarm(); return true; }
        if (hk.Modifiers == 0 && key is Keys.Back or Keys.Delete)
        {
            value = new Hotkey();
            armed = false;
            Render();
            Captured?.Invoke();
            return true;
        }
        // Sin modificadores solo se aceptan F1–F24 (si no, la tecla dejaría de escribirse en cualquier app).
        if (hk.Modifiers == 0 && !(key >= Keys.F1 && key <= Keys.F24)) return true;

        hk.Key = key;
        value = hk;
        armed = false;
        Render();
        Captured?.Invoke();
        return true;
    }

    static string PendingMods(Hotkey h)
    {
        var parts = new List<string>();
        if (h.Ctrl) parts.Add("Ctrl");
        if (h.Alt) parts.Add("Alt");
        if (h.Shift) parts.Add("Shift");
        if (h.Win) parts.Add("Win");
        return string.Join("+", parts) + "+";
    }
}

// Interruptor pequeño con dibujo (pantalla o iconos) para elegir qué partes aplica un estado.
sealed class PartToggle : Control
{
    public enum Glyph { Screens, Icons }

    readonly Glyph glyph;
    bool on, hover;
    public event Action? Toggled;

    public PartToggle(Glyph glyph)
    {
        this.glyph = glyph;
        Cursor = Cursors.Hand;
        TabStop = false;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
    }

    public bool Checked { get => on; set { on = value; Invalidate(); } }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnClick(EventArgs e) { base.OnClick(e); if (Enabled) Toggled?.Invoke(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        float s = DeviceDpi / 96f;
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Panel);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color bg = !Enabled ? Theme.Deep : on ? (hover ? Theme.AccentDark : Theme.Accent) : (hover ? Theme.Hover : Theme.Deep);
        Color fg = !Enabled ? Theme.Line : on ? Color.White : Theme.Muted;
        using (var path = Theme.Rounded(new RectangleF(0, 0, Width - 0.5f, Height - 0.5f), 4 * s))
        using (var b = new SolidBrush(bg)) g.FillPath(b, path);
        float cx = Width / 2f, cy = Height / 2f;
        using var brush = new SolidBrush(fg);
        using var pen = new Pen(fg, 1.6f * s);
        if (glyph == Glyph.Screens)
        {
            g.DrawRectangle(pen, cx - 7 * s, cy - 6 * s, 14 * s, 9 * s);
            g.FillRectangle(brush, cx - 1 * s, cy + 3 * s, 2 * s, 2.5f * s);
            g.FillRectangle(brush, cx - 4 * s, cy + 5.5f * s, 8 * s, 1.6f * s);
        }
        else
        {
            for (int row = 0; row < 2; row++)
                for (int col = 0; col < 2; col++)
                    g.FillRectangle(brush, cx - 6 * s + col * 7 * s, cy - 6 * s + row * 7 * s, 5 * s, 5 * s);
        }
    }
}
