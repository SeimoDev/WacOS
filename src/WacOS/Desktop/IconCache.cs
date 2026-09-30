using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WacOS.Native;

namespace WacOS.Desktop;

public static class IconCache
{
    private static readonly Dictionary<string, ImageSource?> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<IntPtr, ImageSource?> _byHwnd = new();

    public static ImageSource? GetIcon(WindowInfo w)
    {
        // UWP / packaged apps: the frame host exe has a generic icon; ask the window itself first.
        if (w.ClassName == "ApplicationFrameWindow" || w.ExePath.Length == 0 || w.ExePath.StartsWith("process:", StringComparison.Ordinal))
        {
            lock (_byHwnd) if (_byHwnd.TryGetValue(w.Hwnd, out var c)) return c;
            var ico = FromWindow(w.Hwnd) ?? (w.ExePath.Length > 0 && !w.ExePath.StartsWith("process:", StringComparison.Ordinal) ? FromFile(w.ExePath) : null);
            lock (_byHwnd) _byHwnd[w.Hwnd] = ico;
            return ico;
        }
        lock (_byPath) if (_byPath.TryGetValue(w.ExePath, out var cached)) return cached;
        var img = FromFile(w.ExePath) ?? FromWindow(w.Hwnd);
        lock (_byPath) _byPath[w.ExePath] = img;
        return img;
    }

    public static ImageSource? FromFile(string path)
    {
        try
        {
            var info = new Shell32.SHFILEINFO();
            var r = Shell32.SHGetFileInfo(path, 0, ref info, (uint)System.Runtime.InteropServices.Marshal.SizeOf<Shell32.SHFILEINFO>(), Shell32.SHGFI_ICON | Shell32.SHGFI_LARGEICON);
            if (r == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
            try { return ToImage(info.hIcon); }
            finally { User32.DestroyIcon(info.hIcon); }
        }
        catch { return null; }
    }

    public static ImageSource? FromWindow(IntPtr hwnd)
    {
        try
        {
            User32.SendMessageTimeout(hwnd, User32.WM_GETICON, new IntPtr(User32.ICON_BIG), IntPtr.Zero, 0x0002, 200, out var h);
            if (h == IntPtr.Zero) User32.SendMessageTimeout(hwnd, User32.WM_GETICON, new IntPtr(User32.ICON_SMALL2), IntPtr.Zero, 0x0002, 200, out h);
            if (h == IntPtr.Zero) h = User32.GetClassLongPtr(hwnd, User32.GCL_HICON);
            if (h == IntPtr.Zero) h = User32.GetClassLongPtr(hwnd, User32.GCL_HICONSM);
            return h == IntPtr.Zero ? null : ToImage(h);
        }
        catch { return null; }
    }

    private static ImageSource ToImage(IntPtr hIcon)
    {
        var src = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        src.Freeze();
        return src;
    }
}
