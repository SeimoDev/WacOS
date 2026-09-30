namespace WacOS.Native;

/// <summary>One DWM live thumbnail of a source window drawn on a destination window.</summary>
public sealed class Thumb : IDisposable
{
    private IntPtr _h;
    public IntPtr Source { get; }
    public IntPtr Dest { get; }
    public RECT Current { get; private set; }
    public byte Opacity { get; private set; } = 255;
    /// <summary>Source rectangle (window coordinates) that excludes the invisible resize borders.</summary>
    public RECT? Crop { get; set; }

    private Thumb(IntPtr h, IntPtr dest, IntPtr src) { _h = h; Dest = dest; Source = src; }

    public static Thumb? Create(IntPtr dest, IntPtr src)
    {
        if (dest == IntPtr.Zero || src == IntPtr.Zero) return null;
        if (DwmThumb.DwmRegisterThumbnail(dest, src, out var h) != 0 || h == IntPtr.Zero) return null;
        var t = new Thumb(h, dest, src);
        t.Crop = ComputeCrop(src, h);
        return t;
    }

    public static RECT? ComputeCrop(IntPtr src, IntPtr thumbHandle = default)
    {
        if (User32.IsIconic(src)) return null;
        if (!User32.GetWindowRect(src, out var wr)) return null;
        var frame = Dwm.GetFrameBounds(src);
        if (frame.Width == wr.Width && frame.Height == wr.Height) return null;
        if (thumbHandle != IntPtr.Zero)
        {
            if (DwmThumb.DwmQueryThumbnailSourceSize(thumbHandle, out var sz) != 0) return null;
            if (sz.cx != wr.Width || sz.cy != wr.Height) return null; // source already excludes the borders
        }
        return new RECT(frame.Left - wr.Left, frame.Top - wr.Top, frame.Right - wr.Left, frame.Bottom - wr.Top);
    }

    public void Set(RECT dest, byte opacity = 255, bool visible = true)
    {
        lock (this) SetCore(dest, opacity, visible);
    }

    private void SetCore(RECT dest, byte opacity, bool visible)
    {
        if (_h == IntPtr.Zero) return;
        Current = dest; Opacity = opacity;
        var p = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = DwmThumb.TNP_RECTDESTINATION | DwmThumb.TNP_OPACITY | DwmThumb.TNP_VISIBLE | DwmThumb.TNP_SOURCECLIENTAREAONLY,
            rcDestination = dest, opacity = opacity, fVisible = visible && dest.Width > 0 && dest.Height > 0, fSourceClientAreaOnly = false,
        };
        if (Crop is { } c) { p.dwFlags |= DwmThumb.TNP_RECTSOURCE; p.rcSource = c; }
        DwmThumb.DwmUpdateThumbnailProperties(_h, ref p);
    }

    public void Dispose()
    {
        lock (this)
        {
            if (_h != IntPtr.Zero) { DwmThumb.DwmUnregisterThumbnail(_h); _h = IntPtr.Zero; }
        }
    }
}
