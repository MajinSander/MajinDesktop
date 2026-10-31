using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;

namespace MajinDesktop;

// Icono de cada elemento del escritorio tal como lo pinta Explorer (48 px, con transparencia).
static class ShellImages
{
    static readonly Dictionary<string, Bitmap?> cache = new(StringComparer.OrdinalIgnoreCase);
    static IntPtr imageList;

    public static Bitmap? Get(string key)
    {
        if (cache.TryGetValue(key, out var hit)) return hit;
        Bitmap? bmp = null;
        try { bmp = Load(key); }
        catch (Exception ex) when (ex is COMException or ExternalException or ArgumentException) { }
        cache[key] = bmp;
        return bmp;
    }

    public static void Forget(string key)
    {
        if (cache.TryGetValue(key, out var bmp)) bmp?.Dispose();
        cache.Remove(key);
    }

    static Bitmap? Load(string key)
    {
        if (SHParseDisplayName(key, IntPtr.Zero, out IntPtr pidl, 0, out _) != 0 || pidl == IntPtr.Zero) return null;
        var info = new SHFILEINFO();
        try
        {
            if (SHGetFileInfo(pidl, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), 0x8 /* SHGFI_PIDL */ | 0x4000 /* SHGFI_SYSICONINDEX */) == IntPtr.Zero)
                return null;
        }
        finally { Marshal.FreeCoTaskMem(pidl); }

        if (imageList == IntPtr.Zero)
        {
            var iid = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950"); // IID_IImageList
            if (SHGetImageList(2 /* SHIL_EXTRALARGE: 48 px */, ref iid, out imageList) != 0) return null;
        }
        IntPtr hicon = ImageList_GetIcon(imageList, info.iIcon, 0x1 /* ILD_TRANSPARENT */);
        if (hicon == IntPtr.Zero) return null;
        try
        {
            using var icon = Icon.FromHandle(hicon);
            return icon.ToBitmap();
        }
        finally { DestroyIcon(hicon); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHParseDisplayName(string name, IntPtr bindCtx, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SHGetFileInfo(IntPtr pidl, uint attributes, ref SHFILEINFO info, uint size, uint flags);

    [DllImport("shell32.dll")]
    static extern int SHGetImageList(int list, ref Guid riid, out IntPtr imageList);

    [DllImport("comctl32.dll")]
    static extern IntPtr ImageList_GetIcon(IntPtr imageList, int index, uint flags);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr icon);
}
