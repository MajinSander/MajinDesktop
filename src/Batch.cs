using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.RegularExpressions;

namespace MajinDesktop;

// Un lote de cambios sobre accesos directos, con lo necesario para deshacerlo.
sealed class BatchStep
{
    public string Description = "";
    public readonly List<Action> Undo = new();   // en orden inverso al aplicarse
    public int Count;
    public bool CanUndo = true;
}

static class Batch
{
    // ---------- Nombres ----------

    static readonly Regex Suffix = new(@"\s*-\s*(Acceso directo|Shortcut|Verknüpfung|Raccourci)(\s*\(\d+\))?$", RegexOptions.IgnoreCase);

    public static string CleanName(string name)
    {
        string n = Suffix.Replace(name, "");
        n = n.Replace("™", "").Replace("®", "").Replace("©", "");
        n = Regex.Replace(n, @"\s+", " ").Trim();
        return n.Length == 0 ? name : n;
    }

    public static bool NeedsCleanName(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        return CleanName(name) != name;
    }

    public static void Rename(string path, string newName, BatchStep step)
    {
        string dir = Path.GetDirectoryName(path)!, ext = Path.GetExtension(path);
        string target = Library.UniquePath(dir, Library.SafeFileName(newName), ext);
        if (string.Equals(target, path, StringComparison.OrdinalIgnoreCase)) return;
        File.Move(path, target);
        step.Undo.Insert(0, () => { if (File.Exists(target) && !File.Exists(path)) File.Move(target, path); });
        step.Count++;
    }

    // ---------- Papelera ----------

    public static void Recycle(IList<string> paths, BatchStep step)
    {
        if (paths.Count == 0) return;
        var op = new SHFILEOPSTRUCT
        {
            wFunc = 3, // FO_DELETE
            pFrom = string.Join("\0", paths) + "\0\0",
            fFlags = 0x40 | 0x10 | 0x4 | 0x400, // ALLOWUNDO | NOCONFIRMATION | SILENT | NOERRORUI
        };
        int err = SHFileOperation(ref op);
        if (err != 0) throw new IOException($"No se pudieron mandar a la papelera (código {err})");
        step.Count += paths.Count;
        step.CanUndo = false; // se recuperan desde la Papelera de reciclaje
    }

    // ---------- Iconos ----------

    // Icono que usa ahora el acceso (archivo, índice).
    public static (string file, int index) GetIcon(string path)
    {
        if (Path.GetExtension(path).Equals(".url", StringComparison.OrdinalIgnoreCase))
        {
            var info = Shortcuts.Inspect(path);
            return (info.IconFile, info.IconIndex);
        }
        var link = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)link).Load(path, 0);
            var sb = new StringBuilder(1024);
            link.GetIconLocation(sb, sb.Capacity, out int idx);
            return (sb.ToString(), idx);
        }
        finally { Marshal.ReleaseComObject(link); }
    }

    public static void SetIcon(string path, string iconFile, int index, BatchStep? step)
    {
        var old = GetIcon(path);
        if (Path.GetExtension(path).Equals(".url", StringComparison.OrdinalIgnoreCase)) SetUrlIcon(path, iconFile, index);
        else
        {
            var link = (IShellLinkW)new ShellLink();
            try
            {
                ((IPersistFile)link).Load(path, 2 /* STGM_READWRITE */);
                link.SetIconLocation(iconFile, index);
                ((IPersistFile)link).Save(path, true);
            }
            finally { Marshal.ReleaseComObject(link); }
        }
        Refresh(path);
        if (step == null) return;
        step.Count++;
        step.Undo.Insert(0, () => { if (File.Exists(path)) SetIcon(path, old.file, old.index, null); });
    }

    // Cambia IconFile/IconIndex de un .url conservando el resto del archivo.
    static void SetUrlIcon(string path, string iconFile, int index)
    {
        var lines = File.ReadAllLines(path, Encoding.Default).ToList();
        int section = lines.FindIndex(l => l.Trim().Equals("[InternetShortcut]", StringComparison.OrdinalIgnoreCase));
        if (section < 0) { lines.Add("[InternetShortcut]"); section = lines.Count - 1; }
        void Put(string key, string value)
        {
            int end = lines.FindIndex(section + 1, l => l.TrimStart().StartsWith("["));
            if (end < 0) end = lines.Count;
            int i = lines.FindIndex(section + 1, end - section - 1, l => l.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase));
            if (value.Length == 0) { if (i >= 0) lines.RemoveAt(i); return; }
            if (i >= 0) lines[i] = key + "=" + value;
            else lines.Insert(end, key + "=" + value);
        }
        Put("IconFile", iconFile);
        Put("IconIndex", iconFile.Length == 0 ? "" : index.ToString());
        File.WriteAllLines(path, lines, Encoding.Default);
    }

    // Le dice a Explorer que el acceso ha cambiado, para que vuelva a pintar el icono sin reiniciar.
    public static void Refresh(string path)
    {
        IntPtr p = Marshal.StringToHGlobalUni(path);
        try { SHChangeNotify(0x2000 /* SHCNE_UPDATEITEM */, 0x5 /* SHCNF_PATHW */, p, IntPtr.Zero); }
        finally { Marshal.FreeHGlobal(p); }
        ShellImages.Forget(path);
    }

    // Convierte una imagen (PNG, JPG…) en un .ico de varios tamaños, centrada en un cuadrado transparente.
    public static string ImageToIco(string imagePath)
    {
        string dir = Path.Combine(Config.Dir, "icons");
        Directory.CreateDirectory(dir);
        string name = Library.SafeFileName(Path.GetFileNameWithoutExtension(imagePath));
        string ico = Library.UniquePath(dir, name, ".ico");
        using var src = Image.FromFile(imagePath);
        Ico.Write(ico, size =>
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float scale = Math.Min((float)size / src.Width, (float)size / src.Height);
            float w = src.Width * scale, h = src.Height * scale;
            g.DrawImage(src, (size - w) / 2, (size - h) / 2, w, h);
            return bmp;
        });
        return ico;
    }

    // ---------- Win32 / COM ----------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    [DllImport("shell32.dll")]
    static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

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
