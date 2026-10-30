using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using static MajinDesktop.Native;

namespace MajinDesktop;

// Estado de una pantalla dentro de un slot: solo posición y orientación (más el tamaño, para dibujarla).
[DataContract]
sealed class MonitorState
{
    [DataMember(Name = "path", Order = 1)] public string Path = "";   // ruta del monitor en ese puerto: estable entre reinicios
    [DataMember(Name = "edid", Order = 2)] public string Edid = "";   // fabricante:producto, respaldo si cambias de puerto
    [DataMember(Name = "name", Order = 3)] public string Name = "";
    [DataMember(Name = "x", Order = 4)] public int X;
    [DataMember(Name = "y", Order = 5)] public int Y;
    [DataMember(Name = "width", Order = 6)] public int Width;     // huella en el escritorio (ya rotada)
    [DataMember(Name = "height", Order = 7)] public int Height;
    [DataMember(Name = "rotation", Order = 8)] public int Rotation = 1;   // 1=0°, 2=90°, 3=180°, 4=270°

    [OnDeserializing]
    void Defaults(StreamingContext _) { Path = Edid = Name = ""; Rotation = 1; }

    public bool Primary => X == 0 && Y == 0;
    public bool Portrait => Rotation == 2 || Rotation == 4;

    public string RotationText => Rotation switch
    {
        2 => "vertical (90°)",
        3 => "horizontal (180°)",
        4 => "vertical (270°)",
        _ => "horizontal",
    };

    // Formato de config.ini (v0.1), solo para migrarlo.
    public static MonitorState? Parse(string s)
    {
        var p = s.Split(new[] { '|' }, 8);
        if (p.Length < 8) return null;
        try
        {
            return new MonitorState
            {
                Rotation = int.Parse(p[0]), X = int.Parse(p[1]), Y = int.Parse(p[2]),
                Width = int.Parse(p[3]), Height = int.Parse(p[4]),
                Edid = p[5], Name = p[6], Path = p[7],
            };
        }
        catch (FormatException) { return null; }
    }
}

enum ApplyStatus { Applied, AlreadyActive, NoMonitorsFound, Failed }

static class Displays
{
    static void Query(out PATH_INFO[] paths, out MODE_INFO[] modes)
    {
        for (int attempt = 0; ; attempt++)
        {
            int err = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint np, out uint nm);
            if (err != 0) throw new Win32Exception(err);
            paths = new PATH_INFO[np];
            modes = new MODE_INFO[nm];
            err = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero);
            if (err == ERROR_INSUFFICIENT_BUFFER && attempt < 5) continue; // cambió algo entre las dos llamadas
            if (err != 0) throw new Win32Exception(err);
            Array.Resize(ref paths, (int)np);
            Array.Resize(ref modes, (int)nm);
            return;
        }
    }

    internal static int SourceModeIndex(PATH_INFO p, MODE_INFO[] modes)
    {
        int idx = (int)p.sourceInfo.modeInfoIdx;
        return idx >= 0 && idx < modes.Length && modes[idx].infoType == DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE ? idx : -1;
    }

    static MonitorState? Describe(PATH_INFO p, MODE_INFO[] modes, int number)
    {
        int idx = SourceModeIndex(p, modes);
        if (idx < 0) return null;
        var sm = modes[idx].sourceMode;
        bool swapped = p.targetInfo.rotation is 2 or 4; // CCD da el modo de origen sin rotar

        var req = new TARGET_DEVICE_NAME();
        req.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
        req.header.size = (uint)Marshal.SizeOf<TARGET_DEVICE_NAME>();
        req.header.adapterId = p.targetInfo.adapterId;
        req.header.id = p.targetInfo.id;
        bool named = DisplayConfigGetDeviceInfo(ref req) == 0;

        return new MonitorState
        {
            Path = named ? req.monitorDevicePath ?? "" : "",
            Edid = named ? $"{req.edidManufactureId:X4}:{req.edidProductCodeId:X4}" : "",
            Name = named && !string.IsNullOrWhiteSpace(req.monitorFriendlyDeviceName)
                ? req.monitorFriendlyDeviceName.Trim() : $"Pantalla {number}",
            X = sm.position.x, Y = sm.position.y,
            Width = (int)(swapped ? sm.height : sm.width), Height = (int)(swapped ? sm.width : sm.height),
            Rotation = (int)p.targetInfo.rotation,
        };
    }

    // Pantallas activas ahora mismo, ordenadas de izquierda a derecha.
    public static List<MonitorState> Snapshot()
    {
        Query(out var paths, out var modes);
        var list = new List<MonitorState>();
        for (int i = 0; i < paths.Length; i++)
        {
            var m = Describe(paths[i], modes, i + 1);
            if (m != null && !list.Any(o => o.Path.Length > 0 && o.Path == m.Path)) list.Add(m);
        }
        return list.OrderBy(m => m.X).ThenBy(m => m.Y).ToList();
    }

    // Busca en el slot la pantalla guardada que corresponde a una pantalla conectada.
    static MonitorState? Find(IList<MonitorState> slot, MonitorState cur, IList<MonitorState> current)
    {
        var exact = slot.FirstOrDefault(s => s.Path.Length > 0 && string.Equals(s.Path, cur.Path, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;
        // Respaldo por modelo, solo si no hay ambigüedad (dos monitores iguales no se pueden distinguir así).
        if (cur.Edid.Length == 0 || current.Count(c => c.Edid == cur.Edid) != 1) return null;
        var same = slot.Where(s => s.Edid == cur.Edid).ToList();
        return same.Count == 1 ? same[0] : null;
    }

    public static bool Matches(IList<MonitorState> current, IList<MonitorState> slot)
    {
        if (slot.Count == 0) return false;
        int matched = 0;
        foreach (var cur in current)
        {
            var s = Find(slot, cur, current);
            if (s == null) continue;
            if (s.Rotation != cur.Rotation || s.X != cur.X || s.Y != cur.Y) return false;
            matched++;
        }
        return matched > 0;
    }

    public static ApplyStatus Apply(IList<MonitorState> slot, bool validateOnly, out string error)
    {
        error = "";
        try
        {
            Query(out var paths, out var modes);
            var current = new MonitorState?[paths.Length];
            for (int i = 0; i < paths.Length; i++) current[i] = Describe(paths[i], modes, i + 1);

            if (!Plan(paths, modes, current, slot, out bool changed)) return ApplyStatus.NoMonitorsFound;
            if (!changed && !validateOnly) return ApplyStatus.AlreadyActive;

            uint flags = SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES |
                (validateOnly ? SDC_VALIDATE : SDC_APPLY | SDC_SAVE_TO_DATABASE);
            int err = SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes, flags);
            if (err == 0) return ApplyStatus.Applied;
            error = Explain(err);
            return ApplyStatus.Failed;
        }
        catch (Win32Exception ex)
        {
            error = Explain(ex.NativeErrorCode);
            return ApplyStatus.Failed;
        }
    }

    // Parte pura de Apply (sin llamadas a Windows, se prueba en tests/): coloca cada pantalla del slot
    // en la configuración actual. Devuelve false si ninguna pantalla del slot está conectada.
    internal static bool Plan(PATH_INFO[] paths, MODE_INFO[] modes, MonitorState?[] current, IList<MonitorState> slot, out bool changed)
    {
        changed = false;
        var all = current.Where(c => c != null).Select(c => c!).ToList();
        var updated = new HashSet<int>();  // índices de modos de origen ya colocados
        for (int i = 0; i < paths.Length; i++)
        {
            if (current[i] == null) continue;
            var s = Find(slot, current[i]!, all);
            if (s == null) continue;

            // El modo de origen no se toca: la rotación va en la ruta y Windows gira el escritorio.
            int idx = SourceModeIndex(paths[i], modes);
            if (idx < 0) continue;
            if (paths[i].targetInfo.rotation != (uint)s.Rotation) changed = true;
            paths[i].targetInfo.rotation = (uint)s.Rotation;

            var pos = modes[idx].sourceMode.position;
            if (pos.x != s.X || pos.y != s.Y) changed = true;
            modes[idx].sourceMode.position = new POINTL { x = s.X, y = s.Y };
            updated.Add(idx);
        }
        if (updated.Count == 0) return false;

        var sources = Enumerable.Range(0, paths.Length)
            .Select(i => SourceModeIndex(paths[i], modes)).Where(i => i >= 0).Distinct().ToList();

        // Huella de cada origen en el escritorio con su rotación nueva.
        Rect Footprint(int idx)
        {
            var sm = modes[idx].sourceMode;
            bool rotated = paths.Any(p => SourceModeIndex(p, modes) == idx && p.targetInfo.rotation is 2 or 4);
            return new Rect(sm.position.x, sm.position.y, (int)(rotated ? sm.height : sm.width), (int)(rotated ? sm.width : sm.height));
        }

        // Pantallas que no están en el slot: si chocan con alguna colocada, se mandan a la derecha.
        foreach (int idx in sources.Where(i => !updated.Contains(i)))
        {
            var me = Footprint(idx);
            if (!sources.Any(o => o != idx && me.Overlaps(Footprint(o)))) continue;
            int right = sources.Where(o => o != idx).Max(o => Footprint(o).Right);
            modes[idx].sourceMode.position = new POINTL { x = right, y = 0 };
            changed = true;
        }

        // Windows exige que la principal esté en (0,0). Si la principal del slot no está conectada,
        // se desplaza todo para que la pantalla más cercana al origen pase a serlo.
        if (!sources.Any(i => modes[i].sourceMode.position.x == 0 && modes[i].sourceMode.position.y == 0))
        {
            var anchor = modes[sources.OrderBy(i => Math.Abs(modes[i].sourceMode.position.x) + Math.Abs(modes[i].sourceMode.position.y)).First()].sourceMode.position;
            foreach (int i in sources)
            {
                modes[i].sourceMode.position.x -= anchor.x;
                modes[i].sourceMode.position.y -= anchor.y;
            }
            changed = true;
        }

        return true;
    }

    readonly struct Rect
    {
        public readonly int X, Y, W, H;
        public Rect(int x, int y, int w, int h) { X = x; Y = y; W = w; H = h; }
        public int Right => X + W;
        public bool Overlaps(Rect o) => X < o.X + o.W && o.X < X + W && Y < o.Y + o.H && o.Y < Y + H;
    }

    static string Explain(int err) => err switch
    {
        5 => "Windows no deja cambiar las pantallas ahora (¿sesión bloqueada o escritorio remoto?).",
        87 => "Windows rechazó esa disposición (pantallas solapadas o separadas).",
        1610 => "La configuración guardada ya no es válida para las pantallas conectadas.",
        _ => $"{new Win32Exception(err).Message} (código {err})",
    };
}
