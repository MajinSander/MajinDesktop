using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace MajinDesktop;

static class Theme
{
    public static readonly Color Bg = Color.FromArgb(0x31, 0x33, 0x38);
    public static readonly Color Panel = Color.FromArgb(0x2B, 0x2D, 0x31);
    public static readonly Color Deep = Color.FromArgb(0x1E, 0x1F, 0x22);
    public static readonly Color Menu = Color.FromArgb(0x11, 0x12, 0x14);
    public static readonly Color Hover = Color.FromArgb(0x40, 0x42, 0x49);
    public static readonly Color Line = Color.FromArgb(0x3F, 0x41, 0x47);
    public static readonly Color Text = Color.FromArgb(0xDB, 0xDE, 0xE1);
    public static readonly Color Bright = Color.FromArgb(0xF2, 0xF3, 0xF5);
    public static readonly Color Muted = Color.FromArgb(0x94, 0x9B, 0xA4);
    public static readonly Color Accent = Color.FromArgb(0x58, 0x65, 0xF2);
    public static readonly Color AccentDark = Color.FromArgb(0x47, 0x52, 0xC4);
    public static readonly Color Green = Color.FromArgb(0x23, 0xA5, 0x5A);
    public static readonly Color Red = Color.FromArgb(0xF2, 0x3F, 0x43);
    public static readonly Color Monitor = Color.FromArgb(0x4E, 0x50, 0x58);

    public static readonly Font Body = new("Segoe UI", 9.75f);
    public static readonly Font Small = new("Segoe UI", 8.25f);
    public static readonly Font Bold = new("Segoe UI Semibold", 9.75f);
    public static readonly Font Title = new("Segoe UI Semibold", 15f);

    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0.5f) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // Mini-vista de la disposición: la principal en azul, el resto en gris.
    public static void DrawLayout(Graphics g, Rectangle area, IList<MonitorState> mons, bool labels)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        if (mons.Count == 0)
        {
            using var pen = new Pen(Line, 1.5f) { DashStyle = DashStyle.Dash };
            using var path = Rounded(RectangleF.Inflate(area, -3, -3), 6);
            g.DrawPath(pen, path);
            TextRenderer.DrawText(g, "vacío", Small, area, Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        int minX = mons.Min(m => m.X), minY = mons.Min(m => m.Y);
        int maxX = mons.Max(m => m.X + m.Width), maxY = mons.Max(m => m.Y + m.Height);
        float scale = Math.Min((area.Width - 6f) / (maxX - minX), (area.Height - 6f) / (maxY - minY));
        float ox = area.X + (area.Width - (maxX - minX) * scale) / 2;
        float oy = area.Y + (area.Height - (maxY - minY) * scale) / 2;
        float gap = Math.Max(1.5f, scale * 24);

        foreach (var m in mons)
        {
            var r = new RectangleF(ox + (m.X - minX) * scale + gap / 2, oy + (m.Y - minY) * scale + gap / 2,
                m.Width * scale - gap, m.Height * scale - gap);
            using var path = Rounded(r, Math.Max(2, Math.Min(r.Width, r.Height) * 0.08f));
            using var fill = new SolidBrush(m.Primary ? Accent : Monitor);
            g.FillPath(fill, path);
            if (labels && r.Width > 50 && r.Height > 30)
            {
                var text = $"{m.Name}\n{m.Width}×{m.Height}";
                TextRenderer.DrawText(g, text, Small, Rectangle.Round(r), Bright,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
            }
        }
    }

    // Icono: dos pantallas verticales a los lados de una horizontal, como tu escritorio.
    public static Bitmap IconBitmap(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = size / 32f;
        using var back = Rounded(new RectangleF(0, 0, size, size), 7 * s);
        using (var bg = new SolidBrush(Accent)) g.FillPath(bg, back);
        using var white = new SolidBrush(Color.White);
        using var soft = new SolidBrush(Color.FromArgb(200, 255, 255, 255));
        using (var p = Rounded(new RectangleF(3.5f * s, 8 * s, 5.5f * s, 16 * s), 1.4f * s)) g.FillPath(soft, p);
        using (var p = Rounded(new RectangleF(10.5f * s, 10 * s, 11 * s, 8 * s), 1.4f * s)) g.FillPath(white, p);
        using (var p = Rounded(new RectangleF(23 * s, 8 * s, 5.5f * s, 16 * s), 1.4f * s)) g.FillPath(soft, p);
        g.FillRectangle(white, 14.5f * s, 18 * s, 3 * s, 3 * s);
        g.FillRectangle(white, 12.5f * s, 21 * s, 7 * s, 1.6f * s);
        return bmp;
    }

    static Icon? appIcon;
    public static Icon AppIcon => appIcon ??= Icon.FromHandle(IconBitmap(SystemInformation.SmallIconSize.Width).GetHicon());

    // .ico multi-tamaño para el ejecutable.
    public static void WriteIco(string path) => Ico.Write(path, IconBitmap);
}

// .ico de varios tamaños con entradas PNG.
static class Ico
{
    public static void Write(string path, Func<int, Bitmap> render)
    {
        int[] sizes = { 16, 20, 24, 32, 48, 64, 128, 256 };
        var pngs = sizes.Select(sz =>
        {
            using var b = render(sz);
            using var ms = new MemoryStream();
            b.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }).ToArray();
        using var w = new BinaryWriter(File.Create(path));
        w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)0); w.Write((byte)0);
            w.Write((short)1); w.Write((short)32);
            w.Write(pngs[i].Length); w.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var p in pngs) w.Write(p);
    }
}

sealed class DarkMenuColors : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground => Theme.Menu;
    public override Color ImageMarginGradientBegin => Theme.Menu;
    public override Color ImageMarginGradientMiddle => Theme.Menu;
    public override Color ImageMarginGradientEnd => Theme.Menu;
    public override Color MenuBorder => Theme.Deep;
    public override Color MenuItemBorder => Theme.AccentDark;
    public override Color MenuItemSelected => Theme.AccentDark;
    public override Color MenuItemSelectedGradientBegin => Theme.AccentDark;
    public override Color MenuItemSelectedGradientEnd => Theme.AccentDark;
    public override Color MenuItemPressedGradientBegin => Theme.AccentDark;
    public override Color MenuItemPressedGradientEnd => Theme.AccentDark;
    public override Color SeparatorDark => Theme.Line;
    public override Color SeparatorLight => Theme.Menu;
}

sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new DarkMenuColors()) { RoundedEdges = false; }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = !e.Item.Enabled ? Theme.Muted : e.Item.Selected ? Color.White : Theme.Text;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = e.Item?.Selected == true ? Color.White : Theme.Muted;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = e.ImageRectangle;
        float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, u = r.Height / 16f;
        using var pen = new Pen(e.Item.Selected ? Color.White : Theme.Green, 2f * u) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLines(pen, new[] { new PointF(cx - 5 * u, cy), new PointF(cx - 1.5f * u, cy + 3.5f * u), new PointF(cx + 5 * u, cy - 4 * u) });
    }
}
