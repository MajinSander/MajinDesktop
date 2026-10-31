using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace MajinDesktop;

// Qué es cada cosa del escritorio. El orden es el de "Ordenar por tipo".
enum ItemKind { Steam, Epic, Gog, Ea, Ubisoft, Battlenet, Game, App, Web, Folder, File, System, Broken }

sealed class ShortcutInfo
{
    public ItemKind Kind;
    public string Target = "";   // exe, carpeta o URL a la que apunta
    public string Arguments = "";
    public string IconFile = "";
    public int IconIndex;
}

static class Shortcuts
{
    public static string KindName(ItemKind k) => k switch
    {
        ItemKind.Steam => "Steam",
        ItemKind.Epic => "Epic Games",
        ItemKind.Gog => "GOG",
        ItemKind.Ea => "EA",
        ItemKind.Ubisoft => "Ubisoft",
        ItemKind.Battlenet => "Battle.net",
        ItemKind.Game => "Juego",
        ItemKind.App => "Programa",
        ItemKind.Web => "Enlace web",
        ItemKind.Folder => "Carpeta",
        ItemKind.File => "Archivo",
        ItemKind.System => "Sistema",
        _ => "Roto",
    };

    public static bool IsGame(ItemKind k) => k <= ItemKind.Game;

    // key = ruta de análisis del icono del escritorio (archivo, carpeta o ::{GUID}).
    public static ShortcutInfo Inspect(string key)
    {
        if (key.StartsWith("::", StringComparison.Ordinal)) return new ShortcutInfo { Kind = ItemKind.System };
        if (Directory.Exists(key)) return new ShortcutInfo { Kind = ItemKind.Folder, Target = key };
        string ext = Path.GetExtension(key).ToLowerInvariant();
        try
        {
            if (ext == ".url") return ReadUrl(key);
            if (ext == ".lnk") return ReadLnk(key);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
        {
            Log.Error("Leer acceso directo " + key, ex);
        }
        return new ShortcutInfo { Kind = ext == ".exe" ? ItemKind.App : ItemKind.File, Target = key };
    }

    // Accesos de internet (.url), que es lo que crea Steam: [InternetShortcut] URL=steam://rungameid/123
    static ShortcutInfo ReadUrl(string path)
    {
        var info = new ShortcutInfo();
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            int eq = line.IndexOf('=');
            if (eq < 0) continue;
            string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
            if (k.Equals("URL", StringComparison.OrdinalIgnoreCase)) info.Target = v;
            else if (k.Equals("IconFile", StringComparison.OrdinalIgnoreCase)) info.IconFile = v;
            else if (k.Equals("IconIndex", StringComparison.OrdinalIgnoreCase) && int.TryParse(v, out int i)) info.IconIndex = i;
        }
        string url = info.Target.ToLowerInvariant();
        info.Kind = url.StartsWith("steam://") ? ItemKind.Steam
            : url.StartsWith("com.epicgames.launcher://") ? ItemKind.Epic
            : url.StartsWith("goggalaxy://") ? ItemKind.Gog
            : url.StartsWith("origin2://") || url.StartsWith("link2ea://") ? ItemKind.Ea
            : url.StartsWith("uplay://") ? ItemKind.Ubisoft
            : url.StartsWith("battlenet://") ? ItemKind.Battlenet
            : url.StartsWith("http") ? ItemKind.Web
            : ItemKind.File;
        return info;
    }

    static ShortcutInfo ReadLnk(string path)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)link).Load(path, 0);
            var sb = new StringBuilder(1024);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0x4 /* SLGP_RAWPATH */);
            string target = Environment.ExpandEnvironmentVariables(sb.ToString());
            sb.Clear();
            link.GetArguments(sb, sb.Capacity);
            string args = sb.ToString();
            sb.Clear();
            link.GetIconLocation(sb, sb.Capacity, out int iconIndex);
            var info = new ShortcutInfo { Target = target, Arguments = args, IconFile = sb.ToString(), IconIndex = iconIndex };
            info.Kind = ClassifyTarget(target, args);
            return info;
        }
        finally { Marshal.ReleaseComObject(link); }
    }

    internal static ItemKind ClassifyTarget(string target, string args)
    {
        if (target.Length == 0) return ItemKind.System; // accesos a cosas del sistema (Panel de control, apps de la Tienda…)
        string t = target.ToLowerInvariant(), a = args.ToLowerInvariant();
        if (t.Contains(@"\steamapps\common\") || (t.EndsWith(@"\steam.exe") && a.Contains("rungameid"))) return ItemKind.Steam;
        if (t.Contains(@"\epic games\") && !t.Contains(@"\launcher\")) return ItemKind.Epic;
        if (t.Contains(@"\gog galaxy\games\") || t.Contains(@"\gog games\")) return ItemKind.Gog;
        if (t.Contains(@"\ea games\")) return ItemKind.Ea;
        if (t.Contains(@"\ubisoft game launcher\games\")) return ItemKind.Ubisoft;
        bool exists = File.Exists(target) || Directory.Exists(target);
        if (!exists && !t.StartsWith("http") && !t.Contains("://")) return ItemKind.Broken;
        if (Directory.Exists(target)) return ItemKind.Folder;
        if (t.Contains(@"\games\") || t.Contains(@"\juegos\")) return ItemKind.Game;
        return t.EndsWith(".exe") ? ItemKind.App : ItemKind.File;
    }

    public static void CreateLnk(string linkPath, string target, string description = "", string arguments = "", string? iconFile = null, int iconIndex = 0)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(target);
            link.SetWorkingDirectory(Path.GetDirectoryName(target) ?? "");
            if (arguments.Length > 0) link.SetArguments(arguments);
            if (description.Length > 0) link.SetDescription(description);
            link.SetIconLocation(iconFile ?? target, iconFile == null ? 0 : iconIndex);
            ((IPersistFile)link).Save(linkPath, true);
        }
        finally { Marshal.ReleaseComObject(link); }
    }

    // ---------- COM ----------

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr fd, int flags);
        void GetIDList(out IntPtr pidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int cmd);
        void SetShowCmd(int cmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int cch, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, int reserved);
        void Resolve(IntPtr hwnd, int flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
