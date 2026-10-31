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
            Run("Iconos: ida y vuelta por pantalla", IconsRoundTrip);
            Run("Iconos: cambia la principal", IconsFollowTheirMonitor);
            Run("Iconos: pantalla ausente o más pequeña", IconsMissingOrSmaller);
            Run("Organizador: celdas por columnas", OrgCells);
            Run("Organizador: imán a la rejilla", OrgSnap);
            Run("Organizador: colocar respetando ocupadas", OrgArrange);
            Run("Organizador: orden por nombre", OrgSortName);
            Run("Zonas: reglas", ZoneRules);
            Run("Zonas: ordenar en zonas", ZoneSort);
            Run("Zonas: celda libre para un icono nuevo", ZoneFreeCell);
            Run("Accesos: clasificar destino", ClassifyTargets);
            Run("Accesos: .url de Steam", SteamUrl);
            Run("Lote: limpiar nombres", BatchCleanNames);
            Run("Lote: renombrar y deshacer", BatchRenameUndo);
            Run("Lote: icono de .lnk y deshacer", BatchLnkIcon);
            Run("Lote: icono de .url y deshacer", BatchUrlIcon);
            Run("Lote: imagen a .ico", BatchImageToIco);
            Run("Biblioteca: appmanifest", LibraryAcf);
            Run("Biblioteca: nombres de archivo", LibraryFileNames);
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

    // ---------- Iconos ----------

    static readonly System.Drawing.Size Cell = new(94, 116);

    static void IconsRoundTrip()
    {
        var mons = ThreeScreens().Current;
        foreach (var p in new[] { new System.Drawing.Point(2467, 1278), new System.Drawing.Point(-1080, -218), new System.Drawing.Point(2560 + 1000, 0) })
        {
            var rel = Icons.ToRelative("k", "n", p, mons)!;
            Is(p, Icons.ToScreen(rel, mons, Cell)!.Value, $"punto {p}");
        }
        Is("R", Icons.ToRelative("k", "n", new System.Drawing.Point(2600, 100), mons)!.Monitor, "pantalla derecha");
    }

    static void IconsFollowTheirMonitor()
    {
        // Guardado con C de principal; ahora la izquierda es la principal y todo se ha desplazado.
        var before = ThreeScreens().Current;
        var icon = Icons.ToRelative("k", "n", new System.Drawing.Point(100, 50), before)!;
        Is("C", icon.Monitor, "en la central");
        var after = new Fake().Add("L", 0, 0, 1920, 1080, 4).Add("C", 1080, 218, 2560, 1440).Current;
        Is(new System.Drawing.Point(1180, 268), Icons.ToScreen(icon, after, Cell)!.Value, "sigue en la central");
    }

    static void IconsMissingOrSmaller()
    {
        var mons = ThreeScreens().Current;
        var onRight = Icons.ToRelative("k", "n", new System.Drawing.Point(2560 + 1000, 1800), mons)!;
        Is(null, Icons.ToScreen(onRight, new Fake().Add("C", 0, 0, 2560, 1440).Current, Cell), "sin la derecha");
        // La derecha pasa a horizontal (1920x1080): el icono que estaba a 1800 px de alto se queda dentro.
        var smaller = new Fake().Add("C", 0, 0, 2560, 1440).Add("R", 2560, 0, 1920, 1080).Current;
        Is(1080 - Cell.Height / 2, Icons.ToScreen(onRight, smaller, Cell)!.Value.Y, "recortado");
    }

    // ---------- Organizador ----------

    static readonly System.Drawing.Rectangle Area = new(0, 0, 400, 300);
    static readonly System.Drawing.Size C2 = new(100, 100);

    static void OrgCells()
    {
        var cells = Organizer.Cells(Area, C2, false).ToList();
        Is(3 * 2, cells.Count, "3 columnas x 2 filas");   // (400-2)/100 = 3, (300-2)/100 = 2
        Is(new System.Drawing.Point(2, 2), cells[0], "primera");
        Is(new System.Drawing.Point(2, 102), cells[1], "baja por la columna");
        Is(new System.Drawing.Point(300, 2), Organizer.Cells(Area, C2, true).First(), "desde la derecha");
    }

    static void OrgSnap()
    {
        Is(new System.Drawing.Point(102, 102), Organizer.Snap(new System.Drawing.Point(130, 80), Area, C2, false), "celda cercana");
        Is(new System.Drawing.Point(202, 102), Organizer.Snap(new System.Drawing.Point(9000, 9000), Area, C2, false), "se queda dentro");
    }

    static OrgItem It(string name, ItemKind kind = ItemKind.App, int x = 0, int y = 0) =>
        new() { Key = name, Name = name, Kind = kind, Pos = new System.Drawing.Point(x, y), Original = new System.Drawing.Point(x, y) };

    static void OrgArrange()
    {
        var items = new List<OrgItem> { It("a"), It("b"), It("c") };
        int overflow = Organizer.Arrange(items, Area, C2, false, new[] { new System.Drawing.Point(2, 102) });
        Is(0, overflow, "caben");
        Is(new System.Drawing.Point(2, 2), items[0].Pos, "a");
        Is(new System.Drawing.Point(102, 2), items[1].Pos, "b salta la ocupada");
        var many = Enumerable.Range(0, 8).Select(i => It("x" + i)).ToList();
        Is(2, Organizer.Arrange(many, Area, C2, false), "sobran 2");
    }

    static void OrgSortName()
    {
        var items = new List<OrgItem> { It("Zelda"), It("ánima"), It("HELLDIVERS™ 2"), It("batman") };
        items.Sort(Organizer.ByName);
        Is("ánima,batman,HELLDIVERS™ 2,Zelda", string.Join(",", items.Select(i => i.Name)), "orden");
        var byKind = new List<OrgItem> { It("b", ItemKind.App), It("a", ItemKind.Steam) };
        byKind.Sort(Organizer.ByKind);
        Is("a", byKind[0].Name, "Steam antes que programas");
    }

    static void ZoneRules()
    {
        var games = new Zone { Kinds = { ItemKind.Game } };
        Is(true, games.Matches("Hollow Knight", ItemKind.Steam), "cualquier juego incluye Steam");
        Is(false, games.Matches("Discord", ItemKind.App), "programa no");
        var words = new Zone { NameFilter = "dragon ball, xenoverse" };
        Is(true, words.Matches("DRAGON BALL Sparking! ZERO", ItemKind.Steam), "palabra");
        Is(false, new Zone().Matches("x", ItemKind.App), "sin regla no coge nada");
        var both = new Zone { Kinds = { ItemKind.Steam }, NameFilter = "dragon" };
        Is(false, both.Matches("DRAGON BALL", ItemKind.Epic), "tipo y nombre a la vez");
    }

    static void ZoneSort()
    {
        var mons = Organizer.Monitors(new List<MonitorState> { new() { Path = "C", X = 0, Y = 0, Width = 2560, Height = 1440 } });
        mons[0].WorkArea = mons[0].Bounds;
        var zones = new List<Zone>
        {
            new() { Name = "Steam", Monitor = "C", X = 1000, Y = 0, Width = 400, Height = 300, Kinds = { ItemKind.Steam } },
            new() { Name = "Juegos", Monitor = "C", X = 0, Y = 500, Width = 400, Height = 300, Kinds = { ItemKind.Game } },
        };
        var items = new List<OrgItem> { It("Zeta", ItemKind.Steam), It("Alfa", ItemKind.Steam), It("Epic", ItemKind.Epic), It("Word", ItemKind.App, 7, 7) };
        var (placed, overflow) = Organizer.SortIntoZones(items, zones, mons, C2);
        Is(3, placed, "colocados");
        Is(0, overflow, "caben");
        Is(new System.Drawing.Point(1002, 2), items[1].Pos, "Alfa primero en la zona Steam");
        Is(new System.Drawing.Point(1002, 102), items[0].Pos, "Zeta detrás");
        Is(new System.Drawing.Point(2, 502), items[2].Pos, "Epic va a Juegos (la de Steam no la coge)");
        Is(new System.Drawing.Point(7, 7), items[3].Pos, "Word no se toca");
    }

    static void ZoneFreeCell()
    {
        var zone = new System.Drawing.Rectangle(1000, 0, 400, 300);
        var outside = new[] { new System.Drawing.Point(0, 0), new System.Drawing.Point(5000, 5000) };
        Is(new System.Drawing.Point(1002, 2), Organizer.FreeCellIn(zone, C2, outside)!.Value, "los de fuera no ocupan");
        var inside = new[] { new System.Drawing.Point(1002, 2) };
        Is(new System.Drawing.Point(1002, 102), Organizer.FreeCellIn(zone, C2, inside)!.Value, "salta la ocupada");
    }

    static void ClassifyTargets()
    {
        Is(ItemKind.Steam, Shortcuts.ClassifyTarget(@"D:\SteamLibrary\steamapps\common\Hades\Hades.exe", ""), "Steam");
        Is(ItemKind.Steam, Shortcuts.ClassifyTarget(@"C:\Program Files (x86)\Steam\steam.exe", "-applaunch 1 steam://rungameid/1"), "Steam por argumentos");
        Is(ItemKind.Epic, Shortcuts.ClassifyTarget(@"C:\Program Files\Epic Games\Fortnite\x.exe", ""), "Epic");
        Is(ItemKind.Broken, Shortcuts.ClassifyTarget(@"Z:\no\existe.exe", ""), "roto");
        Is(ItemKind.System, Shortcuts.ClassifyTarget("", ""), "sistema");
        Is(ItemKind.App, Shortcuts.ClassifyTarget(Environment.GetFolderPath(Environment.SpecialFolder.Windows) + @"\explorer.exe", ""), "programa");
    }

    static void SteamUrl()
    {
        string f = Path.Combine(Path.GetTempPath(), "md-test-" + Guid.NewGuid().ToString("N") + ".url");
        File.WriteAllText(f, "[{000214A0-0000-0000-C000-000000000046}]\r\nProp3=19,0\r\n[InternetShortcut]\r\nIDList=\r\nIconIndex=0\r\nURL=steam://rungameid/367520\r\nIconFile=C:\\Steam\\steam\\games\\abc.ico\r\n");
        try
        {
            var info = Shortcuts.Inspect(f);
            Is(ItemKind.Steam, info.Kind, "tipo");
            Is("steam://rungameid/367520", info.Target, "url");
            Is(@"C:\Steam\steam\games\abc.ico", info.IconFile, "icono");
        }
        finally { File.Delete(f); }
    }

    // ---------- Lote ----------

    static string TempDir()
    {
        string d = Path.Combine(Path.GetTempPath(), "md-batch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    static void BatchCleanNames()
    {
        Is("Hollow Knight", Batch.CleanName("Hollow Knight - Acceso directo"), "sufijo");
        Is("HELLDIVERS 2", Batch.CleanName("HELLDIVERS™ 2"), "marca");
        Is("Juego", Batch.CleanName("Juego - Shortcut (2)"), "inglés con número");
        Is("Normal", Batch.CleanName("Normal"), "sin cambios");
    }

    static void BatchRenameUndo()
    {
        string d = TempDir();
        try
        {
            string f = Path.Combine(d, "Juego - Acceso directo.url");
            File.WriteAllText(f, "[InternetShortcut]\r\nURL=steam://rungameid/1\r\n");
            File.WriteAllText(Path.Combine(d, "Juego.url"), "ya existe");
            var step = new BatchStep();
            Batch.Rename(f, Batch.CleanName("Juego - Acceso directo"), step);
            Is(true, File.Exists(Path.Combine(d, "Juego (2).url")), "no pisa el que ya existe");
            foreach (var u in step.Undo) u();
            Is(true, File.Exists(f), "deshecho");
        }
        finally { Directory.Delete(d, true); }
    }

    static void BatchLnkIcon()
    {
        string d = TempDir();
        try
        {
            string notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe");
            string lnk = Path.Combine(d, "Bloc.lnk");
            Shortcuts.CreateLnk(lnk, notepad);
            string shell32 = Path.Combine(Environment.SystemDirectory, "shell32.dll");
            var step = new BatchStep();
            Batch.SetIcon(lnk, shell32, 12, step);
            Is((shell32, 12), Batch.GetIcon(lnk), "icono nuevo");
            foreach (var u in step.Undo) u();
            Is(notepad, Batch.GetIcon(lnk).file, "deshecho");
            Is(ItemKind.App, Shortcuts.Inspect(lnk).Kind, "sigue siendo un programa");
        }
        finally { Directory.Delete(d, true); }
    }

    static void BatchUrlIcon()
    {
        string d = TempDir();
        try
        {
            string url = Path.Combine(d, "Juego.url");
            File.WriteAllText(url, "[{000214A0-0000-0000-C000-000000000046}]\r\nProp3=19,0\r\n[InternetShortcut]\r\nURL=steam://rungameid/5\r\nIconFile=C:\\viejo.ico\r\nIconIndex=0\r\n");
            var step = new BatchStep();
            Batch.SetIcon(url, @"C:\nuevo.ico", 0, step);
            var info = Shortcuts.Inspect(url);
            Is(@"C:\nuevo.ico", info.IconFile, "icono nuevo");
            Is("steam://rungameid/5", info.Target, "la URL no se toca");
            Is(true, File.ReadAllText(url).Contains("Prop3=19,0"), "el resto del archivo tampoco");
            foreach (var u in step.Undo) u();
            Is(@"C:\viejo.ico", Shortcuts.Inspect(url).IconFile, "deshecho");
        }
        finally { Directory.Delete(d, true); }
    }

    static void BatchImageToIco()
    {
        string d = TempDir();
        try
        {
            string png = Path.Combine(d, "portada.png");
            using (var b = new System.Drawing.Bitmap(300, 150)) b.Save(png, System.Drawing.Imaging.ImageFormat.Png);
            string ico = Batch.ImageToIco(png);
            // Cabecera ICO: 8 tamaños, del 16 al 256 (que se escribe como 0). .NET no lee las entradas PNG; Windows sí.
            var bytes = File.ReadAllBytes(ico);
            Is((short)8, BitConverter.ToInt16(bytes, 4), "número de tamaños");
            Is((byte)16, bytes[6], "el primero es de 16 px");
            Is((byte)0, bytes[6 + 7 * 16], "el último es de 256 px");
            File.Delete(ico);
        }
        finally { Directory.Delete(d, true); }
    }

    static void LibraryAcf()
    {
        var kv = Library.ReadAcf("\"AppState\"\n{\n\t\"appid\"\t\t\"367520\"\n\t\"name\"\t\t\"Hollow Knight\"\n\t\"installdir\"\t\t\"Hollow Knight\"\n\t\"UserConfig\"\n\t{\n\t\t\"name\"\t\t\"otro\"\n\t}\n}");
        Is("367520", kv["appid"], "appid");
        Is("Hollow Knight", kv["name"], "nombre (el primero, no el de UserConfig)");
        Is("367520", Library.SteamAppId("steam://rungameid/367520"), "appid de la URL");
    }

    static void LibraryFileNames()
    {
        Is("DRAGON BALL Sparking! ZERO", Library.SafeFileName("DRAGON BALL: Sparking! ZERO").Replace("  ", " "), "sin dos puntos");
        Is("ab", Library.SafeFileName("a\\b").Replace(" ", ""), "sin barras");
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
