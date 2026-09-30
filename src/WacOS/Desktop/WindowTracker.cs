using WacOS.Native;

namespace WacOS.Desktop;

/// <summary>Central WinEvent subscriber. Raises high-level events for top-level application windows on the UI thread.</summary>
public sealed class WindowTracker : IDisposable
{
    private readonly WinEventHook _sys, _obj, _cloak;

    public event Action<IntPtr>? ForegroundChanged;
    public event Action<IntPtr>? WindowShown;
    public event Action<IntPtr>? WindowHidden;
    public event Action<IntPtr>? WindowDestroyed;
    public event Action<IntPtr>? MinimizeStart;
    public event Action<IntPtr>? MinimizeEnd;
    public event Action<IntPtr>? MoveSizeStart;
    public event Action<IntPtr>? MoveSizeEnd;
    public event Action<IntPtr>? LocationChanged;
    public event Action<IntPtr>? Cloaked;
    public event Action<IntPtr>? Uncloaked;
    public event Action<IntPtr>? NameChanged;

    /// <summary>Window currently being moved/resized by the user (0 when none).</summary>
    public IntPtr DraggingWindow { get; private set; }
    public long DragStartedAt { get; private set; }

    public WindowTracker()
    {
        _sys = new WinEventHook(User32.EVENT_SYSTEM_FOREGROUND, User32.EVENT_SYSTEM_MINIMIZEEND);
        _sys.Event += OnSys;
        _obj = new WinEventHook(User32.EVENT_OBJECT_CREATE, User32.EVENT_OBJECT_NAMECHANGE);
        _obj.Event += OnObj;
        _cloak = new WinEventHook(User32.EVENT_OBJECT_CLOAKED, User32.EVENT_OBJECT_UNCLOAKED);
        _cloak.Event += OnObj;
    }

    private static bool IsTopLevel(IntPtr hwnd, int idObject, int idChild)
        => hwnd != IntPtr.Zero && idObject == User32.OBJID_WINDOW && idChild == User32.CHILDID_SELF && User32.GetAncestor(hwnd, User32.GA_ROOT) == hwnd;

    private void OnSys(uint ev, IntPtr hwnd, int idObject, int idChild)
    {
        switch (ev)
        {
            case User32.EVENT_SYSTEM_FOREGROUND: ForegroundChanged?.Invoke(hwnd); break;
            case User32.EVENT_SYSTEM_MINIMIZESTART: MinimizeStart?.Invoke(hwnd); break;
            case User32.EVENT_SYSTEM_MINIMIZEEND: MinimizeEnd?.Invoke(hwnd); break;
            case User32.EVENT_SYSTEM_MOVESIZESTART: DraggingWindow = hwnd; DragStartedAt = Environment.TickCount64; MoveSizeStart?.Invoke(hwnd); break;
            case User32.EVENT_SYSTEM_MOVESIZEEND: DraggingWindow = IntPtr.Zero; MoveSizeEnd?.Invoke(hwnd); break;
        }
    }

    private void OnObj(uint ev, IntPtr hwnd, int idObject, int idChild)
    {
        if (!IsTopLevel(hwnd, idObject, idChild)) return;
        switch (ev)
        {
            case User32.EVENT_OBJECT_SHOW: WindowShown?.Invoke(hwnd); break;
            case User32.EVENT_OBJECT_HIDE: WindowHidden?.Invoke(hwnd); break;
            case User32.EVENT_OBJECT_DESTROY: WindowDestroyed?.Invoke(hwnd); break;
            case User32.EVENT_OBJECT_LOCATIONCHANGE: LocationChanged?.Invoke(hwnd); break;
            case User32.EVENT_OBJECT_CLOAKED: Cloaked?.Invoke(hwnd); break;
            case User32.EVENT_OBJECT_UNCLOAKED: Uncloaked?.Invoke(hwnd); break;
            case User32.EVENT_OBJECT_NAMECHANGE: NameChanged?.Invoke(hwnd); break;
        }
    }

    public void Dispose() { _sys.Dispose(); _obj.Dispose(); _cloak.Dispose(); }
}
