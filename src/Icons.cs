using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;

namespace MajinDesktop;

// Posición de un icono del escritorio, guardada respecto a la esquina de su pantalla: así sigue en su
// monitor aunque cambie cuál es la principal o el orden de las pantallas.
[DataContract]
sealed class IconPos
{
    [DataMember(Name = "key", Order = 1)] public string Key = "";       // ruta de análisis (archivo o ::{GUID})
    [DataMember(Name = "name", Order = 2)] public string Name = "";
    [DataMember(Name = "monitor", Order = 3)] public string Monitor = ""; // MonitorState.Path
    [DataMember(Name = "edid", Order = 4)] public string Edid = "";
    [DataMember(Name = "dx", Order = 5)] public int Dx;
    [DataMember(Name = "dy", Order = 6)] public int Dy;

    [OnDeserializing]
    void Defaults(StreamingContext _) { Key = Name = Monitor = Edid = ""; }
}

sealed class IconApplyResult
{
    public int Moved, Missing, NoMonitor;
    public bool AutoArrangeDisabled;
}

// Lee y coloca los iconos del escritorio con la vía documentada de Windows:
// IShellWindows → navegador del escritorio → vista activa → IFolderView2.
static class Icons
{
    // ---------- Cálculo puro (se prueba en tests/) ----------

    internal static MonitorState? MonitorAt(Point p, IList<MonitorState> mons)
    {
        var inside = mons.FirstOrDefault(m => p.X >= m.X && p.X < m.X + m.Width && p.Y >= m.Y && p.Y < m.Y + m.Height);
        if (inside != null) return inside;
        // Fuera de todas (icono medio salido): la pantalla más cercana.
        return mons.OrderBy(m => Distance(p, m)).FirstOrDefault();
    }

    static long Distance(Point p, MonitorState m)
    {
        long dx = Math.Max(Math.Max(m.X - p.X, 0), p.X - (m.X + m.Width - 1));
        long dy = Math.Max(Math.Max(m.Y - p.Y, 0), p.Y - (m.Y + m.Height - 1));
        return dx * dx + dy * dy;
    }

    internal static IconPos? ToRelative(string key, string name, Point screen, IList<MonitorState> mons)
    {
        var m = MonitorAt(screen, mons);
        if (m == null) return null;
        return new IconPos { Key = key, Name = name, Monitor = m.Path, Edid = m.Edid, Dx = screen.X - m.X, Dy = screen.Y - m.Y };
    }

    // Dónde va el icono con las pantallas de ahora. null si su pantalla no está conectada.
    // cell = tamaño de la celda de un icono, para que no se quede fuera si la pantalla ahora es más pequeña.
    internal static Point? ToScreen(IconPos icon, IList<MonitorState> mons, Size cell)
    {
        var m = mons.FirstOrDefault(o => o.Path.Length > 0 && string.Equals(o.Path, icon.Monitor, StringComparison.OrdinalIgnoreCase));
        if (m == null && icon.Edid.Length > 0 && mons.Count(o => o.Edid == icon.Edid) == 1)
            m = mons.First(o => o.Edid == icon.Edid);
        if (m == null) return null;
        // Como mucho media celda fuera: Windows coloca los iconos de la última columna casi pegados al borde.
        int x = Math.Max(0, Math.Min(icon.Dx, m.Width - cell.Width / 2));
        int y = Math.Max(0, Math.Min(icon.Dy, m.Height - cell.Height / 2));
        return new Point(m.X + x, m.Y + y);
    }

    // ---------- Escritorio real ----------

    public static List<IconPos> Snapshot(IList<MonitorState> mons)
    {
        using var desk = DesktopView.Open();
        var list = new List<IconPos>();
        foreach (var item in desk.Items())
        {
            var screen = desk.ToScreen(item.Position);
            if (ToRelative(item.Key, item.Name, screen, mons) is { } pos) list.Add(pos);
        }
        return list;
    }

    // dryRun: solo cuenta lo que movería, sin tocar el escritorio.
    public static IconApplyResult Apply(IList<IconPos> layout, IList<MonitorState> mons, bool dryRun = false)
    {
        var result = new IconApplyResult();
        using var desk = DesktopView.Open();
        var items = desk.Items().GroupBy(i => i.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var cell = desk.Spacing;
        var pidls = new List<IntPtr>();
        var points = new List<Point>();
        foreach (var icon in layout)
        {
            if (!items.TryGetValue(icon.Key, out var item)) { result.Missing++; continue; }
            if (ToScreen(icon, mons, cell) is not { } screen) { result.NoMonitor++; continue; }
            var view = desk.ToView(screen);
            if (view == item.Position) continue;
            pidls.Add(item.Pidl);
            points.Add(view);
        }
        if (dryRun) { result.Moved = pidls.Count; return result; }
        if (pidls.Count > 0)
        {
            // Con "Organizar automáticamente" Windows ignora las posiciones: hay que quitarlo.
            result.AutoArrangeDisabled = desk.DisableAutoArrange();
            desk.Position(pidls, points);
            result.Moved = pidls.Count;
        }
        return result;
    }

    // Para el organizador: cada icono con su posición en pantalla, y el tamaño de celda de la rejilla.
    public static List<(string Key, string Name, Point Pos)> Read(out Size cell)
    {
        using var desk = DesktopView.Open();
        cell = desk.Spacing;
        return desk.Items().Select(i => (i.Key, i.Name, desk.ToScreen(i.Position))).ToList();
    }

    // Mueve los iconos indicados (clave → esquina en pantalla). Devuelve cuántos se movieron.
    public static int MoveTo(IDictionary<string, Point> targets, out bool autoArrangeDisabled)
    {
        autoArrangeDisabled = false;
        using var desk = DesktopView.Open();
        var pidls = new List<IntPtr>();
        var points = new List<Point>();
        foreach (var item in desk.Items())
        {
            if (!targets.TryGetValue(item.Key, out var screen)) continue;
            var view = desk.ToView(screen);
            if (view == item.Position) continue;
            pidls.Add(item.Pidl);
            points.Add(view);
        }
        if (pidls.Count == 0) return 0;
        autoArrangeDisabled = desk.DisableAutoArrange();
        desk.Position(pidls, points);
        return pidls.Count;
    }

    public static int Count()
    {
        using var desk = DesktopView.Open();
        return desk.Items().Count();
    }
}

// Vista de carpeta del escritorio (la de Explorer). Libera los PIDL al cerrarse.
sealed class DesktopView : IDisposable
{
    public readonly struct Item
    {
        public readonly IntPtr Pidl;
        public readonly string Key, Name;
        public readonly Point Position;
        public Item(IntPtr pidl, string key, string name, Point position) { Pidl = pidl; Key = key; Name = name; Position = position; }
    }

    readonly IFolderView2 view;
    readonly IntPtr listView;
    readonly List<IntPtr> pidls = new();

    DesktopView(IFolderView2 view, IntPtr listView)
    {
        this.view = view;
        this.listView = listView;
    }

    public static DesktopView Open()
    {
        var shellWindows = (IShellWindows)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"))!)!;
        object loc = 0 /* CSIDL_DESKTOP */;
        object? root = null; // VT_EMPTY
        var disp = shellWindows.FindWindowSW(ref loc, ref root!, 8 /* SWC_DESKTOP */, out _, 1 /* SWFO_NEEDDISPATCH */)
            ?? throw new COMException("No encuentro el escritorio de Explorer");
        var sid = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");   // SID_STopLevelBrowser
        var iid = new Guid("000214E2-0000-0000-C000-000000000046");   // IID_IShellBrowser
        ((IServiceProvider)disp).QueryService(ref sid, ref iid, out object browserObj);
        ((IShellBrowser)browserObj).QueryActiveShellView(out object viewObj);
        return new DesktopView((IFolderView2)viewObj, FindDesktopList());
    }

    public IEnumerable<Item> Items()
    {
        view.ItemCount(2 /* SVGIO_ALLVIEW */, out int count);
        var iidItem = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"); // IID_IShellItem
        for (int i = 0; i < count; i++)
        {
            view.Item(i, out IntPtr pidl);
            pidls.Add(pidl);
            view.GetItemPosition(pidl, out POINT pt);
            view.GetItem(i, ref iidItem, out object itemObj);
            var shellItem = (IShellItem)itemObj;
            string key = Name(shellItem, 0x80028000 /* SIGDN_DESKTOPABSOLUTEPARSING */);
            string name = Name(shellItem, 0 /* SIGDN_NORMALDISPLAY */);
            Marshal.ReleaseComObject(shellItem);
            yield return new Item(pidl, key, name, new Point(pt.x, pt.y));
        }
    }

    static string Name(IShellItem item, uint sigdn)
    {
        item.GetDisplayName(sigdn, out IntPtr p);
        try { return Marshal.PtrToStringUni(p) ?? ""; }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    // La lista de iconos cuelga de Progman o, con fondos animados (Wallpaper Engine), de un WorkerW.
    // Solo se lee su posición; no se toca ninguna de estas ventanas.
    static IntPtr FindDesktopList()
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((top, _) =>
        {
            IntPtr defView = FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView == IntPtr.Zero) return true;
            found = FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
            return found == IntPtr.Zero;
        }, IntPtr.Zero);
        return found;
    }

    // Tamaño de la celda de un icono (separación de la cuadrícula).
    public Size Spacing
    {
        get
        {
            var pt = new POINT();
            view.GetSpacing(ref pt);
            return new Size(pt.x, pt.y);
        }
    }

    // Coordenadas de la vista ↔ pantalla: el origen es la esquina de la lista de iconos del escritorio.
    Point Origin
    {
        get
        {
            var o = new POINT();
            if (listView != IntPtr.Zero) ClientToScreen(listView, ref o);
            return new Point(o.x, o.y);
        }
    }

    public Point ToScreen(Point view) { var o = Origin; return new Point(view.X + o.X, view.Y + o.Y); }
    public Point ToView(Point screen) { var o = Origin; return new Point(screen.X - o.X, screen.Y - o.Y); }

    // Devuelve true si estaba activado y se ha quitado.
    public bool DisableAutoArrange()
    {
        view.GetCurrentFolderFlags(out uint flags);
        if ((flags & 0x1) == 0) return false; // FWF_AUTOARRANGE
        view.SetCurrentFolderFlags(0x1, 0);
        return true;
    }

    public void Position(IList<IntPtr> items, IList<Point> points)
    {
        var pts = points.Select(p => new POINT { x = p.X, y = p.Y }).ToArray();
        view.SelectAndPositionItems((uint)items.Count, items.ToArray(), pts, 0x80 /* SVSI_POSITIONITEM */);
    }

    public void Dispose()
    {
        foreach (var p in pidls) Marshal.FreeCoTaskMem(p);
        pidls.Clear();
        Marshal.ReleaseComObject(view);
    }

    // ---------- COM ----------

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int x, y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? title);

    delegate bool EnumProc(IntPtr hwnd, IntPtr param);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumProc proc, IntPtr param);

    [DllImport("user32.dll")]
    static extern bool ClientToScreen(IntPtr hwnd, ref POINT pt);

    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    interface IShellWindows
    {
        int Count { get; }
        [return: MarshalAs(UnmanagedType.IDispatch)] object Item([In, MarshalAs(UnmanagedType.Struct)] object index);
        [return: MarshalAs(UnmanagedType.IUnknown)] object _NewEnum();
        void Register([MarshalAs(UnmanagedType.IDispatch)] object pid, int hwnd, int swClass, out int cookie);
        void RegisterPending(int threadId, [In, MarshalAs(UnmanagedType.Struct)] ref object loc, [In, MarshalAs(UnmanagedType.Struct)] ref object root, int swClass, out int cookie);
        void Revoke(int cookie);
        void OnNavigate(int cookie, [In, MarshalAs(UnmanagedType.Struct)] ref object loc);
        void OnActivated(int cookie, bool active);
        [return: MarshalAs(UnmanagedType.IDispatch)]
        object FindWindowSW([In, MarshalAs(UnmanagedType.Struct)] ref object loc, [In, MarshalAs(UnmanagedType.Struct)] ref object root, int swClass, out int hwnd, int options);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IServiceProvider
    {
        void QueryService(ref Guid service, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellBrowser
    {
        void GetWindow(out IntPtr hwnd);
        void ContextSensitiveHelp(bool enter);
        void InsertMenusSB(IntPtr menu, IntPtr widths);
        void SetMenuSB(IntPtr menu, IntPtr olemenu, IntPtr active);
        void RemoveMenusSB(IntPtr menu);
        void SetStatusTextSB(IntPtr text);
        void EnableModelessSB(bool enable);
        void TranslateAcceleratorSB(IntPtr msg, short id);
        void BrowseObject(IntPtr pidl, uint flags);
        void GetViewStateStream(uint mode, out IntPtr stream);
        void GetControlWindow(uint id, out IntPtr hwnd);
        void SendControlMsg(uint id, uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result);
        void QueryActiveShellView([MarshalAs(UnmanagedType.IUnknown)] out object view);
    }

    [ComImport, Guid("1AF3A467-214F-4298-908E-06B03E0B39F9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFolderView2
    {
        // IFolderView
        void GetCurrentViewMode(out uint mode);
        void SetCurrentViewMode(uint mode);
        void GetFolder(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object folder);
        void Item(int index, out IntPtr pidl);
        void ItemCount(uint flags, out int count);
        void Items(uint flags, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object items);
        void GetSelectionMarkedItem(out int item);
        void GetFocusedItem(out int item);
        void GetItemPosition(IntPtr pidl, out POINT pt);
        void GetSpacing(ref POINT pt);
        void GetDefaultSpacing(out POINT pt);
        [PreserveSig] int GetAutoArrange();
        void SelectItem(int item, uint flags);
        void SelectAndPositionItems(uint count, [In, MarshalAs(UnmanagedType.LPArray)] IntPtr[] pidls,
            [In, MarshalAs(UnmanagedType.LPArray)] POINT[] points, uint flags);
        // IFolderView2
        void SetGroupBy(IntPtr key, bool ascending);
        void GetGroupBy(IntPtr key, out bool ascending);
        void SetViewProperty(IntPtr pidl, IntPtr key, IntPtr value);
        void GetViewProperty(IntPtr pidl, IntPtr key, IntPtr value);
        void SetTileViewProperties(IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string props);
        void SetExtendedTileViewProperties(IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string props);
        void SetText(int type, [MarshalAs(UnmanagedType.LPWStr)] string text);
        void SetCurrentFolderFlags(uint mask, uint flags);
        void GetCurrentFolderFlags(out uint flags);
        void GetSortColumnCount(out int count);
        void SetSortColumns(IntPtr columns, int count);
        void GetSortColumns(IntPtr columns, int count);
        void GetItem(int index, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object item);
        void GetVisibleItem(int start, bool previous, out int item);
        void GetSelectedItem(int start, out int item);
        void GetSelection(bool noneImpliesFolder, out IntPtr items);
        void GetSelectionState(IntPtr pidl, out uint flags);
        void InvokeVerbOnSelection([MarshalAs(UnmanagedType.LPStr)] string verb);
        void SetViewModeAndIconSize(int mode, int size);
        void GetViewModeAndIconSize(out int mode, out int size);
        void SetGroupSubsetCount(uint visibleRows);
        void GetGroupSubsetCount(out uint visibleRows);
        void SetRedraw(bool redraw);
        void IsMoveInSameFolder();
        void DoRename();
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        void BindToHandler(IntPtr bc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint sigdn, out IntPtr name);
        void GetAttributes(uint mask, out uint attribs);
        void Compare(IShellItem other, uint hint, out int order);
    }
}
