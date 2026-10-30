using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MajinDesktop;

// Ventana invisible (solo mensajes) que recibe los atajos globales y el aviso de "ya estoy abierto".
sealed class HotkeyWindow : NativeWindow, IDisposable
{
    public const int WM_SHOW_SETTINGS = 0x8000 + 1; // WM_APP + 1

    readonly Action<int> onHotkey;
    readonly Action onShow;
    readonly List<int> ids = new();

    [DllImport("user32.dll")]
    static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    public HotkeyWindow(Action<int> onHotkey, Action onShow)
    {
        this.onHotkey = onHotkey;
        this.onShow = onShow;
        CreateHandle(new CreateParams { Parent = (IntPtr)(-3) }); // HWND_MESSAGE
    }

    public bool Register(int id, Hotkey h)
    {
        if (h.IsEmpty) return true;
        if (!Native.RegisterHotKey(Handle, id, h.Modifiers | Native.MOD_NOREPEAT, (uint)h.Key)) return false;
        ids.Add(id);
        return true;
    }

    public void UnregisterAll()
    {
        foreach (int id in ids) Native.UnregisterHotKey(Handle, id);
        ids.Clear();
    }

    // Se puede llamar desde cualquier hilo.
    public void PostShow() => PostMessage(Handle, WM_SHOW_SETTINGS, IntPtr.Zero, IntPtr.Zero);

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY) onHotkey((int)m.WParam);
        else if (m.Msg == WM_SHOW_SETTINGS) onShow();
        else base.WndProc(ref m);
    }

    public void Dispose()
    {
        UnregisterAll();
        DestroyHandle();
    }
}
