using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace MajinDesktop;

// Representación del escritorio: pantallas a escala, zonas e iconos. Rueda = zoom, botón derecho o central = mover la vista,
// doble clic en vacío = encajar todo.
sealed class DesktopCanvas : Control
{
    public List<OrgMonitor> Monitors = new();
    public List<OrgItem> Items = new();
    public List<Zone> Zones = new();
    public Size Cell { get; set; } = new(94, 116);
    public bool Snap = true, FromRight;
    public bool DrawZoneMode;

    public Zone? SelectedZone { get; private set; }
    public event Action? SelectionChanged, Changed, BeforeChange;
    public event Action<Rectangle>? ZoneDrawn;

    float scale = 0.2f;
    PointF offset;          // posición en la vista del origen del mundo
    Rectangle world;        // unión de las pantallas
    bool fitted;

    enum Drag { None, Icons, Band, Pan, ZoneMove, ZoneResize, ZoneDraw }
    Drag drag;
    Point dragStartWorld, dragStartView;
    PointF panStart;
    Dictionary<OrgItem, Point> dragOrigins = new();
    Rectangle zoneStartRect;
    Rectangle band;
    bool changedDuringDrag;
    OrgItem? hover;

    public DesktopCanvas()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                 ControlStyles.UserPaint | ControlStyles.Selectable, true);
        BackColor = Theme.Deep;
    }

    public IEnumerable<OrgItem> Selected => Items.Where(i => i.Selected);

    public void SelectZone(Zone? z) { SelectedZone = z; Invalidate(); SelectionChanged?.Invoke(); }

    // ---------- Vista ----------

    public void Fit()
    {
        if (Monitors.Count == 0 || Width < 10 || Height < 10) return;
        world = Monitors.Select(m => m.Bounds).Aggregate(Rectangle.Union);
        float pad = 24 * DeviceDpi / 96f;
        scale = Math.Min((Width - 2 * pad) / world.Width, (Height - 2 * pad) / world.Height);
        offset = new PointF((Width - world.Width * scale) / 2 - world.X * scale, (Height - world.Height * scale) / 2 - world.Y * scale);
        fitted = true;
        Invalidate();
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); if (!fitted || drag == Drag.None) Fit(); }

    PointF V(Point w) => new(offset.X + w.X * scale, offset.Y + w.Y * scale);
    RectangleF V(Rectangle r) => new(offset.X + r.X * scale, offset.Y + r.Y * scale, r.Width * scale, r.Height * scale);
    Point W(Point v) => new((int)Math.Round((v.X - offset.X) / scale), (int)Math.Round((v.Y - offset.Y) / scale));

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        var before = W(e.Location);
        float fit = Math.Min((Width - 48f) / Math.Max(1, world.Width), (Height - 48f) / Math.Max(1, world.Height));
        scale = Math.Max(fit * 0.6f, Math.Min(1.5f, scale * (e.Delta > 0 ? 1.15f : 1 / 1.15f)));
        offset = new PointF(e.X - before.X * scale, e.Y - before.Y * scale);
        Invalidate();
    }

    // ---------- Dibujo ----------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Deep);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        float s = DeviceDpi / 96f;

        foreach (var m in Monitors)
        {
            var r = V(m.Bounds);
            using (var path = Theme.Rounded(r, 6 * s))
            using (var fill = new SolidBrush(Theme.Panel)) g.FillPath(fill, path);
            // Barra de tareas (lo que queda fuera de la zona de trabajo).
            using (var bar = new SolidBrush(Theme.Menu))
            {
                var wa = V(m.WorkArea);
                if (wa.Bottom < r.Bottom - 1) g.FillRectangle(bar, r.X, wa.Bottom, r.Width, r.Bottom - wa.Bottom);
                if (wa.Top > r.Top + 1) g.FillRectangle(bar, r.X, r.Y, r.Width, wa.Top - r.Y);
            }
            // Nombre encima de la pantalla, para no taparse con los iconos de la primera fila.
            string label = m.State.Name + (m.State.Primary ? "  ·  principal" : "");
            int lh = TextRenderer.MeasureText(label, Theme.Small).Height;
            TextRenderer.DrawText(g, label, Theme.Small, new Point((int)(r.X + 2 * s), (int)(r.Y - lh - 3 * s)), Theme.Muted);
        }

        foreach (var z in Zones)
        {
            if (Organizer.ZoneRect(z, Monitors) is not { } wr) continue;
            var r = V(wr);
            bool sel = z == SelectedZone;
            using (var fill = new SolidBrush(Color.FromArgb(sel ? 46 : 26, Theme.Accent))) g.FillRectangle(fill, r);
            using (var pen = new Pen(Color.FromArgb(sel ? 255 : 150, Theme.Accent), (sel ? 2f : 1.2f) * s) { DashStyle = sel ? DashStyle.Solid : DashStyle.Dash })
                g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
            var labelRect = ZoneLabel(z, r);
            using (var lb = new SolidBrush(sel ? Theme.Accent : Color.FromArgb(200, Theme.AccentDark))) g.FillRectangle(lb, labelRect);
            TextRenderer.DrawText(g, LabelText(z), Theme.Small, Rectangle.Round(labelRect), Color.White,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            if (sel) g.FillRectangle(Brushes.White, HandleRect(r));
        }

        float cw = Cell.Width * scale, ch = Cell.Height * scale;
        float img = Math.Min(48 * scale * 1.6f, Math.Min(cw, ch) * 0.62f);
        bool labels = cw >= 56 * s;
        foreach (var it in Items)
        {
            var p = V(it.Pos);
            var cellRect = new RectangleF(p.X, p.Y, cw, ch);
            if (it.Selected)
                using (var path = Theme.Rounded(RectangleF.Inflate(cellRect, -1, -1), 4 * s))
                using (var sel = new SolidBrush(Color.FromArgb(110, Theme.Accent))) g.FillPath(sel, path);
            else if (it == hover)
                using (var path = Theme.Rounded(RectangleF.Inflate(cellRect, -1, -1), 4 * s))
                using (var hv = new SolidBrush(Color.FromArgb(40, 255, 255, 255))) g.FillPath(hv, path);
            var bmp = ShellImages.Get(it.Key);
            var ir = new RectangleF(p.X + (cw - img) / 2, p.Y + Math.Max(1, ch * 0.06f), img, img);
            if (bmp != null) g.DrawImage(bmp, ir);
            else using (var ph = new SolidBrush(Theme.Monitor)) g.FillRectangle(ph, ir);
            if (it.Moved)
                using (var dot = new SolidBrush(Theme.Green)) g.FillEllipse(dot, p.X + cw - 7 * s, p.Y + 2 * s, 5 * s, 5 * s);
            if (labels)
            {
                var tr = new Rectangle((int)p.X, (int)(ir.Bottom + 2), (int)cw, (int)(p.Y + ch - ir.Bottom - 2));
                TextRenderer.DrawText(g, it.Name, Theme.Small, tr, Theme.Bright,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
        }

        if (drag is Drag.Band or Drag.ZoneDraw)
        {
            var r = V(band);
            using var fill = new SolidBrush(Color.FromArgb(40, Theme.Accent));
            using var pen = new Pen(Theme.Accent, s) { DashStyle = drag == Drag.ZoneDraw ? DashStyle.Dash : DashStyle.Solid };
            g.FillRectangle(fill, r);
            g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
        }

        string status = hover != null ? $"{hover.Name}  ·  {Shortcuts.KindName(hover.Kind)}"
            : DrawZoneMode ? "Arrastra sobre una pantalla para dibujar la zona  ·  Esc para cancelar"
            : "Rueda: zoom (para ver los iconos de cerca)  ·  botón derecho: mover la vista  ·  doble clic: encajar  ·  flechas: mover la selección";
        TextRenderer.DrawText(g, status, Theme.Small, new Point((int)(10 * s), Height - (int)(22 * s)), Theme.Muted);
    }

    string LabelText(Zone z) => $" {(z.Name.Length > 0 ? z.Name : "Zona")}  ·  {z.RuleText}";

    RectangleF ZoneLabel(Zone z, RectangleF r)
    {
        var size = TextRenderer.MeasureText(LabelText(z), Theme.Small);
        return new RectangleF(r.X, r.Y - size.Height - 2, Math.Min(Math.Max(r.Width, 60), size.Width + 8), size.Height + 2);
    }

    RectangleF HandleRect(RectangleF r) { float h = 9 * DeviceDpi / 96f; return new RectangleF(r.Right - h / 2, r.Bottom - h / 2, h, h); }

    // ---------- Ratón ----------

    OrgItem? ItemAt(Point world) =>
        Items.LastOrDefault(i => new Rectangle(i.Pos, Cell).Contains(world));

    Zone? ZoneLabelAt(Point view) =>
        Zones.LastOrDefault(z => Organizer.ZoneRect(z, Monitors) is { } wr && ZoneLabel(z, V(wr)).Contains(view));

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        var wpt = W(e.Location);
        dragStartWorld = wpt;
        dragStartView = e.Location;
        changedDuringDrag = false;
        if (e.Button is MouseButtons.Right or MouseButtons.Middle)
        {
            drag = Drag.Pan;
            panStart = offset;
            Cursor = Cursors.SizeAll;
            return;
        }
        if (e.Button != MouseButtons.Left) return;

        if (DrawZoneMode)
        {
            drag = Drag.ZoneDraw;
            band = new Rectangle(wpt, Size.Empty);
            return;
        }
        if (SelectedZone != null && Organizer.ZoneRect(SelectedZone, Monitors) is { } zr && HandleRect(V(zr)).Contains(e.Location))
        {
            drag = Drag.ZoneResize;
            zoneStartRect = zr;
            return;
        }
        if (ZoneLabelAt(e.Location) is { } labelZone)
        {
            SelectZone(labelZone);
            drag = Drag.ZoneMove;
            zoneStartRect = Organizer.ZoneRect(labelZone, Monitors)!.Value;
            return;
        }
        bool ctrl = (ModifierKeys & Keys.Control) != 0;
        var hit = ItemAt(wpt);
        if (hit != null)
        {
            if (ctrl) hit.Selected = !hit.Selected;
            else if (!hit.Selected) { foreach (var i in Items) i.Selected = false; hit.Selected = true; }
            dragOrigins = Selected.ToDictionary(i => i, i => i.Pos);
            drag = Drag.Icons;
            SelectionChanged?.Invoke();
            Invalidate();
            return;
        }
        if (!ctrl) foreach (var i in Items) i.Selected = false;
        var zoneInside = Zones.LastOrDefault(z => Organizer.ZoneRect(z, Monitors) is { } r && r.Contains(wpt));
        SelectedZone = zoneInside;
        drag = Drag.Band;
        band = new Rectangle(wpt, Size.Empty);
        SelectionChanged?.Invoke();
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var wpt = W(e.Location);
        int dx = wpt.X - dragStartWorld.X, dy = wpt.Y - dragStartWorld.Y;
        switch (drag)
        {
            case Drag.Pan:
                offset = new PointF(panStart.X + e.X - dragStartView.X, panStart.Y + e.Y - dragStartView.Y);
                break;
            case Drag.Icons:
                if (!changedDuringDrag && Math.Abs(e.X - dragStartView.X) + Math.Abs(e.Y - dragStartView.Y) < 4) return;
                if (!changedDuringDrag) { BeforeChange?.Invoke(); changedDuringDrag = true; }
                foreach (var kv in dragOrigins)
                {
                    var p = new Point(kv.Value.X + dx, kv.Value.Y + dy);
                    kv.Key.Pos = Snap ? SnapToMonitor(p) : p;
                }
                break;
            case Drag.Band:
            case Drag.ZoneDraw:
                band = Rectangle.FromLTRB(Math.Min(dragStartWorld.X, wpt.X), Math.Min(dragStartWorld.Y, wpt.Y),
                    Math.Max(dragStartWorld.X, wpt.X), Math.Max(dragStartWorld.Y, wpt.Y));
                break;
            case Drag.ZoneMove:
            case Drag.ZoneResize:
                if (SelectedZone == null) break;
                if (!changedDuringDrag) { BeforeChange?.Invoke(); changedDuringDrag = true; }
                var nr = drag == Drag.ZoneMove
                    ? new Rectangle(zoneStartRect.X + dx, zoneStartRect.Y + dy, zoneStartRect.Width, zoneStartRect.Height)
                    : new Rectangle(zoneStartRect.X, zoneStartRect.Y, Math.Max(Cell.Width, zoneStartRect.Width + dx), Math.Max(Cell.Height, zoneStartRect.Height + dy));
                PlaceZone(SelectedZone, nr);
                break;
            default:
                var h = ItemAt(wpt);
                if (h != hover) { hover = h; Invalidate(); }
                Cursor = DrawZoneMode ? Cursors.Cross : h != null ? Cursors.Hand : Cursors.Default;
                return;
        }
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var was = drag;
        drag = Drag.None;
        Cursor = Cursors.Default;
        if (was == Drag.Band)
        {
            foreach (var i in Items)
                if (band.Contains(new Point(i.Pos.X + Cell.Width / 2, i.Pos.Y + Cell.Height / 2))) i.Selected = true;
            SelectionChanged?.Invoke();
        }
        else if (was == Drag.ZoneDraw)
        {
            DrawZoneMode = false;
            if (band.Width >= Cell.Width && band.Height >= Cell.Height) ZoneDrawn?.Invoke(band);
        }
        else if (changedDuringDrag) Changed?.Invoke();
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = null; Invalidate(); }

    protected override void OnDoubleClick(EventArgs e)
    {
        base.OnDoubleClick(e);
        if (ItemAt(W(PointToClient(MousePosition))) == null) Fit();
    }

    public Point SnapToMonitor(Point p)
    {
        var center = new Point(p.X + Cell.Width / 2, p.Y + Cell.Height / 2);
        var m = Organizer.MonitorAt(center, Monitors);
        return m == null ? p : Organizer.Snap(p, m.WorkArea, Cell, FromRight);
    }

    // Guarda el rectángulo de la zona respecto a la pantalla donde cae su centro.
    public void PlaceZone(Zone z, Rectangle world)
    {
        var m = Organizer.MonitorAt(new Point(world.X + world.Width / 2, world.Y + world.Height / 2), Monitors);
        if (m == null) return;
        var r = Rectangle.Intersect(world, m.WorkArea);
        if (r.Width < Cell.Width || r.Height < Cell.Height) r = new Rectangle(r.Location, new Size(Math.Max(r.Width, Cell.Width), Math.Max(r.Height, Cell.Height)));
        z.Monitor = m.State.Path;
        z.Edid = m.State.Edid;
        z.X = r.X - m.Bounds.X;
        z.Y = r.Y - m.Bounds.Y;
        z.Width = r.Width;
        z.Height = r.Height;
    }
}
