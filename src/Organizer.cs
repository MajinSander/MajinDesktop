using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Runtime.Serialization;
using System.Windows.Forms;

namespace MajinDesktop;

// Zona del organizador (estilo Fences, pero solo existe aquí: no se dibuja nada en el escritorio real).
// Su rectángulo va respecto a su pantalla, igual que los iconos.
[DataContract]
sealed class Zone
{
    [DataMember(Name = "name", Order = 1)] public string Name = "";
    [DataMember(Name = "monitor", Order = 2)] public string Monitor = "";
    [DataMember(Name = "edid", Order = 3)] public string Edid = "";
    [DataMember(Name = "x", Order = 4)] public int X;
    [DataMember(Name = "y", Order = 5)] public int Y;
    [DataMember(Name = "width", Order = 6)] public int Width;
    [DataMember(Name = "height", Order = 7)] public int Height;
    // Qué va dentro: tipos (Game = cualquier juego) y/o palabras del nombre separadas por comas.
    [DataMember(Name = "kinds", Order = 8)] public List<ItemKind> Kinds = new();
    [DataMember(Name = "nameFilter", Order = 9)] public string NameFilter = "";

    [OnDeserializing]
    void Defaults(StreamingContext _) { Name = Monitor = Edid = NameFilter = ""; Kinds = new(); }

    public bool HasRule => Kinds.Count > 0 || NameFilter.Trim().Length > 0;

    public bool Matches(string name, ItemKind kind)
    {
        if (!HasRule) return false;
        bool kindOk = Kinds.Count == 0 || Kinds.Contains(kind) || (Kinds.Contains(ItemKind.Game) && Shortcuts.IsGame(kind));
        var words = NameFilter.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(w => w.Trim()).Where(w => w.Length > 0).ToList();
        bool nameOk = words.Count == 0 || words.Any(w => name.IndexOf(w, StringComparison.CurrentCultureIgnoreCase) >= 0);
        return kindOk && nameOk;
    }

    public string RuleText
    {
        get
        {
            var parts = Kinds.Select(k => k == ItemKind.Game ? "Juegos" : Shortcuts.KindName(k)).ToList();
            if (NameFilter.Trim().Length > 0) parts.Add($"«{NameFilter.Trim()}»");
            return parts.Count == 0 ? "sin regla" : string.Join(", ", parts);
        }
    }
}

sealed class OrgItem
{
    public string Key = "", Name = "";
    public ItemKind Kind;
    public Point Pos, Original;   // esquina del icono en coordenadas de pantalla
    public bool Selected;
    public bool Moved => Pos != Original;
}

sealed class OrgMonitor
{
    public MonitorState State = new();
    public Rectangle Bounds, WorkArea;   // WorkArea = sin la barra de tareas
}

static class Organizer
{
    public const int Margin = 2; // Windows deja 2 px antes de la primera fila/columna

    public static List<OrgMonitor> Monitors(IList<MonitorState> states)
    {
        var screens = Screen.AllScreens;
        return states.Select(m =>
        {
            var bounds = new Rectangle(m.X, m.Y, m.Width, m.Height);
            var screen = screens.FirstOrDefault(s => s.Bounds == bounds);
            return new OrgMonitor { State = m, Bounds = bounds, WorkArea = screen?.WorkingArea ?? bounds };
        }).ToList();
    }

    public static OrgMonitor? MonitorAt(Point p, IList<OrgMonitor> mons) =>
        mons.FirstOrDefault(m => m.Bounds.Contains(p)) ??
        mons.OrderBy(m => Dist2(p, m.Bounds)).FirstOrDefault();

    static long Dist2(Point p, Rectangle r)
    {
        long dx = Math.Max(Math.Max(r.Left - p.X, 0), p.X - r.Right + 1);
        long dy = Math.Max(Math.Max(r.Top - p.Y, 0), p.Y - r.Bottom + 1);
        return dx * dx + dy * dy;
    }

    // Celdas de una zona, columna a columna (como coloca Windows), desde la izquierda o la derecha.
    public static IEnumerable<Point> Cells(Rectangle area, Size cell, bool fromRight)
    {
        int cols = Math.Max(1, (area.Width - Margin) / cell.Width);
        int rows = Math.Max(1, (area.Height - Margin) / cell.Height);
        for (int c = 0; c < cols; c++)
        {
            int x = fromRight ? area.Right - (c + 1) * cell.Width : area.Left + Margin + c * cell.Width;
            for (int r = 0; r < rows; r++)
                yield return new Point(x, area.Top + Margin + r * cell.Height);
        }
    }

    // Celda más cercana dentro de la zona de trabajo de su pantalla.
    public static Point Snap(Point p, Rectangle area, Size cell, bool fromRight)
    {
        int cols = Math.Max(1, (area.Width - Margin) / cell.Width);
        int rows = Math.Max(1, (area.Height - Margin) / cell.Height);
        int col = fromRight
            ? (int)Math.Round((area.Right - cell.Width - p.X) / (double)cell.Width)
            : (int)Math.Round((p.X - area.Left - Margin) / (double)cell.Width);
        int row = (int)Math.Round((p.Y - area.Top - Margin) / (double)cell.Height);
        col = Math.Max(0, Math.Min(cols - 1, col));
        row = Math.Max(0, Math.Min(rows - 1, row));
        int x = fromRight ? area.Right - (col + 1) * cell.Width : area.Left + Margin + col * cell.Width;
        return new Point(x, area.Top + Margin + row * cell.Height);
    }

    // Coloca los elementos, en ese orden, en las celdas libres de la zona. Devuelve cuántos no cupieron
    // (esos se quedan amontonados en la última celda libre para que no se pierdan).
    public static int Arrange(IList<OrgItem> items, Rectangle area, Size cell, bool fromRight, ICollection<Point>? occupied = null)
    {
        var free = Cells(area, cell, fromRight).Where(c => occupied == null || !occupied.Contains(c)).ToList();
        int i = 0;
        for (; i < items.Count && i < free.Count; i++) items[i].Pos = free[i];
        var last = free.Count > 0 ? free[free.Count - 1] : new Point(area.Left + Margin, area.Top + Margin);
        for (int j = i; j < items.Count; j++) items[j].Pos = last;
        return items.Count - i;
    }

    static readonly CompareInfo Compare = CultureInfo.GetCultureInfo("es-ES").CompareInfo;

    public static string SortName(string name) =>
        new string(name.Where(ch => ch != '™' && ch != '®' && ch != '©').ToArray()).Trim();

    public static int ByName(OrgItem a, OrgItem b) =>
        Compare.Compare(SortName(a.Name), SortName(b.Name), CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace);

    public static int ByKind(OrgItem a, OrgItem b)
    {
        int k = a.Kind.CompareTo(b.Kind);
        return k != 0 ? k : ByName(a, b);
    }

    // Orden en que están ahora (columna a columna), para compactar sin desordenar.
    public static int ByPosition(OrgItem a, OrgItem b, Size cell, bool fromRight)
    {
        int ca = a.Pos.X / Math.Max(1, cell.Width), cb = b.Pos.X / Math.Max(1, cell.Width);
        if (ca != cb) return fromRight ? cb.CompareTo(ca) : ca.CompareTo(cb);
        return a.Pos.Y.CompareTo(b.Pos.Y);
    }

    public static Rectangle? ZoneRect(Zone z, IList<OrgMonitor> mons)
    {
        var m = FindMonitor(z.Monitor, z.Edid, mons);
        return m == null ? null : new Rectangle(m.Bounds.X + z.X, m.Bounds.Y + z.Y, z.Width, z.Height);
    }

    public static OrgMonitor? FindMonitor(string path, string edid, IList<OrgMonitor> mons) =>
        mons.FirstOrDefault(m => m.State.Path.Length > 0 && string.Equals(m.State.Path, path, StringComparison.OrdinalIgnoreCase))
        ?? (edid.Length > 0 && mons.Count(m => m.State.Edid == edid) == 1 ? mons.First(m => m.State.Edid == edid) : null);

    // Mete en cada zona lo que cumple su regla (la primera zona que encaje gana), ordenado por nombre.
    // Devuelve cuántos iconos se han colocado y cuántos no cabían.
    public static (int placed, int overflow) SortIntoZones(IList<OrgItem> items, IList<Zone> zones, IList<OrgMonitor> mons, Size cell)
    {
        int placed = 0, overflow = 0;
        var taken = new HashSet<OrgItem>();
        foreach (var z in zones)
        {
            if (ZoneRect(z, mons) is not { } rect) continue;
            var inZone = items.Where(i => !taken.Contains(i) && z.Matches(i.Name, i.Kind)).ToList();
            inZone.Sort(ByName);
            foreach (var i in inZone) taken.Add(i);
            overflow += Arrange(inZone, rect, cell, false);
            placed += inZone.Count;
        }
        return (placed, overflow);
    }

    // Zona en la que caería un icono nuevo (para colocarlo solo).
    public static Zone? ZoneFor(string name, ItemKind kind, IList<Zone> zones) => zones.FirstOrDefault(z => z.Matches(name, kind));

    public static Point? FreeCellIn(Rectangle area, Size cell, IEnumerable<Point> taken)
    {
        // Solo cuentan los iconos que están dentro de la zona (los de fuera, con el imán, caerían en su borde).
        var occupied = new HashSet<Point>(taken.Where(area.Contains).Select(p => Snap(p, area, cell, false)));
        foreach (var c in Cells(area, cell, false))
            if (!occupied.Contains(c)) return c;
        return null;
    }
}
