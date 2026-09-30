using System.Windows.Threading;
using WacOS.Desktop;
using WacOS.Native;
using WacOS.Settings;

namespace WacOS.Input;

public enum Corner { TopLeft, TopRight, BottomLeft, BottomRight }
public enum ScreenEdge { Left, Right, Top, Bottom }

/// <summary>
/// Pointer-driven behaviours: hot corners, edge dwell while dragging a window, wallpaper clicks,
/// left-edge hover (Stage Manager strip reveal) and Magic-Mouse style horizontal wheel swipes.
/// </summary>
public sealed class MouseService
{
    private readonly WindowTracker _tracker;
    private readonly DispatcherTimer _timer;
    private int _x, _y;
    private Corner? _armedCorner;
    private bool _cornerFired;
    private ScreenEdge? _edge; private long _edgeSince; private bool _edgeFired;
    private bool _leftEdgeHover; private long _leftEdgeSince;
    private bool _lButtonDown; private int _downX, _downY; private IntPtr _downHwnd;
    private long _lastWheelFire;

    public event Action<Corner, MonitorInfo>? HotCorner;
    /// <summary>The user held a dragged window against a screen edge.</summary>
    public event Action<ScreenEdge, IntPtr, MonitorInfo>? DragEdgeDwell;
    /// <summary>Left-click (without drag) on the desktop wallpaper/icons area.</summary>
    public event Action<MonitorInfo>? WallpaperClicked;
    public event Action<MonitorInfo, bool>? LeftEdgeHover;
    public event Action<int>? HorizontalSwipe; // -1 left, +1 right (Magic Mouse emulation)
    public event Action<MouseEventArgs2>? Raw;

    public int X => _x;
    public int Y => _y;
    public bool LeftButtonDown => _lButtonDown;

    public MouseService(LowLevelHooks hooks, WindowTracker tracker)
    {
        _tracker = tracker;
        hooks.Mouse += OnMouse;
        User32.GetCursorPos(out var p); _x = p.X; _y = p.Y;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(60) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    private void OnMouse(MouseEventArgs2 e)
    {
        Raw?.Invoke(e);
        if (e.Handled) return;
        _x = e.X; _y = e.Y;
        switch (e.Message)
        {
            case User32.WM_LBUTTONDOWN:
                _lButtonDown = true; _downX = e.X; _downY = e.Y;
                _downHwnd = User32.WindowFromPoint(new POINT(e.X, e.Y));
                break;
            case User32.WM_LBUTTONUP:
                if (_lButtonDown)
                {
                    _lButtonDown = false;
                    if (Math.Abs(e.X - _downX) < 4 && Math.Abs(e.Y - _downY) < 4 && WindowEnumerator.IsDesktopWindow(_downHwnd)
                        && User32.WindowFromPoint(new POINT(e.X, e.Y)) == _downHwnd)
                    {
                        var mon = Monitors.FromPoint(e.X, e.Y);
                        if (mon != null) Dispatch(() => WallpaperClicked?.Invoke(mon));
                    }
                }
                break;
            case User32.WM_MOUSEHWHEEL when App.Settings.Current.MagicMouseEmulation:
                WheelSwipe(e.WheelDelta > 0 ? 1 : -1);
                break;
            case User32.WM_MOUSEWHEEL when App.Settings.Current.MagicMouseEmulation && User32.IsKeyDown(User32.VK_SHIFT):
                WheelSwipe(e.WheelDelta > 0 ? -1 : 1);
                e.Handled = true;
                break;
        }
    }

    private void WheelSwipe(int dir)
    {
        long now = Environment.TickCount64;
        if (now - _lastWheelFire < 350) return;
        _lastWheelFire = now;
        Dispatch(() => HorizontalSwipe?.Invoke(dir));
    }

    private static void Dispatch(Action a) => App.Current?.Dispatcher.BeginInvoke(a);

    private void Tick()
    {
        var s = App.Settings.Current;
        // The hook does not see programmatic cursor moves; the real position is authoritative.
        if (User32.GetCursorPos(out var cp)) { _x = cp.X; _y = cp.Y; }
        var mon = Monitors.FromPoint(_x, _y);
        if (mon == null) return;
        var b = mon.Bounds;
        long now = Environment.TickCount64;

        // ---- hot corners (trigger immediately when the pointer hits the corner, re-arm after leaving) ----
        Corner? corner = null;
        const int hit = 2, leave = 24;
        if (_x <= b.Left + hit && _y <= b.Top + hit) corner = Corner.TopLeft;
        else if (_x >= b.Right - 1 - hit && _y <= b.Top + hit) corner = Corner.TopRight;
        else if (_x <= b.Left + hit && _y >= b.Bottom - 1 - hit) corner = Corner.BottomLeft;
        else if (_x >= b.Right - 1 - hit && _y >= b.Bottom - 1 - hit) corner = Corner.BottomRight;
        if (corner != null)
        {
            if (!_cornerFired && ModifierHeld(s.HotCornerModifier) && !_lButtonDown)
            {
                _cornerFired = true; _armedCorner = corner;
                HotCorner?.Invoke(corner.Value, mon);
            }
        }
        else if (_cornerFired)
        {
            bool far = !(_x <= b.Left + leave || _x >= b.Right - 1 - leave) || !(_y <= b.Top + leave || _y >= b.Bottom - 1 - leave);
            if (far) { _cornerFired = false; _armedCorner = null; }
        }

        // ---- edge dwell while dragging a window ----
        var dragging = _tracker.DraggingWindow;
        if (dragging != IntPtr.Zero && _lButtonDown)
        {
            ScreenEdge? edge = null;
            if (_y <= b.Top) edge = ScreenEdge.Top;
            else if (_x <= b.Left) edge = ScreenEdge.Left;
            else if (_x >= b.Right - 1) edge = ScreenEdge.Right;
            if (edge != _edge) { _edge = edge; _edgeSince = now; _edgeFired = false; }
            if (edge != null && !_edgeFired)
            {
                int dwell = edge == ScreenEdge.Top ? 350 : 900;
                if (now - _edgeSince >= dwell)
                {
                    _edgeFired = true;
                    DragEdgeDwell?.Invoke(edge.Value, dragging, mon);
                    _edgeSince = now; // allow repeat after another dwell if still held there
                    _edgeFired = false;
                }
            }
        }
        else { _edge = null; _edgeFired = false; }

        // ---- left edge hover (strip reveal) ----
        bool atLeft = _x <= b.Left && !_lButtonDown && _y > b.Top + 40 && _y < b.Bottom - 40;
        if (atLeft && !_leftEdgeHover)
        {
            if (_leftEdgeSince == 0) _leftEdgeSince = now;
            else if (now - _leftEdgeSince > 180) { _leftEdgeHover = true; LeftEdgeHover?.Invoke(mon, true); }
        }
        else if (!atLeft)
        {
            _leftEdgeSince = 0;
            if (_leftEdgeHover && _x > b.Left + 240) { _leftEdgeHover = false; LeftEdgeHover?.Invoke(mon, false); }
        }
    }

    private static bool ModifierHeld(ModifierKey m) => m switch
    {
        ModifierKey.None => true,
        ModifierKey.Ctrl => User32.IsKeyDown(User32.VK_CONTROL),
        ModifierKey.Shift => User32.IsKeyDown(User32.VK_SHIFT),
        ModifierKey.Alt => User32.IsKeyDown(User32.VK_MENU),
        ModifierKey.Win => User32.IsKeyDown(User32.VK_LWIN) || User32.IsKeyDown(User32.VK_RWIN),
        _ => true,
    };
}
