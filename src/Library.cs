using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace MajinDesktop;

sealed class Game
{
    public string Platform = "";      // Steam, Epic, GOG
    public ItemKind Kind;
    public string Id = "";            // appid de Steam, AppName de Epic, id de GOG
    public string Name = "";
    public string InstallDir = "";
    public string LaunchUrl = "";     // steam://rungameid/…, com.epicgames.launcher://…
    public string Exe = "";           // para GOG (y para sacar el icono en Epic)
    public string IconFile = "";      // .ico/.exe con el icono
    public string IconHash = "";      // Steam: clienticon (para descargarlo si falta)
    public string? Shortcut;          // acceso directo que ya tiene en el escritorio
}

// Juegos instalados en Steam, Epic y GOG (solo lectura de sus archivos y del registro).
static class Library
{
    public static string? SteamRoot()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        var p = (k?.GetValue("SteamPath") as string)?.Replace('/', '\\');
        return p != null && Directory.Exists(p) ? p : null;
    }

    public static List<Game> Scan()
    {
        var games = new List<Game>();
        try { games.AddRange(Steam()); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException) { Log.Error("Biblioteca de Steam", ex); }
        try { games.AddRange(Epic()); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException) { Log.Error("Biblioteca de Epic", ex); }
        try { games.AddRange(Gog()); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { Log.Error("Biblioteca de GOG", ex); }
        return games.OrderBy(g => Organizer.SortName(g.Name), StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    // ---------- Steam ----------

    // Herramientas que Steam instala como "apps" y no son juegos.
    static readonly HashSet<string> SteamTools = new() { "228980", "1070560", "1391110", "1493710", "1628350", "2180100", "250820", "1826330", "1887720" };

    static IEnumerable<Game> Steam()
    {
        var root = SteamRoot();
        if (root == null) yield break;
        var libs = new List<string> { root };
        string vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));

        var found = new Dictionary<string, Game>();
        foreach (var lib in libs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var acf in Directory.GetFiles(apps, "appmanifest_*.acf"))
            {
                var kv = ReadAcf(File.ReadAllText(acf));
                if (!kv.TryGetValue("appid", out var id) || found.ContainsKey(id) || SteamTools.Contains(id)) continue;
                kv.TryGetValue("name", out var name);
                kv.TryGetValue("installdir", out var dir);
                if (name == null || name.IndexOf("Redistributable", StringComparison.OrdinalIgnoreCase) >= 0 || name.StartsWith("Proton", StringComparison.Ordinal)) continue;
                found[id] = new Game
                {
                    Platform = "Steam", Kind = ItemKind.Steam, Id = id, Name = name,
                    InstallDir = dir == null ? "" : Path.Combine(apps, "common", dir),
                    LaunchUrl = "steam://rungameid/" + id,
                };
            }
        }

        // Icono (clienticon) y tipo de cada app desde la caché de Steam.
        var info = AppInfo.Read(Path.Combine(root, "appcache", "appinfo.vdf"), found.Keys);
        foreach (var g in found.Values)
        {
            if (info.TryGetValue(g.Id, out var a))
            {
                if (a.Type.Length > 0 && !a.Type.Equals("game", StringComparison.OrdinalIgnoreCase)) continue; // herramientas, demos de SDK…
                g.IconHash = a.ClientIcon;
                if (a.ClientIcon.Length > 0) g.IconFile = Path.Combine(root, "steam", "games", a.ClientIcon + ".ico");
            }
            yield return g;
        }
    }

    internal static Dictionary<string, string> ReadAcf(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Regex.Matches(text, "^\\s*\"(\\w+)\"\\s+\"([^\"]*)\"", RegexOptions.Multiline))
            if (!d.ContainsKey(m.Groups[1].Value)) d[m.Groups[1].Value] = m.Groups[2].Value;
        return d;
    }

    // ---------- Epic ----------

    static IEnumerable<Game> Epic()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
        if (!Directory.Exists(dir)) yield break;
        foreach (var file in Directory.GetFiles(dir, "*.item"))
        {
            string json = File.ReadAllText(file);
            string Get(string key) => Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"").Groups[1].Value.Replace(@"\\", @"\");
            string name = Get("DisplayName"), app = Get("AppName"), ns = Get("CatalogNamespace"), item = Get("CatalogItemId");
            string location = Get("InstallLocation"), exe = Get("LaunchExecutable");
            if (name.Length == 0 || app.Length == 0 || json.Contains("\"bIsApplication\": false")) continue;
            string full = location.Length > 0 && exe.Length > 0 ? Path.Combine(location, exe) : "";
            yield return new Game
            {
                Platform = "Epic", Kind = ItemKind.Epic, Id = app, Name = name, InstallDir = location, Exe = full,
                LaunchUrl = $"com.epicgames.launcher://apps/{ns}%3A{item}%3A{app}?action=launch&silent=true",
                IconFile = File.Exists(full) ? full : "",
            };
        }
    }

    // ---------- GOG ----------

    static IEnumerable<Game> Gog()
    {
        using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
        if (root == null) yield break;
        foreach (var id in root.GetSubKeyNames())
        {
            using var k = root.OpenSubKey(id);
            string name = k?.GetValue("gameName") as string ?? "", exe = k?.GetValue("exe") as string ?? "", path = k?.GetValue("path") as string ?? "";
            if (name.Length == 0 || !File.Exists(exe)) continue;
            yield return new Game { Platform = "GOG", Kind = ItemKind.Gog, Id = id, Name = name, InstallDir = path, Exe = exe, IconFile = exe };
        }
    }

    // ---------- Accesos que ya hay ----------

    // Marca en cada juego el acceso directo que ya tiene en el escritorio (por su URL o por su carpeta).
    public static void MatchShortcuts(IList<Game> games, IEnumerable<(string Path, ShortcutInfo Info)> shortcuts)
    {
        var list = shortcuts.ToList();
        foreach (var g in games)
        {
            var hit = list.FirstOrDefault(s =>
                (g.Platform == "Steam" && Regex.IsMatch(s.Info.Target + " " + s.Info.Arguments, @"rungameid/" + g.Id + @"(\D|$)")) ||
                (g.Platform == "Epic" && s.Info.Target.IndexOf("%3A" + g.Id, StringComparison.OrdinalIgnoreCase) >= 0) ||
                (g.InstallDir.Length > 3 && s.Info.Target.StartsWith(g.InstallDir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)));
            g.Shortcut = hit.Path;
        }
    }

    // ---------- Crear accesos ----------

    public static string SafeFileName(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => bad.Contains(c) ? ' ' : c).ToArray());
        return Regex.Replace(clean, @"\s+", " ").Trim().TrimEnd('.');
    }

    public static string UniquePath(string dir, string name, string ext)
    {
        string p = Path.Combine(dir, name + ext);
        for (int i = 2; File.Exists(p) || Directory.Exists(p); i++) p = Path.Combine(dir, $"{name} ({i}){ext}");
        return p;
    }

    // Crea el acceso en el escritorio y devuelve su ruta.
    public static string CreateShortcut(Game g, string desktop)
    {
        string name = SafeFileName(g.Name);
        if (g.LaunchUrl.Length > 0)
        {
            string path = UniquePath(desktop, name, ".url");
            var sb = new StringBuilder("[InternetShortcut]\r\n");
            sb.Append("URL=").Append(g.LaunchUrl).Append("\r\n");
            if (g.IconFile.Length > 0) sb.Append("IconIndex=0\r\nIconFile=").Append(g.IconFile).Append("\r\n");
            File.WriteAllText(path, sb.ToString(), Encoding.Default);
            return path;
        }
        string lnk = UniquePath(desktop, name, ".lnk");
        Shortcuts.CreateLnk(lnk, g.Exe, g.Name);
        return lnk;
    }

    // ---------- Iconos de Steam ----------

    // Descarga el .ico de un juego de Steam de su CDN (lo que Steam haría si el icono no está).
    // Lo deja donde Steam lo busca; si esa carpeta no deja escribir, en la carpeta de la app.
    public static string DownloadSteamIcon(string appId, string hash, string wantedPath)
    {
        string url = $"https://cdn.cloudflare.steamstatic.com/steamcommunity/public/images/apps/{appId}/{hash}.ico";
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        using var web = new WebClient();
        web.Headers[HttpRequestHeader.UserAgent] = "MajinDesktop";
        byte[] data = web.DownloadData(url);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(wantedPath)!);
            File.WriteAllBytes(wantedPath, data);
            return wantedPath;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            string mine = Path.Combine(Config.Dir, "icons", hash + ".ico");
            Directory.CreateDirectory(Path.GetDirectoryName(mine)!);
            File.WriteAllBytes(mine, data);
            return mine;
        }
    }

    // appid de un .url de Steam, o "" si no lo es.
    public static string SteamAppId(string url) => Regex.Match(url, @"rungameid/(\d+)").Groups[1].Value;
}

// Lectura de appinfo.vdf (caché binaria de Steam). Formatos v27, v28 y v29 (con tabla de cadenas).
static class AppInfo
{
    public sealed class App { public string Type = "", ClientIcon = "", Name = ""; }

    public static Dictionary<string, App> Read(string path, IEnumerable<string> appIds)
    {
        var wanted = new HashSet<uint>(appIds.Select(a => uint.TryParse(a, out var v) ? v : 0).Where(v => v != 0));
        var result = new Dictionary<string, App>();
        if (!File.Exists(path) || wanted.Count == 0) return result;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new BinaryReader(fs);
        uint magic = r.ReadUInt32();
        r.ReadUInt32(); // universe
        int version = magic switch { 0x07564427 => 27, 0x07564428 => 28, 0x07564429 => 29, _ => 0 };
        if (version == 0) throw new FormatException($"appinfo.vdf con un formato desconocido ({magic:X8})");

        string[]? strings = null;
        if (version >= 29)
        {
            long tableOffset = r.ReadInt64();
            long back = fs.Position;
            fs.Position = tableOffset;
            int count = (int)r.ReadUInt32();
            strings = new string[count];
            for (int i = 0; i < count; i++) strings[i] = ReadCString(r);
            fs.Position = back;
        }

        while (fs.Position < fs.Length)
        {
            uint appid = r.ReadUInt32();
            if (appid == 0) break;
            uint size = r.ReadUInt32();
            long next = fs.Position + size;
            if (wanted.Contains(appid))
            {
                r.ReadUInt32(); r.ReadUInt32(); r.ReadUInt64();   // infoState, lastUpdated, picsToken
                r.ReadBytes(20); r.ReadUInt32();                  // sha1, changeNumber
                if (version >= 28) r.ReadBytes(20);               // sha1 del binario
                var app = new App();
                ReadNode(r, strings, new List<string>(), app);
                result[appid.ToString()] = app;
                if (result.Count == wanted.Count) break;
            }
            fs.Position = next;
        }
        return result;
    }

    static string ReadCString(BinaryReader r)
    {
        var bytes = new List<byte>();
        for (byte b; (b = r.ReadByte()) != 0;) bytes.Add(b);
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    // KeyValues binario. Solo se guardan appinfo/common/{type, clienticon, name}.
    static void ReadNode(BinaryReader r, string[]? strings, List<string> path, App app)
    {
        while (true)
        {
            byte type = r.ReadByte();
            if (type == 0x08) return;
            string key = strings != null ? strings[r.ReadInt32()] : ReadCString(r);
            switch (type)
            {
                case 0x00:
                    path.Add(key);
                    ReadNode(r, strings, path, app);
                    path.RemoveAt(path.Count - 1);
                    break;
                case 0x01:
                    string value = ReadCString(r);
                    if (path.Count == 2 && path[1] == "common")
                    {
                        if (key == "type") app.Type = value;
                        else if (key == "clienticon") app.ClientIcon = value;
                        else if (key == "name") app.Name = value;
                    }
                    break;
                case 0x02: case 0x03: case 0x04: case 0x06: r.ReadInt32(); break;
                case 0x07: case 0x0A: r.ReadInt64(); break;
                case 0x05:
                    while (r.ReadUInt16() != 0) { }
                    break;
                default: throw new FormatException($"Tipo desconocido en appinfo.vdf: {type:X2}");
            }
        }
    }
}
