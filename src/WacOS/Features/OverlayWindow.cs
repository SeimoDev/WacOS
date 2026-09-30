using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WacOS.Desktop;
using WacOS.Native;

namespace WacOS.Features;

/// <summary>
/// Base for full-monitor overlay windows. The window is opaque (hardware presented, unlike layered windows which are
/// read back to system memory every frame) and is kept permanently shown but DWM-cloaked, so revealing it is instant
/// and always shows an already rendered frame.
/// </summary>
public class OverlayWindow : Window
{
    public MonitorInfo Monitor { get; private set; } = null!;
    /// <summary>Device pixels per DIP for this window.</summary>
    public double Scale { get; private set; } = 1.0;
    public bool IsRevealed { get; private set; }

    public OverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = false;
        Background = Brushes.Black;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
    }

    public IntPtr Handle => new WindowInteropHelper(this).Handle;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        SetExStyle(User32.WS_EX_TOOLWINDOW | User32.WS_EX_NOACTIVATE, true);
        Dwm.DisableTransitions(Handle, true);
        Dwm.SetCornerPreference(Handle, 1 /* DWMWCP_DONOTROUND */);
    }

    private void SetExStyle(long bits, bool on)
    {
        long ex = User32.GetWindowLong(Handle, User32.GWL_EXSTYLE);
        ex = on ? ex | bits : ex & ~bits;
        User32.SetWindowLong(Handle, User32.GWL_EXSTYLE, ex);
    }

    /// <summary>Positions the window to cover a monitor exactly (device pixels); it stays cloaked.</summary>
    public void PlaceOnMonitor(MonitorInfo mon)
    {
        Monitor = mon;
        var b = mon.Bounds;
        new WindowInteropHelper(this).EnsureHandle();
        SetCloak(true);
        User32.SetWindowPos(Handle, User32.HWND_TOPMOST, b.Left, b.Top, b.Width, b.Height, User32.SWP_NOACTIVATE);
        uint winDpi = User32.GetDpiForWindow(Handle);
        Scale = winDpi > 0 ? winDpi / 96.0 : mon.Scale;
        Width = b.Width / Scale; Height = b.Height / Scale;
    }

    private void SetCloak(bool cloak)
    {
        int v = cloak ? 1 : 0;
        Dwm.DwmSetWindowAttribute(Handle, Dwm.DWMWA_CLOAK, ref v, sizeof(int));
    }

    /// <summary>Shows the window once (cloaked) so WPF creates its render target and keeps frames ready.</summary>
    public void Warmup()
    {
        if (IsVisible) return;
        SetCloak(true);
        Show();
        var b = Monitor.Bounds;
        User32.SetWindowPos(Handle, User32.HWND_TOPMOST, b.Left, b.Top, b.Width, b.Height, User32.SWP_NOACTIVATE);
    }

    /// <summary>Makes the (already rendered) window visible instantly.</summary>
    public void Reveal(bool activate)
    {
        Warmup();
        SetExStyle(User32.WS_EX_NOACTIVATE, false);
        User32.SetWindowPos(Handle, User32.HWND_TOPMOST, 0, 0, 0, 0, User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
        SetCloak(false);
        IsRevealed = true;
        if (activate) { User32.ForceForeground(Handle); Activate(); Focus(); }
    }

    public void Conceal()
    {
        SetCloak(true);
        SetExStyle(User32.WS_EX_NOACTIVATE, true);
        IsRevealed = false;
    }

    public Rect ToLocal(RECT r) => new((r.Left - Monitor.Bounds.Left) / Scale, (r.Top - Monitor.Bounds.Top) / Scale, r.Width / Scale, r.Height / Scale);
    public Point ToLocal(int x, int y) => new((x - Monitor.Bounds.Left) / Scale, (y - Monitor.Bounds.Top) / Scale);

    /// <summary>DIP rect → device pixels relative to the window's client area.</summary>
    public RECT Px(Rect r) => new((int)Math.Round(r.X * Scale), (int)Math.Round(r.Y * Scale), (int)Math.Round((r.X + r.Width) * Scale), (int)Math.Round((r.Y + r.Height) * Scale));

    /// <summary>Screen rect → device pixels relative to the window's client area.</summary>
    public RECT Rel(RECT screen) => new(screen.Left - Monitor.Bounds.Left, screen.Top - Monitor.Bounds.Top, screen.Right - Monitor.Bounds.Left, screen.Bottom - Monitor.Bounds.Top);
}

public static class Wallpaper
{
    private static readonly Dictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public static string CurrentPath()
    {
        var sb = new System.Text.StringBuilder(1024);
        User32.SystemParametersInfo(0x0073 /* SPI_GETDESKWALLPAPER */, (uint)sb.Capacity, sb, 0);
        return sb.ToString();
    }

    public static ImageSource? Load(string? path, int decodeWidth = 800)
    {
        if (string.IsNullOrWhiteSpace(path)) path = CurrentPath();
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path)) return null;
        string key = path + "#" + decodeWidth;
        lock (_cache) if (_cache.TryGetValue(key, out var c)) return c;
        ImageSource? img = null;
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.DecodePixelWidth = decodeWidth;
            bi.UriSource = new Uri(path);
            bi.EndInit();
            bi.Freeze();
            img = bi;
        }
        catch (Exception ex) { Log.Debug("wallpaper load: " + ex.Message); }
        lock (_cache) _cache[key] = img;
        return img;
    }

    public static Brush BackdropBrush(string? path, int decodeWidth = 400)
    {
        var img = Load(path, decodeWidth);
        if (img == null) return new SolidColorBrush(Color.FromRgb(30, 32, 38));
        var b = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
        b.Freeze();
        return b;
    }
}

/// <summary>
/// An exact picture of the desktop (wallpaper + icons) per monitor, captured in the background. Mission Control uses it
/// as its first frame so the transition from the real desktop is seamless.
/// </summary>
public static class DesktopBackdrop
{
    private static readonly Dictionary<IntPtr, BitmapSource> _cache = new();
    private static int _pending;
    /// <summary>Raised on the UI thread after new desktop pictures are available.</summary>
    public static event Action? Updated;

    public static BitmapSource? Get(MonitorInfo mon)
    {
        lock (_cache) return _cache.TryGetValue(mon.Handle, out var b) ? b : null;
    }

    public static void RefreshAsync()
    {
        if (Interlocked.Exchange(ref _pending, 1) == 1) return;
        Worker.Post(() =>
        {
            try
            {
                var progman = User32.FindWindow("Progman", null);
                if (progman == IntPtr.Zero || !User32.GetWindowRect(progman, out var pr)) return;
                var full = WindowCapture.Capture(progman);
                if (full == null) { Log.Debug("desktop backdrop capture failed"); return; }
                foreach (var mon in Monitors.All())
                {
                    var b = mon.Bounds;
                    int x = b.Left - pr.Left, y = b.Top - pr.Top;
                    if (x < 0 || y < 0 || x + b.Width > full.PixelWidth || y + b.Height > full.PixelHeight) continue;
                    var crop = new CroppedBitmap(full, new Int32Rect(x, y, b.Width, b.Height));
                    crop.Freeze();
                    lock (_cache) _cache[mon.Handle] = crop;
                }
                App.Current?.Dispatcher.BeginInvoke(() => Updated?.Invoke());
            }
            finally { Interlocked.Exchange(ref _pending, 0); }
        });
    }
}
