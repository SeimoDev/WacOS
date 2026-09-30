using WacOS.Native;

namespace WacOS.Desktop;

public sealed class MonitorInfo
{
    public IntPtr Handle { get; init; }
    public RECT Bounds { get; init; }
    public RECT WorkArea { get; init; }
    public bool IsPrimary { get; init; }
    public string Device { get; init; } = "";
    public double Dpi { get; init; } = 96;
    public double Scale => Dpi / 96.0;
    public override string ToString() => $"{Device} {Bounds} {(IsPrimary ? "primary" : "")} dpi={Dpi}";
}

public static class Monitors
{
    public static List<MonitorInfo> All()
    {
        var list = new List<MonitorInfo>();
        User32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr hdc, ref RECT r, IntPtr d) =>
        {
            var mi = Get(h);
            if (mi != null) list.Add(mi);
            return true;
        }, IntPtr.Zero);
        return list.OrderByDescending(m => m.IsPrimary).ThenBy(m => m.Bounds.Left).ToList();
    }

    public static MonitorInfo? Get(IntPtr h)
    {
        if (h == IntPtr.Zero) return null;
        var mi = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
        if (!User32.GetMonitorInfo(h, ref mi)) return null;
        uint dpi = 96;
        try { User32.GetDpiForMonitor(h, 0, out dpi, out _); } catch { }
        return new MonitorInfo { Handle = h, Bounds = mi.rcMonitor, WorkArea = mi.rcWork, IsPrimary = (mi.dwFlags & User32.MONITORINFOF_PRIMARY) != 0, Device = mi.szDevice, Dpi = dpi };
    }

    public static MonitorInfo? FromWindow(IntPtr hwnd) => Get(User32.MonitorFromWindow(hwnd, User32.MONITOR_DEFAULTTONEAREST));
    public static MonitorInfo? FromPoint(int x, int y) => Get(User32.MonitorFromPoint(new POINT(x, y), User32.MONITOR_DEFAULTTONEAREST));
    public static MonitorInfo Primary() => All().First(m => m.IsPrimary);
    public static MonitorInfo? FromCursor() { User32.GetCursorPos(out var p); return FromPoint(p.X, p.Y); }
}
