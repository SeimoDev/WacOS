using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WacOS.Native;

namespace WacOS.Desktop;

/// <summary>Captures window contents via PrintWindow(PW_RENDERFULLCONTENT), which works for DWM-composed and cloaked windows.</summary>
public static class WindowCapture
{
    private static readonly Dictionary<IntPtr, (BitmapSource bmp, DateTime at)> _cache = new();

    public static BitmapSource? Capture(IntPtr hwnd, bool allowCached = false, double maxAgeMs = 400)
    {
        lock (_cache)
        {
            if (allowCached && _cache.TryGetValue(hwnd, out var c) && (DateTime.UtcNow - c.at).TotalMilliseconds < maxAgeMs) return c.bmp;
        }
        var bmp = CaptureCore(hwnd);
        if (bmp != null) lock (_cache) _cache[hwnd] = (bmp, DateTime.UtcNow);
        return bmp;
    }

    public static BitmapSource? Cached(IntPtr hwnd)
    {
        lock (_cache) return _cache.TryGetValue(hwnd, out var c) ? c.bmp : null;
    }

    public static void Forget(IntPtr hwnd) { lock (_cache) _cache.Remove(hwnd); }

    private static BitmapSource? CaptureCore(IntPtr hwnd)
    {
        if (!User32.IsWindow(hwnd)) return null;
        if (!User32.GetWindowRect(hwnd, out var wr)) return null;
        int w = wr.Width, h = wr.Height;
        if (w <= 0 || h <= 0 || w > 16384 || h > 16384) return null;
        if (User32.IsHungAppWindow(hwnd)) return null;

        IntPtr screenDc = User32.GetDC(IntPtr.Zero);
        IntPtr memDc = Gdi32.CreateCompatibleDC(screenDc);
        IntPtr bmp = Gdi32.CreateCompatibleBitmap(screenDc, w, h);
        IntPtr old = Gdi32.SelectObject(memDc, bmp);
        try
        {
            bool ok = User32.PrintWindow(hwnd, memDc, User32.PW_RENDERFULLCONTENT);
            if (!ok) ok = User32.PrintWindow(hwnd, memDc, 0);
            if (!ok) return null;
            var src = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

            // Crop the invisible resize borders so the thumbnail matches what the user sees.
            var frame = Dwm.GetFrameBounds(hwnd);
            int cx = Math.Max(0, frame.Left - wr.Left), cy = Math.Max(0, frame.Top - wr.Top);
            int cw = Math.Min(frame.Width, w - cx), ch = Math.Min(frame.Height, h - cy);
            BitmapSource result = src;
            if (cw > 0 && ch > 0 && (cx > 0 || cy > 0 || cw < w || ch < h))
                result = new CroppedBitmap(src, new Int32Rect(cx, cy, cw, ch));
            if (IsBlank(result)) return null;
            result.Freeze();
            return result;
        }
        catch (Exception ex) { Log.Debug("Capture failed: " + ex.Message); return null; }
        finally
        {
            Gdi32.SelectObject(memDc, old);
            Gdi32.DeleteObject(bmp);
            Gdi32.DeleteDC(memDc);
            User32.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>Cheap sampling check for a fully black capture (typical for windows that refuse PrintWindow).</summary>
    private static bool IsBlank(BitmapSource bmp)
    {
        try
        {
            int w = bmp.PixelWidth, h = bmp.PixelHeight;
            if (w < 4 || h < 4) return true;
            var conv = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
            int stride = w * 4;
            var buf = new byte[stride];
            int rows = 0, nonBlack = 0;
            for (int y = h / 8; y < h; y += Math.Max(1, h / 8))
            {
                conv.CopyPixels(new Int32Rect(0, y, w, 1), buf, stride, 0);
                for (int x = 0; x < w; x += Math.Max(1, w / 16))
                {
                    int i = x * 4;
                    if (buf[i] > 8 || buf[i + 1] > 8 || buf[i + 2] > 8) nonBlack++;
                }
                rows++;
            }
            return nonBlack == 0 && rows > 0;
        }
        catch { return false; }
    }

    /// <summary>Captures the whole monitor (used for backdrops and slide animations).</summary>
    public static BitmapSource? CaptureScreen(RECT r)
    {
        IntPtr screenDc = User32.GetDC(IntPtr.Zero);
        IntPtr memDc = Gdi32.CreateCompatibleDC(screenDc);
        IntPtr bmp = Gdi32.CreateCompatibleBitmap(screenDc, r.Width, r.Height);
        IntPtr old = Gdi32.SelectObject(memDc, bmp);
        try
        {
            Gdi32.BitBlt(memDc, 0, 0, r.Width, r.Height, screenDc, r.Left, r.Top, Gdi32.SRCCOPY | Gdi32.CAPTUREBLT);
            var src = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch { return null; }
        finally
        {
            Gdi32.SelectObject(memDc, old);
            Gdi32.DeleteObject(bmp);
            Gdi32.DeleteDC(memDc);
            User32.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
