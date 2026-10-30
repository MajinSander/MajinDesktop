using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using static MajinDesktop.Native;

namespace MajinDesktop;

// Pruebas de la lógica que no necesita tocar Windows. build.ps1 las compila y las ejecuta en cada build.
static class Tests
{
    static int failed, passed;

    static int Main()
    {
        string dir = Path.Combine(Path.GetTempPath(), "MajinDesktop-tests-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("MAJINDESKTOP_DATA", dir); // antes de tocar Config
        try
        {
            Run("Atajo: ida y vuelta", HotkeyRoundTrip);
            Run("Atajo: comparar", HotkeySameAs);
            Run("Monitor: formato v0.1", MonitorParse);
            Run("Plan: gira y coloca", PlanRotatesAndPlaces);
            Run("Plan: ya activo", PlanNoChanges);
            Run("Plan: nada conectado", PlanNothingConnected);
            Run("Plan: pantalla extra que choca", PlanMovesOverlappingExtra);
            Run("Plan: principal ausente", PlanShiftsWhenPrimaryMissing);
            Run("Plan: respaldo por modelo", PlanFallsBackToEdid);
            Run("Coincide con el actual", MatchesCurrent);
            Run("Config: primera vez", ConfigDefaults);
            Run("Config: JSON ida y vuelta", ConfigRoundTrip);
            Run("Config: migra config.ini", ConfigMigratesIni);
            Run("Config: JSON roto", ConfigRecoversFromBrokenJson);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        Console.WriteLine($"{passed} bien, {failed} mal");
        return failed == 0 ? 0 : 1;
    }

    static void Run(string name, Action test)
    {
        try { test(); passed++; Console.WriteLine("  ok   " + name); }
        catch (Exception ex) { failed++; Console.WriteLine($"  FALLO {name}: {ex.Message}"); }
    }

    static void Is<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{what}: esperaba {expected}, salió {actual}");
    }

    // ---------- Atajos ----------

    static void HotkeyRoundTrip()
    {
        var h = new Hotkey { Ctrl = true, Alt = true, Shift = true, Key = Keys.D3 };
        Is("Ctrl+Alt+Shift+D3", h.Serialize(), "archivo");
        Is("Ctrl+Alt+Shift+3", h.ToString(), "pantalla");
        var back = Hotkey.Parse(h.Serialize());
        Is(true, back.SameAs(h), "ida y vuelta");
        Is(true, Hotkey.Parse("").IsEmpty, "vacío");
        Is(Keys.Back, Hotkey.Parse("ctrl+back").Key, "minúsculas");
    }

    static void HotkeySameAs()
    {
        var a = Hotkey.Parse("Ctrl+Alt+D1");
        Is(false, a.SameAs(Hotkey.Parse("Ctrl+Shift+D1")), "otros modificadores");
        Is(false, new Hotkey().SameAs(new Hotkey()), "vacíos nunca coinciden");
    }

    static void MonitorParse()
    {
        var m = MonitorState.Parse(@"4|-1080|-218|1080|1920|AC10:F071|DELL SE2216H|\\?\DISPLAY#DELF071#1")!;
        Is(4, m.Rotation, "rotación");
        Is(-1080, m.X, "x");
        Is(@"\\?\DISPLAY#DELF071#1", m.Path, "ruta");
        Is(true, m.Portrait, "vertical");
        Is(null, MonitorState.Parse("1|2|3"), "incompleto");
    }

    // ---------- Plan (colocación de pantallas) ----------

    // Escritorio de pega: cada pantalla es una ruta con su modo de origen (tamaño SIN rotar, como lo da CCD).
    sealed class Fake
    {
        public readonly List<PATH_INFO> Paths = new();
        public readonly List<MODE_INFO> Modes = new();
        public readonly List<MonitorState> Current = new();

        public Fake Add(string path, int x, int y, uint w, uint h, int rotation = 1, string edid = "")
        {
            var mode = new MODE_INFO { infoType = DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE };
            mode.sourceMode.width = w;
            mode.sourceMode.height = h;
            mode.sourceMode.position = new POINTL { x = x, y = y };
            var p = new PATH_INFO();
            p.sourceInfo.modeInfoIdx = (uint)Modes.Count;
            p.targetInfo.rotation = (uint)rotation;
            Modes.Add(mode);
            Paths.Add(p);
            bool rot = rotation is 2 or 4;
            Current.Add(new MonitorState { Path = path, Edid = edid, X = x, Y = y, Width = (int)(rot ? h : w), Height = (int)(rot ? w : h), Rotation = rotation });
            return this;
        }

        public bool Plan(IList<MonitorState> slot, out bool changed, out PATH_INFO[] paths, out MODE_INFO[] modes)
        {
            paths = Paths.ToArray();
            modes = Modes.ToArray();
            return Displays.Plan(paths, modes, Current.Cast<MonitorState?>().ToArray(), slot, out changed);
        }
    }

    static MonitorState M(string path, int x, int y, int rotation = 1, string edid = "") =>
        new() { Path = path, X = x, Y = y, Rotation = rotation, Edid = edid };

    static Fake ThreeScreens() => new Fake()
        .Add("L", -1080, -218, 1920, 1080, 4)
        .Add("C", 0, 0, 2560, 1440)
        .Add("R", 2560, -480, 1920, 1080, 4);

    static void PlanRotatesAndPlaces()
    {
        var slot = new[] { M("L", -1920, 0), M("C", 0, 0), M("R", 2560, -480, 2) };
        Is(true, ThreeScreens().Plan(slot, out bool changed, out var paths, out var modes), "encontrado");
        Is(true, changed, "cambia");
        Is(1u, paths[0].targetInfo.rotation, "izquierda en horizontal");
        Is(-1920, modes[0].sourceMode.position.x, "izquierda x");
        Is(2u, paths[2].targetInfo.rotation, "derecha a 90°");
        Is(1920u, modes[0].sourceMode.width, "el modo de origen no se gira");
    }

    static void PlanNoChanges()
    {
        var f = ThreeScreens();
        f.Plan(f.Current.ToList(), out bool changed, out _, out _);
        Is(false, changed, "sin cambios");
    }

    static void PlanNothingConnected()
    {
        Is(false, ThreeScreens().Plan(new[] { M("TV", 0, 0) }, out _, out _, out _), "ninguna conectada");
    }

    static void PlanMovesOverlappingExtra()
    {
        // La TV está encendida pero el slot no la incluye; el slot pone la derecha justo donde está la TV.
        var f = new Fake().Add("C", 0, 0, 2560, 1440).Add("TV", 2560, 0, 3840, 2160);
        var slot = new[] { M("C", 0, 0), M("R", 2560, 0, 4) };
        f.Add("R", 6400, 0, 1920, 1080, 4);
        f.Plan(slot, out _, out _, out var modes);
        Is(2560 + 1080, modes[1].sourceMode.position.x, "la TV pasa a la derecha de todo");
    }

    static void PlanShiftsWhenPrimaryMissing()
    {
        // El slot tenía de principal una pantalla que ya no está: se desplaza todo para que haya una en (0,0).
        var f = new Fake().Add("C", 0, 0, 2560, 1440).Add("R", 2560, 0, 1920, 1080);
        var slot = new[] { M("X", 0, 0), M("C", 1920, 0), M("R", 4480, 0) };
        f.Plan(slot, out bool changed, out _, out var modes);
        Is(true, changed, "cambia");
        Is(0, modes[0].sourceMode.position.x, "C pasa a principal");
        Is(2560, modes[1].sourceMode.position.x, "R se desplaza igual");
    }

    static void PlanFallsBackToEdid()
    {
        // Cambiaste el cable de puerto: la ruta es otra, pero el modelo es único.
        var f = new Fake().Add("C-nuevo", 0, 0, 2560, 1440, 1, "ACR:06CF").Add("R", 2560, 0, 1920, 1080, 1, "BNQ:7951");
        var slot = new[] { M("C-viejo", 0, 0, 1, "ACR:06CF"), M("R", 2560, 0, 4, "BNQ:7951") };
        Is(true, f.Plan(slot, out _, out var paths, out _), "encontrado");
        Is(4u, paths[1].targetInfo.rotation, "R girada");
    }

    static void MatchesCurrent()
    {
        var f = ThreeScreens();
        Is(true, Displays.Matches(f.Current, f.Current), "igual");
        var moved = f.Current.Select(m => M(m.Path, m.X, m.Y + 10, m.Rotation)).ToList();
        Is(false, Displays.Matches(f.Current, moved), "movida");
        Is(false, Displays.Matches(f.Current, new List<MonitorState>()), "slot vacío");
    }

    // ---------- Configuración ----------

    static void Clean()
    {
        if (Directory.Exists(Config.Dir)) Directory.Delete(Config.Dir, true);
    }

    static void ConfigDefaults()
    {
        Clean();
        var c = Config.Load();
        Is(9, c.Slots.Length, "slots");
        Is("Ctrl+Alt+Shift+1", c.Slots[0].Hotkey.ToString(), "atajo slot 1");
        Is("Ctrl+Alt+Shift+0", c.Cycle.ToString(), "siguiente");
        Is("Ctrl+Alt+Shift+Retroceso", c.Undo.ToString(), "deshacer");
    }

    static void ConfigRoundTrip()
    {
        Clean();
        var c = Config.Load();
        c.Slots[2].Name = "Juegos ñ";
        c.Slots[2].Monitors.Add(M(@"\\?\DISPLAY#X", -1080, 5, 4));
        c.Previous = new List<MonitorState> { M("C", 0, 0) };
        c.Last = 2;
        c.Save();
        var d = Config.Load();
        Is("Juegos ñ", d.Slots[2].Name, "nombre");
        Is(-1080, d.Slots[2].Monitors[0].X, "x");
        Is(@"\\?\DISPLAY#X", d.Slots[2].Monitors[0].Path, "ruta");
        Is("Ctrl+Alt+Shift+3", d.Slots[2].Hotkey.ToString(), "atajo");
        Is(true, d.Slots[0].IsEmpty, "slot vacío");
        Is("", d.Slots[0].Name, "nombre vacío no es null");
        Is(1, d.Previous!.Count, "anterior");
        Is(2, d.Last, "último");
    }

    static void ConfigMigratesIni()
    {
        Clean();
        Directory.CreateDirectory(Config.Dir);
        File.WriteAllText(Path.Combine(Config.Dir, "config.ini"),
            "cycle=Ctrl+Alt+D0\nlast=1\n\n[slot2]\nname=Viejo\nhotkey=Ctrl+Alt+D2\nmonitor=4|-1080|-218|1080|1920|E|DELL|P1\n");
        var c = Config.Load();
        Is("Viejo", c.Slots[1].Name, "nombre");
        Is("Ctrl+Alt+2", c.Slots[1].Hotkey.ToString(), "atajo");
        Is("P1", c.Slots[1].Monitors[0].Path, "monitor");
        Is("Ctrl+Alt+Shift+Retroceso", c.Undo.ToString(), "deshacer por defecto");
        Is(true, File.Exists(Config.FilePath), "config.json creado");
        Is(true, File.Exists(Path.Combine(Config.Dir, "config.ini.bak")), "ini apartado");
    }

    static void ConfigRecoversFromBrokenJson()
    {
        Clean();
        Directory.CreateDirectory(Config.Dir);
        File.WriteAllText(Config.FilePath, "{ esto no es json");
        var c = Config.Load();
        Is(9, c.Slots.Length, "empieza de cero");
        Is(1, Directory.GetFiles(Config.Dir, "config.broken-*.json").Length, "roto apartado");
    }
}
