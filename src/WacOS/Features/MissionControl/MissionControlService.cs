using System.Windows.Media;
using System.Windows.Threading;
using WacOS.Desktop;
using WacOS.Input;
using WacOS.Native;
using WacOS.Settings;
using WacOS.VirtualDesktops;

namespace WacOS.Features.MissionControl;

public enum MissionControlMode { Full, AppExpose }

/// <summary>Snapshot of the desktop taken when Mission Control opens; shared by all per-monitor overlays.</summary>
public sealed class MissionControlModel
{
    public IReadOnlyList<Space> Spaces { get; init; } = Array.Empty<Space>();
    public Guid CurrentSpace { get; init; }
    public List<WindowInfo> Windows { get; init; } = new();                 // every app window, top-most first
    public Dictionary<IntPtr, Guid> WindowSpace { get; init; } = new();
    public HashSet<IntPtr> Pinned { get; init; } = new();
    /// <summary>Windows hidden by WacOS (stowed in the Stage Manager strip). They belong to the space and are shown.</summary>
    public HashSet<IntPtr> Parked { get; init; } = new();
    public string? FrontAppKey { get; init; }
    public string? FrontAppName { get; init; }
}

public sealed class MissionControlService
{
    private readonly VirtualDesktopService _vd;
    private readonly WindowTracker _tracker;
    private readonly Dictionary<IntPtr, MissionControlWindow> _windows = new();
    private readonly Dictionary<(Guid space, IntPtr mon), ImageSource> _thumbCache = new();
    private IntPtr _dragHwnd;
    private bool _open, _closing, _switchingInternally;

    public bool IsOpen => _open;
    public MissionControlMode Mode { get; private set; }
    public bool IsDragMode => _dragHwnd != IntPtr.Zero;

    public MissionControlService(VirtualDesktopService vd, WindowTracker tracker, HotkeyService hotkeys, TouchpadGestureService gestures, MouseService mouse)
    {
        _vd = vd; _tracker = tracker;
        var s = () => App.Settings.Current;
        hotkeys.Register("MissionControl", () => s().MissionControl, Toggle);
        hotkeys.Register("MissionControlF3", () => s().MissionControlAlt, Toggle);
        hotkeys.Register("AppExpose", () => s().ApplicationWindows, ToggleAppExpose);
        hotkeys.Register("MC-Escape", () => IsOpen ? KeyChord.Of(User32.VK_ESCAPE) : null, Close);
        gestures.Gesture += OnGesture;
        mouse.Raw += OnRawMouse;
        _tracker.MoveSizeEnd += _ => { if (IsDragMode) App.Current.Dispatcher.BeginInvoke(() => { if (IsDragMode) FinishDrag(); }); };
        _vd.CurrentChanged += (_, _) =>
        {
            if (IsOpen && !_switchingInternally && !_closing) Refresh();
            // The wallpaper can differ per space: refresh the cached desktop picture once the switch has settled.
            Anim.After(700, DesktopBackdrop.RefreshAsync);
        };
        // Pre-create everything so the first Ctrl+↑ is as fast as every other one.
        App.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, Warmup);
    }

    private void Warmup()
    {
        DesktopBackdrop.RefreshAsync();
        Flyer.Warmup();
        EnsureWindows();
    }

    /// <summary>One cloaked, pre-rendered overlay per monitor, recreated when the display layout changes.</summary>
    private void EnsureWindows()
    {
        var mons = Monitors.All();
        foreach (var (h, w) in _windows.ToList())
        {
            var m = mons.FirstOrDefault(x => x.Handle == h);
            if (m == null || m.Bounds.Left != w.Monitor.Bounds.Left || m.Bounds.Top != w.Monitor.Bounds.Top || m.Bounds.Width != w.Monitor.Bounds.Width || m.Bounds.Height != w.Monitor.Bounds.Height || Math.Abs(m.Dpi - w.Monitor.Dpi) > 0.5)
            {
                try { w.Close(); } catch { }
                _windows.Remove(h);
            }
        }
        foreach (var m in mons)
        {
            if (_windows.ContainsKey(m.Handle)) continue;
            var w = new MissionControlWindow(this, _vd, m);
            w.Warmup();
            _windows[m.Handle] = w;
        }
    }

    private void OnGesture(GestureKind kind, int fingers)
    {
        var s = App.Settings.Current;
        if (s.MissionControlGestureFingers == 0 || fingers != s.MissionControlGestureFingers) return;
        switch (kind)
        {
            case GestureKind.SwipeUp:
                if (IsOpen && Mode == MissionControlMode.AppExpose) Close();
                else if (!IsOpen) Open(MissionControlMode.Full);
                break;
            case GestureKind.SwipeDown:
                if (IsOpen) Close();
                else if (s.AppExposeGesture) Open(MissionControlMode.AppExpose);
                break;
            case GestureKind.Spread:
                if (s.ShowDesktopGesture && !IsOpen) App.ShowDesktop.Toggle();
                break;
            case GestureKind.Pinch:
                if (s.LaunchpadGesture && !IsOpen) User32.SendKeyTap(User32.VK_LWIN);
                break;
        }
    }

    public void Toggle() { if (IsOpen && Mode == MissionControlMode.Full) Close(); else Open(MissionControlMode.Full); }
    public void ToggleAppExpose() { if (IsOpen && Mode == MissionControlMode.AppExpose) Close(); else Open(MissionControlMode.AppExpose); }

    public void Open(MissionControlMode mode) => OpenCore(mode, IntPtr.Zero);

    /// <summary>Opened by dragging a window to the top of the screen; the OS move loop is still running.</summary>
    public void OpenForDrag(IntPtr hwnd, MonitorInfo mon)
    {
        if (IsOpen) return;
        OpenCore(MissionControlMode.Full, hwnd);
    }

    private void OpenCore(MissionControlMode mode, IntPtr dragHwnd)
    {
        if (_open) CloseNow();
        var model = BuildModel(mode);
        if (mode == MissionControlMode.AppExpose && model.FrontAppKey == null) return;
        Mode = mode; _dragHwnd = dragHwnd;
        EnsureWindows();
        foreach (var w in _windows.Values) w.Prepare(model, mode, dragHwnd, EnterKind.FromDesktop, id => CachedThumb(id, w.Monitor.Handle));
        // Let WPF lay out and render the prepared (still cloaked) content before it becomes visible.
        App.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        var cursorMon = Monitors.FromCursor()?.Handle ?? IntPtr.Zero;
        _open = true;
        App.StageManager.OnOverlayOpened(true);
        foreach (var w in _windows.Values) w.Reveal(activate: dragHwnd == IntPtr.Zero && w.Monitor.Handle == cursorMon);
        foreach (var w in _windows.Values) w.BeginEnter(EnterKind.FromDesktop);
        RenderSpaceThumbsAsync(model);
    }

    private ImageSource? CachedThumb(Guid space, IntPtr mon) => _thumbCache.TryGetValue((space, mon), out var img) ? img : null;

    /// <summary>Space miniatures need window captures, which are slow: compose them off the UI thread.</summary>
    private void RenderSpaceThumbsAsync(MissionControlModel model)
    {
        if (Mode != MissionControlMode.Full) return;
        var jobs = _windows.Values.Select(w => (win: w, mon: w.Monitor, size: w.SpaceThumbPixels)).ToList();
        // PrintWindow makes DWM render each window synchronously, which would stutter the fly-in: wait until it is over.
        Anim.After(620, () => Worker.Post(() =>
        {
            foreach (var job in jobs)
            {
                var thumbs = MissionControlWindow.RenderSpaceThumbs(model, job.mon, job.size.w, job.size.h);
                App.Current?.Dispatcher.BeginInvoke(() =>
                {
                    foreach (var (id, img) in thumbs) _thumbCache[(id, job.mon.Handle)] = img;
                    if (_open && !_closing) job.win.SetSpaceThumbs(thumbs);
                });
            }
        }));
    }

    private void OnRawMouse(MouseEventArgs2 e)
    {
        if (!IsDragMode || !IsOpen) return;
        foreach (var w in _windows.Values.ToArray()) w.ExternalMouse(e.Message, e.X, e.Y);
        if (e.Message == User32.WM_LBUTTONUP) App.Current.Dispatcher.BeginInvoke(FinishDrag);
    }

    private void FinishDrag()
    {
        if (!IsDragMode) return;
        var hwnd = _dragHwnd; _dragHwnd = IntPtr.Zero;
        User32.GetCursorPos(out var p);
        var target = _windows.Values.Select(w => w.SpaceDropTarget(p.X, p.Y)).FirstOrDefault(t => t != null);
        if (target == null) { CloseAnimated(IntPtr.Zero); return; }
        CloseNow();
        if (target.IsAddButton)
        {
            var sp = _vd.Create();
            if (sp != null) { _vd.MoveWindowToSpace(hwnd, sp); _vd.SwitchTo(sp); }
        }
        else if (target.Space != null)
        {
            if (target.Space.Id != _vd.Current?.Id) { _vd.MoveWindowToSpace(hwnd, target.Space); _vd.SwitchTo(target.Space); }
        }
    }

    public void Close() => CloseAnimated(IntPtr.Zero);

    private void CloseAnimated(IntPtr activate, Action? then = null)
    {
        if (!_open || _closing) return;
        _closing = true;
        int pending = _windows.Count;
        if (pending == 0) { CloseNow(); then?.Invoke(); return; }
        foreach (var w in _windows.Values.ToArray())
            w.BeginExit(activate, () => { if (--pending == 0) { CloseNow(); then?.Invoke(); } });
    }

    private void CloseNow()
    {
        foreach (var w in _windows.Values) { try { w.Finish(); } catch (Exception ex) { Log.Error("MC finish", ex); } }
        bool wasOpen = _open;
        _open = false; _closing = false; _dragHwnd = IntPtr.Zero;
        if (wasOpen) App.StageManager.OnOverlayOpened(false);
    }

    public void Refresh()
    {
        if (!_open || _closing) return;
        var model = BuildModel(Mode);
        foreach (var w in _windows.Values) { w.Prepare(model, Mode, IntPtr.Zero, EnterKind.FadeIn, id => CachedThumb(id, w.Monitor.Handle)); w.BeginEnter(EnterKind.FadeIn); }
        RenderSpaceThumbsAsync(model);
    }

    /// <summary>Switch spaces from inside Mission Control (Option‑click keeps it open).</summary>
    public void SwitchSpace(Space space, bool stayOpen)
    {
        if (!stayOpen)
        {
            CloseNow();
            _vd.SwitchTo(space);
            return;
        }
        _switchingInternally = true;
        try { _vd.SwitchTo(space, animated: false); }
        finally { _switchingInternally = false; }
        Anim.After(120, Refresh);
    }

    public void ActivateWindow(IntPtr hwnd)
    {
        if (!_open || _closing) return;
        App.ShowDesktop.OnUserActivity();
        var spaceId = _vd.GetWindowSpaceId(hwnd);
        if (spaceId != Guid.Empty && spaceId != _vd.Current?.Id && !_vd.IsWindowPinned(hwnd))
        {
            CloseNow();
            _vd.SwitchTo(spaceId);
            App.Activate(hwnd);
            return;
        }
        if (App.StageManager.IsStowed(hwnd))
        {
            // A window from the strip: it flies to its place on the stage while the current stage set flies to the
            // strip; when the overview is gone, Stage Manager swaps the real windows without a second animation.
            CloseAnimated(hwnd, () => { App.StageManager.RequestActivate(hwnd, animate: false); App.Activate(hwnd); });
            return;
        }
        // Activate underneath the overlay first, then let the thumbnails fly back onto the real windows.
        App.StageManager.RequestActivate(hwnd);
        App.Activate(hwnd);
        CloseAnimated(hwnd);
    }

    public MissionControlModel BuildModel(MissionControlMode mode)
    {
        var spaces = _vd.GetSpaces();
        var cur = _vd.Current?.Id ?? Guid.Empty;
        var fgInfo = WindowEnumerator.Describe(User32.GetForegroundWindow());
        var windows = WindowEnumerator.GetWindows(includeMinimized: true, includeOtherDesktops: true);
        var spaceOf = new Dictionary<IntPtr, Guid>();
        var pinned = new HashSet<IntPtr>();
        var parked = new HashSet<IntPtr>();
        foreach (var w in windows.ToList())
        {
            if (w.IsCloaked && Cloak.IsHidden(w.Hwnd))
            {
                // Hidden by WacOS (stowed in the Stage Manager strip): still a window of this space, shown like any other.
                w.IsCloaked = false; spaceOf[w.Hwnd] = cur; parked.Add(w.Hwnd);
            }
            else if (w.IsCloaked)
            {
                if (mode == MissionControlMode.AppExpose) { windows.Remove(w); continue; }
                spaceOf[w.Hwnd] = _vd.GetWindowSpaceId(w.Hwnd);
            }
            else { spaceOf[w.Hwnd] = cur; if (spaces.Count > 1 && _vd.IsWindowPinned(w.Hwnd)) pinned.Add(w.Hwnd); }
        }
        return new MissionControlModel
        {
            Spaces = spaces, CurrentSpace = cur, Windows = windows, WindowSpace = spaceOf, Pinned = pinned, Parked = parked,
            FrontAppKey = fgInfo?.AppKey, FrontAppName = fgInfo?.AppName,
        };
    }

    public void Shutdown()
    {
        CloseNow();
        foreach (var w in _windows.Values) { try { w.Close(); } catch { } }
        _windows.Clear();
    }
}
