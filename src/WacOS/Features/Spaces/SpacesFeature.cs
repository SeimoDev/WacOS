using System.Windows.Threading;
using WacOS.Desktop;
using WacOS.Input;
using WacOS.Native;
using WacOS.Settings;
using WacOS.VirtualDesktops;

namespace WacOS.Features.Spaces;

/// <summary>
/// Everything about switching and organising Spaces: shortcuts, gestures, drag-to-edge, app assignments,
/// automatic rearrangement, full-screen apps as their own space and "switch to a Space with open windows".
/// </summary>
public sealed class SpacesFeature
{
    private readonly VirtualDesktopService _vd;
    private readonly WindowTracker _tracker;
    private readonly Dictionary<IntPtr, Guid> _fullscreenSpaces = new(); // hwnd -> auto-created space
    private readonly Dictionary<IntPtr, Guid> _fullscreenOrigin = new();  // hwnd -> space it came from
    private readonly HashSet<IntPtr> _assignedOnce = new();
    private readonly DispatcherTimer _fsTimer;
    private long _lastSwitch;

    public SpacesFeature(VirtualDesktopService vd, WindowTracker tracker, HotkeyService hotkeys, TouchpadGestureService gestures, MouseService mouse)
    {
        _vd = vd; _tracker = tracker;
        var s = () => App.Settings.Current;

        hotkeys.Register("MoveLeft", () => s().UseWindowsStyleSpaceShortcuts ? KeyChord.Of(User32.VK_LEFT, ctrl: true, win: true) : s().MoveLeftASpace, () => Step(-1), allowRepeat: true);
        hotkeys.Register("MoveRight", () => s().UseWindowsStyleSpaceShortcuts ? KeyChord.Of(User32.VK_RIGHT, ctrl: true, win: true) : s().MoveRightASpace, () => Step(+1), allowRepeat: true);
        for (int i = 0; i < 10; i++)
        {
            int n = i; // Ctrl+1..9 → Desktop 1..9, Ctrl+0 → Desktop 10
            hotkeys.Register("Desktop" + n, () => s().SwitchToDesktopNumberShortcuts ? KeyChord.Of(0x30 + n, ctrl: true) : null, () => _vd.SwitchToIndex(n == 0 ? 9 : n - 1));
        }

        gestures.Gesture += OnGesture;
        mouse.HorizontalSwipe += dir => Step(App.Settings.Current.NaturalSwipeDirection ? -dir : dir);
        mouse.DragEdgeDwell += OnDragEdge;

        _vd.CurrentChanged += OnCurrentChanged;
        _tracker.WindowShown += OnWindowShown;
        _tracker.ForegroundChanged += OnForeground;
        _tracker.WindowDestroyed += OnWindowDestroyed;
        _tracker.LocationChanged += OnLocationChanged;

        _fsTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(700) };
        _fsTimer.Tick += (_, _) => ScanFullscreen();
        _fsTimer.Start();
    }

    /// <summary>Move one space left (-1) or right (+1). While the user drags a window, the window travels along.</summary>
    public bool Step(int direction)
    {
        long now = Environment.TickCount64;
        if (now - _lastSwitch < 180) return true;
        _lastSwitch = now;
        var spaces = _vd.GetSpaces();
        int idx = _vd.CurrentIndex + direction;
        if (idx < 0 || idx >= spaces.Count) return true; // macOS: no wrap-around, key still consumed
        var target = spaces[idx];
        var dragging = _tracker.DraggingWindow;
        if (dragging != IntPtr.Zero && User32.IsKeyDown(User32.VK_LBUTTON))
        {
            _vd.MoveWindowToSpace(dragging, target);
            _vd.SwitchTo(target);
        }
        else _vd.SwitchTo(target);
        return true;
    }

    private void OnGesture(GestureKind kind, int fingers)
    {
        var s = App.Settings.Current;
        if (kind is GestureKind.SwipeLeft or GestureKind.SwipeRight)
        {
            if (s.SwipeBetweenSpacesFingers == 0 || fingers != s.SwipeBetweenSpacesFingers) return;
            if (App.MissionControl.IsOpen) return;
            int dir = kind == GestureKind.SwipeLeft ? 1 : -1; // natural: swipe left → content moves left → next space on the right
            if (!s.NaturalSwipeDirection) dir = -dir;
            Step(dir);
        }
    }

    private void OnDragEdge(ScreenEdge edge, IntPtr hwnd, MonitorInfo mon)
    {
        var s = App.Settings.Current;
        if (edge == ScreenEdge.Top)
        {
            if (s.DragWindowsToTopToEnterMissionControl && !App.MissionControl.IsOpen) App.MissionControl.OpenForDrag(hwnd, mon);
            return;
        }
        if (App.MissionControl.IsOpen) return;
        // Only when this monitor edge is the edge of the whole desktop (single display or outermost monitor).
        var all = Monitors.All();
        bool outer = edge == ScreenEdge.Left ? all.All(m => m.Bounds.Left >= mon.Bounds.Left) : all.All(m => m.Bounds.Right <= mon.Bounds.Right);
        if (!outer) return;
        var spaces = _vd.GetSpaces();
        int idx = _vd.CurrentIndex + (edge == ScreenEdge.Left ? -1 : 1);
        if (idx < 0 || idx >= spaces.Count) return;
        _vd.MoveWindowToSpace(hwnd, spaces[idx]);
        _vd.SwitchTo(spaces[idx]);
    }

    // ---- automatic rearrangement (most recent space first) ----
    private void OnCurrentChanged(Space? old, Space now)
    {
        var s = App.Settings.Current;
        if (s.AutoRearrangeSpaces && now.Index != 0 && !_suppressRearrange)
        {
            _suppressRearrange = true;
            // Defer a little so the switch animation finishes before the bar reorders.
            App.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                try { _vd.Move(now, 0); } finally { _suppressRearrange = false; }
            });
        }
    }
    private bool _suppressRearrange;

    // ---- app assignments ("Assign To" in the Dock) ----
    private void OnWindowShown(IntPtr hwnd)
    {
        App.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            var wi = WindowEnumerator.Describe(hwnd, includeOtherDesktops: true);
            if (wi == null || _assignedOnce.Contains(hwnd)) return;
            _assignedOnce.Add(hwnd);
            var rule = FindAssignment(wi.ExePath);
            if (rule == null) return;
            switch (rule.Mode)
            {
                case AssignMode.AllDesktops:
                    _vd.PinWindow(hwnd, true);
                    break;
                case AssignMode.ThisDesktop:
                    var target = _vd.GetSpaces().FirstOrDefault(sp => sp.Id == rule.DesktopId);
                    if (target != null && _vd.GetWindowSpaceId(hwnd) != target.Id) _vd.MoveWindowToSpace(hwnd, target);
                    break;
            }
        });
    }

    private void OnWindowDestroyed(IntPtr hwnd)
    {
        _assignedOnce.Remove(hwnd);
        if (_fullscreenSpaces.Remove(hwnd, out var spaceId)) RemoveAutoSpace(spaceId);
        _fullscreenOrigin.Remove(hwnd);
    }

    public AppAssignment? FindAssignment(string exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return null;
        return App.Settings.Current.AppAssignments.FirstOrDefault(a => string.Equals(a.ExePath, exePath, StringComparison.OrdinalIgnoreCase) && a.Mode != AssignMode.None);
    }

    public void Assign(WindowInfo w, AssignMode mode)
    {
        App.Settings.Update(st =>
        {
            st.AppAssignments.RemoveAll(a => string.Equals(a.ExePath, w.ExePath, StringComparison.OrdinalIgnoreCase));
            if (mode != AssignMode.None)
            {
                var cur = _vd.Current;
                st.AppAssignments.Add(new AppAssignment { ExePath = w.ExePath, AppName = w.AppName, Mode = mode, DesktopId = mode == AssignMode.ThisDesktop ? cur?.Id ?? Guid.Empty : Guid.Empty, DesktopName = cur?.DisplayName ?? "" });
            }
        });
        // Apply to existing windows of the app immediately.
        foreach (var win in WindowEnumerator.GetWindows(includeOtherDesktops: true).Where(x => string.Equals(x.ExePath, w.ExePath, StringComparison.OrdinalIgnoreCase)))
        {
            if (mode == AssignMode.AllDesktops) _vd.PinWindow(win.Hwnd, true);
            else
            {
                if (_vd.IsWindowPinned(win.Hwnd)) _vd.PinWindow(win.Hwnd, false);
                if (mode == AssignMode.ThisDesktop && _vd.Current is { } cur) _vd.MoveWindowToSpace(win.Hwnd, cur);
            }
        }
    }

    // ---- "When switching to an application, switch to a Space with open windows for the application" ----
    private void OnForeground(IntPtr hwnd)
    {
        if (!App.Settings.Current.SwitchToSpaceWithOpenWindows) return;
        if (hwnd == IntPtr.Zero || !User32.IsWindow(hwnd)) return;
        // Windows normally switches desktops itself; enforce it for windows activated programmatically on another desktop.
        if (Dwm.GetCloaked(hwnd) == Dwm.DWM_CLOAKED_SHELL && !_vd.IsWindowPinned(hwnd))
        {
            var id = _vd.GetWindowSpaceId(hwnd);
            if (id != Guid.Empty && id != _vd.Current?.Id) _vd.SwitchTo(id);
        }
    }

    // ---- full-screen apps as their own space ----
    private void OnLocationChanged(IntPtr hwnd) { /* handled by the periodic scan to avoid event storms */ }

    private void ScanFullscreen()
    {
        if (!App.Settings.Current.FullscreenAppsGetOwnSpace) return;
        if (App.MissionControl.IsOpen) return;
        try
        {
            var fg = User32.GetForegroundWindow();
            // 1) windows that left full-screen (or got minimized) → return them and drop their space
            foreach (var kv in _fullscreenSpaces.ToList())
            {
                var wi = WindowEnumerator.Describe(kv.Key, includeOtherDesktops: true);
                if (wi == null || !wi.IsFullscreen)
                {
                    _fullscreenSpaces.Remove(kv.Key);
                    if (wi != null && _fullscreenOrigin.TryGetValue(kv.Key, out var origin))
                    {
                        var originSpace = _vd.GetSpaces().FirstOrDefault(s => s.Id == origin);
                        if (originSpace != null) { _vd.MoveWindowToSpace(kv.Key, originSpace); if (fg == kv.Key) _vd.SwitchTo(originSpace); }
                    }
                    _fullscreenOrigin.Remove(kv.Key);
                    RemoveAutoSpace(kv.Value);
                }
            }
            // 2) the foreground window just went full-screen → give it a space named after the app
            if (fg != IntPtr.Zero && !_fullscreenSpaces.ContainsKey(fg))
            {
                var wi = WindowEnumerator.Describe(fg);
                if (wi != null && wi.IsFullscreen && !_vd.IsWindowPinned(fg) && _vd.GetSpaces().Count < VirtualDesktopService.MaxSpaces)
                {
                    var cur = _vd.Current; if (cur == null) return;
                    // Don't do it for the shell / lock screen / our own.
                    if (wi.ClassName is "Progman" or "WorkerW" or "Windows.UI.Core.CoreWindow") return;
                    var space = _vd.Create(); if (space == null) return;
                    _vd.Rename(space, wi.AppName);
                    _fullscreenOrigin[fg] = cur.Id; _fullscreenSpaces[fg] = space.Id;
                    _vd.MoveWindowToSpace(fg, space);
                    _vd.SwitchTo(space);
                    App.Activate(fg);
                }
            }
        }
        catch (Exception ex) { Log.Error("fullscreen scan", ex); }
    }

    private void RemoveAutoSpace(Guid id)
    {
        var sp = _vd.GetSpaces().FirstOrDefault(s => s.Id == id);
        if (sp != null) _vd.Remove(sp);
    }

    public bool IsAutoFullscreenSpace(Guid id) => _fullscreenSpaces.ContainsValue(id);
}
