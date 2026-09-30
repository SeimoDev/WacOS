using System.Runtime.InteropServices;

namespace WacOS.Native;

[StructLayout(LayoutKind.Sequential)]
public struct DWM_THUMBNAIL_PROPERTIES
{
    public uint dwFlags;
    public RECT rcDestination;
    public RECT rcSource;
    public byte opacity;
    [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
    [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
}

public static class DwmThumb
{
    public const uint TNP_RECTDESTINATION = 1, TNP_RECTSOURCE = 2, TNP_OPACITY = 4, TNP_VISIBLE = 8, TNP_SOURCECLIENTAREAONLY = 0x10;
    [DllImport("dwmapi.dll")] public static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
    [DllImport("dwmapi.dll")] public static extern int DwmUnregisterThumbnail(IntPtr thumb);
    [DllImport("dwmapi.dll")] public static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref DWM_THUMBNAIL_PROPERTIES props);
    [DllImport("dwmapi.dll")] public static extern int DwmQueryThumbnailSourceSize(IntPtr thumb, out SIZE size);
    [DllImport("dwmapi.dll")] public static extern int DwmFlush();
}

/// <summary>
/// A bare top-level window with no redirection surface: it is invisible itself, but DWM thumbnails registered on it are
/// composited by DWM on the GPU. Used as the canvas for window fly animations.
/// </summary>
public sealed class ThumbHost : IDisposable
{
    private static readonly WndProcDelegate _proc = (h, m, w, l) => User32.DefWindowProc(h, m, w, l);
    private static bool _registered;
    public IntPtr Handle { get; private set; }
    public RECT Bounds { get; private set; }

    public ThumbHost(RECT bounds, bool noRedirection = true)
    {
        const string cls = "WacOS.ThumbHost";
        if (!_registered)
        {
            var wc = new User32.WNDCLASS { lpfnWndProc = _proc, lpszClassName = cls, hInstance = Kernel32.GetModuleHandle(null) };
            User32.RegisterClassW(ref wc);
            _registered = true;
        }
        Bounds = bounds;
        long ex = User32.WS_EX_TOOLWINDOW | User32.WS_EX_TOPMOST | User32.WS_EX_NOACTIVATE;
        if (noRedirection) ex |= User32.WS_EX_NOREDIRECTIONBITMAP;
        Handle = User32.CreateWindowEx((int)ex, cls, "", unchecked((int)User32.WS_POPUP), bounds.Left, bounds.Top, bounds.Width, bounds.Height, IntPtr.Zero, IntPtr.Zero, Kernel32.GetModuleHandle(null), IntPtr.Zero);
        Dwm.DisableTransitions(Handle, true);
    }

    public void Show() => User32.SetWindowPos(Handle, User32.HWND_TOPMOST, 0, 0, 0, 0, User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE | User32.SWP_SHOWWINDOW);
    public void Hide() => User32.ShowWindow(Handle, User32.SW_HIDE);
    public void Dispose() { if (Handle != IntPtr.Zero) { User32.DestroyWindow(Handle); Handle = IntPtr.Zero; } }
}
