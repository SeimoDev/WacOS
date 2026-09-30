using System.Windows.Media;
using System.Windows.Media.Imaging;
using WacOS.Desktop;

namespace WacOS.Features.StageManager;

/// <summary>A group of windows that appear on the stage together and share one strip thumbnail.</summary>
public sealed class WindowSet
{
    public List<IntPtr> Windows { get; } = new();          // front-most first
    public Dictionary<IntPtr, WindowInfo> Info { get; } = new();
    public Dictionary<IntPtr, BitmapSource?> Snapshots { get; } = new();
    /// <summary>Visible frame of each window when it was last on the stage (screen pixels).</summary>
    public Dictionary<IntPtr, WacOS.Native.RECT> Frames { get; } = new();
    /// <summary>Thumbnail source crop (excludes invisible resize borders), captured while the window was visible.</summary>
    public Dictionary<IntPtr, WacOS.Native.RECT?> Crops { get; } = new();
    public long LastActive { get; set; } = Environment.TickCount64;

    public IntPtr Front => Windows.Count > 0 ? Windows[0] : IntPtr.Zero;
    public string AppKey => Windows.Count > 0 && Info.TryGetValue(Windows[0], out var i) ? i.AppKey : "";
    public string AppName => Windows.Count > 0 && Info.TryGetValue(Windows[0], out var i) ? i.AppName : "";
    public IEnumerable<ImageSource> Icons => Windows.Select(w => Info.GetValueOrDefault(w)?.Icon).Where(i => i != null).Distinct().Select(i => i!);
    public bool Contains(IntPtr hwnd) => Windows.Contains(hwnd);
    public bool IsEmpty => Windows.Count == 0;

    public void Add(WindowInfo wi, bool front = true)
    {
        if (Windows.Contains(wi.Hwnd)) { Windows.Remove(wi.Hwnd); }
        if (front) Windows.Insert(0, wi.Hwnd); else Windows.Add(wi.Hwnd);
        Info[wi.Hwnd] = wi;
    }

    public void Remove(IntPtr hwnd) { Windows.Remove(hwnd); Info.Remove(hwnd); Snapshots.Remove(hwnd); Frames.Remove(hwnd); Crops.Remove(hwnd); }

    public void MoveToFront(IntPtr hwnd) { if (Windows.Remove(hwnd)) Windows.Insert(0, hwnd); }
}

/// <summary>Stage Manager state for one (space, display) pair.</summary>
public sealed class StageState
{
    public List<WindowSet> Strip { get; } = new();   // most recent first (top of the strip)
    public WindowSet? Stage { get; set; }
    public bool DesktopRevealed { get; set; }

    public WindowSet? FindSet(IntPtr hwnd)
    {
        if (Stage != null && Stage.Contains(hwnd)) return Stage;
        return Strip.FirstOrDefault(s => s.Contains(hwnd));
    }

    public WindowSet? FindStripSetForApp(string appKey) => Strip.FirstOrDefault(s => s.AppKey == appKey);

    public IEnumerable<WindowSet> AllSets => Stage == null ? Strip : Strip.Prepend(Stage);
}
