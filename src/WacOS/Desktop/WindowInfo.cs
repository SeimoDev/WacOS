using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Media;
using WacOS.Native;

namespace WacOS.Desktop;

public sealed class WindowInfo
{
    public IntPtr Hwnd { get; init; }
    public string Title { get; set; } = "";
    public string ClassName { get; init; } = "";
    public uint ProcessId { get; init; }
    public string ExePath { get; init; } = "";
    public string AppName { get; init; } = "";
    /// <summary>Key used to group windows of the same application.</summary>
    public string AppKey { get; init; } = "";
    public RECT Bounds { get; set; }
    public bool IsMinimized { get; set; }
    public bool IsMaximized { get; set; }
    public bool IsCloaked { get; set; }
    public IntPtr Monitor { get; set; }

    public bool IsFullscreen
    {
        get
        {
            if (IsMinimized) return false;
            var mon = Monitors.FromWindow(Hwnd);
            if (mon == null) return false;
            var b = Bounds; var m = mon.Bounds;
            long style = User32.GetWindowLong(Hwnd, User32.GWL_STYLE);
            bool noCaption = (style & User32.WS_CAPTION) != User32.WS_CAPTION;
            return noCaption && b.Left <= m.Left && b.Top <= m.Top && b.Right >= m.Right && b.Bottom >= m.Bottom;
        }
    }

    public ImageSource? Icon => IconCache.GetIcon(this);

    public override string ToString() => $"{Title} [{AppName}] {Bounds}";
}

public static class ProcessInfo
{
    private static readonly Dictionary<uint, (string path, string name, DateTime at)> _cache = new();

    public static (string path, string name) Get(uint pid)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(pid, out var c) && (DateTime.UtcNow - c.at).TotalMinutes < 5) return (c.path, c.name);
        }
        string path = "", name = "";
        var h = Kernel32.OpenProcess(Kernel32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h != IntPtr.Zero)
        {
            try
            {
                var sb = new StringBuilder(1024); int size = sb.Capacity;
                if (Kernel32.QueryFullProcessImageName(h, 0, sb, ref size)) path = sb.ToString();
            }
            finally { Kernel32.CloseHandle(h); }
        }
        if (path.Length > 0)
        {
            try
            {
                var fvi = FileVersionInfo.GetVersionInfo(path);
                name = fvi.FileDescription?.Trim() ?? "";
            }
            catch { }
            if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileNameWithoutExtension(path);
        }
        else
        {
            // Elevated / protected processes refuse the image path; the process name is still readable and
            // serves as a stable identity so different elevated apps are never grouped together.
            try { name = Process.GetProcessById((int)pid).ProcessName; path = "process:" + name.ToLowerInvariant(); } catch { }
        }
        lock (_cache) _cache[pid] = (path, name, DateTime.UtcNow);
        return (path, name);
    }
}
