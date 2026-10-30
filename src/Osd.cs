using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace MajinDesktop;

// Aviso flotante abajo en el centro de la pantalla principal; no roba el foco ni bloquea el ratón.
sealed class Osd : Form
{
    readonly Timer timer = new() { Interval = 30 };
    string title = "", detail = "";
    IList<MonitorState> mons = Array.Empty<MonitorState>();
    Color accent = Theme.Accent;
    int elapsed;

    public Osd()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Theme.Menu;
        DoubleBuffered = true;
        timer.Tick += (_, _) => Fade();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 | 0x80 | 0x20 | 0x8; // NOACTIVATE | TOOLWINDOW | TRANSPARENT | TOPMOST
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.RoundCorners(Handle);
    }

    float Dpi => DeviceDpi / 96f;

    public void Flash(string title, string detail, IList<MonitorState>? mons, Color accent)
    {
        this.title = title;
        this.detail = detail;
        this.mons = mons ?? Array.Empty<MonitorState>();
        this.accent = accent;

        float s = Dpi;
        int textW = Math.Max(TextRenderer.MeasureText(title, Theme.Bold).Width, TextRenderer.MeasureText(detail, Theme.Body).Width);
        int w = (int)((this.mons.Count > 0 ? 112 : 24) * s) + textW + (int)(28 * s);
        int h = (int)(64 * s);
        // Métricas en vivo: tras cambiar de slot la pantalla principal puede ser otra.
        int cx = Native.GetSystemMetrics(0), cy = Native.GetSystemMetrics(1);
        Bounds = new Rectangle((cx - w) / 2, cy - h - (int)(110 * s), w, h);

        elapsed = 0;
        Opacity = 0.97;
        if (!Visible) Show();
        Invalidate();
        timer.Start();
    }

    void Fade()
    {
        elapsed += timer.Interval;
        if (elapsed < 1600) return;
        Opacity -= 0.12;
        if (Opacity > 0.05) return;
        timer.Stop();
        Hide();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        float s = Dpi;
        var g = e.Graphics;
        using (var bar = new SolidBrush(accent)) g.FillRectangle(bar, 0, 0, 4 * s, Height);

        int x = (int)(18 * s);
        if (mons.Count > 0)
        {
            Theme.DrawLayout(g, new Rectangle(x, (int)(8 * s), (int)(84 * s), Height - (int)(16 * s)), mons, false);
            x += (int)(94 * s);
        }
        TextRenderer.DrawText(g, title, Theme.Bold, new Point(x, (int)(12 * s)), Theme.Bright);
        TextRenderer.DrawText(g, detail, Theme.Body, new Point(x, (int)(33 * s)), Theme.Muted);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) timer.Dispose();
        base.Dispose(disposing);
    }
}
