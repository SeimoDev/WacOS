using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WacOS.Desktop;
using WacOS.Input;
using WacOS.Native;
using WacOS.Settings;
using WacOS.VirtualDesktops;

namespace WacOS.Features.StageManager;

/// <summary>macOS Stage Manager: the active app's windows on the stage, everything else as thumbnails in a left strip.</summary>
public sealed class StageManagerService
{
    private const double SwapMs = 560;   // spring: most of the travel happens in the first ~220 ms, then it settles

    private readonly VirtualDesktopService _vd;
    private readonly WindowTracker _tracker;
    private readonly MouseService _mouse;
    private readonly Dictionary<(Guid, IntPtr), StageState> _states = new();
    private readonly Dictionary<IntPtr, StripWindow> _strips = new();
    private readonly HashSet<IntPtr> _ourMinimize = new(), _ourRestore = new();
    private readonly HashSet<IntPtr> _hoverReveal = new();
    private readonly HashSet<IntPtr> _unmanageable = new();
    private readonly List<(WindowSet slot, MonitorInfo mon, List<Flyer.Ghost> ghosts)> _pendingParks = new();
    private readonly DispatcherTimer _evalTimer, _springTimer, _snapshotTimer;
    private bool _enabled, _overlayOpen, _animatePrev = true;
    private long _springSince; private WindowSet? _springSet; private bool _pressStartedInStrip;
    private long _suppressForegroundUntil;

    // ---- a stage window being dragged by the user (drop it on the strip to stow it) ----
    private readonly DispatcherTimer _dragTimer;
    private IntPtr _dragHwnd, _dragNearMon;
    private bool _dragIsMove;
    private RECT _dragPreFrame, _dragLastFrame, _dragMoveFrame;
    private int _dragRigidTicks, _dragStillTicks;
    private bool _dragButtonSeen, _dragClip, _dockSuspended;
    private StripWindow? _dragHintStrip;
    private readonly Dictionary<IntPtr, RECT> _restFrames = new();   // where stage windows sit when nobody drags them
    private readonly Dictionary<IntPtr, RECT> _dragPeers = new();    // other stage windows of the dragged app, at drag start
    private readonly HashSet<IntPtr> _snapshotTried = new();         // windows whose picture was already taken off the screen
    private readonly HashSet<IntPtr> _fillScheduled = new();
    private RECT _dragStartFrame;

    public bool IsEnabled => _enabled;
    public event Action<bool>? EnabledChanged;

    public StageManagerService(VirtualDesktopService vd, WindowTracker tracker, MouseService mouse, HotkeyService hotkeys)
    {
        _vd = vd; _tracker = tracker; _mouse = mouse;
        hotkeys.Register("ToggleStageManager", () => App.Settings.Current.ToggleStageManager, Toggle);
        hotkeys.Register("CycleAppWindows", () => App.Settings.Current.CycleAppWindows, CycleAppWindows);
        _tracker.ForegroundChanged += h => Safe(() => OnForeground(h));
        _tracker.WindowShown += h => Delay(80, () => OnWindowShown(h));
        _tracker.WindowDestroyed += h => Safe(() => OnWindowGone(h));
        _tracker.WindowHidden += h => Safe(() => OnWindowGone(h));
        _tracker.MinimizeStart += h => Safe(() => OnMinimizeStart(h));
        _tracker.MinimizeEnd += h => Safe(() => OnMinimizeEnd(h));
        _tracker.MoveSizeEnd += h => Safe(() => OnMoveSizeEnd(h));
        _tracker.Uncloaked += h => Safe(() => OnShellUncloaked(h));
        _tracker.LocationChanged += h => { if (_enabled) { _evalTimer.Start(); if (_dragHwnd == IntPtr.Zero && !_overlayOpen) Safe(() => BeginDragWatch(h)); } };
        _vd.CurrentChanged += (_, _) => Safe(OnSpaceChanged);
        _mouse.WallpaperClicked += mon => Safe(() => OnWallpaperClicked(mon));
        _mouse.LeftEdgeHover += (mon, on) => Safe(() => { if (on) _hoverReveal.Add(mon.Handle); else _hoverReveal.Remove(mon.Handle); EvaluateStrips(); });
        _mouse.Raw += e =>
        {
            // Runs on the hook thread: only note what happened and let the UI thread do the work.
            if (e.Message == User32.WM_LBUTTONDOWN)
            {
                try { _pressStartedInStrip = _strips.Values.Any(s => s.IsShown && s.ScreenRect.Contains(e.X, e.Y)); } catch { }
                int x = e.X, y = e.Y;
                if (_enabled) App.Current?.Dispatcher.BeginInvoke(() => Safe(() => BeginDragWatch(User32.GetAncestor(User32.WindowFromPoint(new POINT(x, y)), User32.GA_ROOT))));
            }
            else if (e.Message == User32.WM_LBUTTONUP && _dragHwnd != IntPtr.Zero)
                App.Current?.Dispatcher.BeginInvoke(() => Safe(EndDragWatch));
        };
        _evalTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _evalTimer.Tick += (_, _) => { _evalTimer.Stop(); EvaluateStrips(); };
        _springTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _springTimer.Tick += (_, _) => SpringTick();
        _dragTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(30) };
        _dragTimer.Tick += (_, _) => Safe(DragTick);
        _snapshotTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
        _snapshotTimer.Tick += (_, _) => SnapshotStage();
        App.Settings.Changed += OnSettingsChanged;
    }

    private static void Safe(Action a) { try { a(); } catch (Exception ex) { Log.Error("StageManager", ex); } }
    private static void Delay(int ms, Action a)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) => { t.Stop(); Safe(a); };
        t.Start();
    }

    // ================================================================ enable / disable

    public void Toggle() => SetEnabled(!_enabled);

    public void SetEnabled(bool on, bool persist = true)
    {
        if (_enabled == on) return;
        if (on) Enable(); else Disable();
        if (persist && App.Settings.Current.StageManagerEnabled != on) App.Settings.Update(s => s.StageManagerEnabled = on);
        EnabledChanged?.Invoke(on);
    }

    private void Enable()
    {
        _enabled = true;
        _animatePrev = SystemMinimizeAnimation.Get();
        SystemMinimizeAnimation.Set(false);
        EdgeSnap.Suspend(App.Settings.Current.StageManagerBlocksEdgeSnap);
        DesktopIcons.SetVisible(App.Settings.Current.ShowItemsInStageManager);
        foreach (var mon in Monitors.All()) EnsureStrip(mon);
        foreach (var mon in Monitors.All())
        {
            GetState(mon, build: true);
            RenderStrip(mon);
        }
        _springTimer.Start(); _snapshotTimer.Start();
        EvaluateStrips();
        Log.Info("Stage Manager on");
    }

    private void Disable()
    {
        _enabled = false;
        _springTimer.Stop(); _snapshotTimer.Stop(); _dragTimer.Stop();
        ReleaseClip(); CancelThumbDrag();
        _dragHwnd = IntPtr.Zero; _dragHintStrip = null; _restFrames.Clear();
        // Windows fly back out of the strip to where they were.
        var ghosts = new List<Flyer.Ghost>();
        var restore = new List<IntPtr>();
        foreach (var ((space, monHandle), st) in _states)
        {
            bool current = space == _vd.CurrentId;
            _strips.TryGetValue(monHandle, out var strip);
            foreach (var set in Enumerable.Reverse(st.Strip))
                foreach (var h in Enumerable.Reverse(set.Windows))
                {
                    if (!User32.IsWindow(h) || !IsParked(h)) continue;
                    restore.Add(h);
                    if (_quitting || !current || strip == null || !strip.HasSlot(set)) continue;
                    bool iconic = User32.IsIconic(h);
                    var frame = iconic ? (set.Frames.TryGetValue(h, out var f) && f.Width > 0 ? f : default) : Dwm.GetFrameBounds(h);
                    if (frame.Width <= 0) continue;
                    var g = Flyer.Add(h, Anim.Fit(strip.SlotRect(set), frame.Width, frame.Height), 215, iconic ? set.Crops.GetValueOrDefault(h) : null);
                    if (g == null) continue;
                    g.To = frame; g.OpacityTo = 255;
                    ghosts.Add(g);
                }
        }
        _states.Clear(); _pendingParks.Clear();
        foreach (var s in _strips.Values) { try { s.Close(); } catch { } }
        _strips.Clear();
        void RestoreAll()
        {
            foreach (var h in restore)
            {
                if (!User32.IsWindow(h)) continue;
                if (User32.IsIconic(h)) { _ourRestore.Add(h); User32.ShowWindow(h, User32.SW_SHOWNOACTIVATE); }
                Cloak.Show(h);
            }
            SystemMinimizeAnimation.Set(_animatePrev);
        }
        EdgeSnap.Suspend(false);
        if (ghosts.Count > 0 && App.Current != null && !_quitting)
        {
            for (int i = 0; i < ghosts.Count; i++) ghosts[i].DelayMs = Math.Min(120, 14 * (ghosts.Count - 1 - i));
            Flyer.Animate(ghosts, SwapMs, Easing.Soft, RestoreAll, holdMs: 50, opacityEase: Easing.OutCubic);
        }
        else { foreach (var g in ghosts) Flyer.Remove(g); RestoreAll(); }
        DesktopIcons.SetVisible(App.Settings.Current.ShowItemsOnDesktop);
        Log.Info("Stage Manager off");
    }

    private bool _quitting;
    /// <summary>Turns Stage Manager off synchronously (used on exit, when no animation can run).</summary>
    public void Shutdown() { _quitting = true; if (_enabled) { Disable(); EnabledChanged?.Invoke(false); } }

    private void OnSettingsChanged()
    {
        var s = App.Settings.Current;
        if (s.StageManagerEnabled != _enabled && !(App.ForceStageManager && _enabled)) { if (s.StageManagerEnabled) Enable(); else Disable(); EnabledChanged?.Invoke(_enabled); }
        if (_enabled) { DesktopIcons.SetVisible(s.ShowItemsInStageManager); EdgeSnap.Suspend(s.StageManagerBlocksEdgeSnap); EvaluateStrips(); }
        else DesktopIcons.SetVisible(s.ShowItemsOnDesktop);
    }

    public void OnOverlayOpened(bool open) { _overlayOpen = open; if (_enabled) EvaluateStrips(); }

    // ================================================================ state

    private StripWindow EnsureStrip(MonitorInfo mon)
    {
        if (!_strips.TryGetValue(mon.Handle, out var s)) { s = new StripWindow(this, mon); _strips[mon.Handle] = s; }
        return s;
    }

    private StageState GetState(MonitorInfo mon, bool build)
    {
        var key = (_vd.CurrentId, mon.Handle);
        if (_states.TryGetValue(key, out var st)) return st;
        // A pure lookup must never register an empty state: that would make the real initial build be skipped
        // (event handlers can run re-entrantly while a COM call pumps messages).
        if (!build) return new StageState();
        st = new StageState();
        _states[key] = st;
        BuildInitial(st, mon);
        return st;
    }

    private void BuildInitial(StageState st, MonitorInfo mon)
    {
        var windows = WindowEnumerator.GetWindows(includeMinimized: true).Where(w => w.Monitor == mon.Handle && !w.IsFullscreen).ToList();
        var fg = User32.GetForegroundWindow();
        Log.Debug($"initial state on {mon.Device}: fg={fg:X} windows={windows.Count}");
        bool allAtOnce = App.Settings.Current.ShowWindowsFromApplication == ShowWindowsMode.AllAtOnce;
        var sets = new List<WindowSet>();
        foreach (var w in windows)
        {
            if (_unmanageable.Contains(w.Hwnd) || !OnCurrentSpace(w.Hwnd)) continue;
            var set = allAtOnce ? sets.FirstOrDefault(s => s.AppKey == w.AppKey) : null;
            if (set == null) { set = new WindowSet(); sets.Add(set); }
            set.Add(w, front: false);
        }
        var stage = sets.FirstOrDefault(s => s.Contains(fg));
        if (stage != null && stage.Windows.All(IsParked)) stage = null;
        st.Stage = stage;
        foreach (var s in sets.Where(s => s != stage)) st.Strip.Add(s);
        // Park back-to-front so the ghosts keep the real stacking while they fly into the strip.
        foreach (var s in Enumerable.Reverse(st.Strip)) ParkSet(s, mon, animate: true);
        if (stage != null) foreach (var h in stage.Windows) EnsureRightOfStrip(h, mon);
    }

    /// <summary>
    /// True when the window belongs to the space the user is on. Stage Manager state is kept per space, so a window of
    /// another space must never be filed under (or animated on) the current one.
    /// </summary>
    private bool OnCurrentSpace(IntPtr hwnd)
    {
        var id = _vd.GetWindowSpaceId(hwnd);
        if (id == Guid.Empty || id == _vd.CurrentId) return true;
        return _vd.IsWindowPinned(hwnd);   // shown on all desktops
    }

    private StageState? StateFor(IntPtr hwnd, out MonitorInfo? mon)
    {
        mon = Monitors.FromWindow(hwnd);
        if (mon == null) return null;
        return GetState(mon, build: true);
    }

    // ================================================================ core operations

    /// <summary>A window that lives in the strip: hidden by the shell (normal case) or minimized (fallback / user minimize).</summary>
    private static bool IsParked(IntPtr h) => User32.IsIconic(h) || Cloak.IsHidden(h);

    /// <summary>Hides a window without making the app repaint later; falls back to minimizing when the shell refuses.</summary>
    private bool HideWindow(IntPtr h)
    {
        if (Cloak.Hide(h)) return true;
        _ourMinimize.Add(h);
        User32.ShowWindow(h, User32.SW_SHOWMINNOACTIVE);
        if (User32.IsIconic(h)) return true;
        _ourMinimize.Remove(h);
        return false;
    }

    /// <summary>
    /// Sends the set's windows into the strip: a live ghost replaces each window, the real window is hidden
    /// underneath, and the ghosts fly to the strip slot once the strip has been laid out (see <see cref="RenderStrip"/>).
    /// </summary>
    private void ParkSet(WindowSet set, MonitorInfo mon, bool animate, WindowSet? slotOwner = null)
    {
        EnsureStrip(mon);
        Log.Debug($"park [{set.AppName}] {string.Join(",", set.Windows.Select(h => h.ToString("X")))}");
        var ghosts = new List<Flyer.Ghost>();
        var made = new Dictionary<IntPtr, Flyer.Ghost?>();
        var fg = User32.GetForegroundWindow();
        // Ghosts are stacked in creation order: bottom-most window first.
        foreach (var h in Enumerable.Reverse(set.Windows))
        {
            if (!User32.IsWindow(h) || IsParked(h)) continue;
            var frame = Dwm.GetFrameBounds(h);
            set.Frames[h] = frame;
            var g = animate ? Flyer.Add(h, frame) : null;
            made[h] = g;
            set.Crops[h] = g?.Thumb.Crop ?? Thumb.ComputeCrop(h);
            var bmp = WindowCapture.Capture(h, allowCached: true, maxAgeMs: 3000);
            if (bmp == null && g != null)
            {
                // PrintWindow is refused by elevated apps. The ghost, however, is a live picture drawn by the
                // compositor and is on top of everything right now: read it back from the screen.
                var vis = new RECT(Math.Max(frame.Left, mon.Bounds.Left), Math.Max(frame.Top, mon.Bounds.Top), Math.Min(frame.Right, mon.Bounds.Right), Math.Min(frame.Bottom, mon.Bounds.Bottom));
                if (vis.Width > 40 && vis.Height > 40 && (long)vis.Width * vis.Height >= (long)frame.Width * frame.Height * 9 / 10)
                {
                    DwmThumb.DwmFlush(); DwmThumb.DwmFlush();
                    bmp = WindowCapture.CaptureScreen(vis);
                    if (bmp != null) _snapshotTried.Add(h);
                }
            }
            if (bmp != null) set.Snapshots[h] = bmp;
        }
        foreach (var h in set.Windows.ToList())
        {
            if (!made.TryGetValue(h, out var g)) continue;
            if (!HideWindow(h))
            {
                // Neither the shell nor we may touch this window (elevated process): leave it on the desktop.
                _unmanageable.Add(h);
                Flyer.Remove(g);
                Log.Warn($"Cannot manage window {h:X} '{set.Info.GetValueOrDefault(h)?.Title}' (elevated process?) - run WacOS as administrator to include it");
                continue;
            }
            if (g != null) ghosts.Add(g);
        }
        foreach (var h in set.Windows.Where(_unmanageable.Contains).ToList()) set.Remove(h);
        // Keyboard focus must not stay inside a hidden window.
        if (set.Contains(fg)) User32.ForceForeground(User32.GetShellWindow());
        if (ghosts.Count > 0) _pendingParks.Add((slotOwner ?? set, mon, ghosts));
    }

    /// <summary>Starts the fly-to-strip animation of everything parked since the last strip layout.</summary>
    private void FlushParks(MonitorInfo mon, StripWindow strip)
    {
        foreach (var p in _pendingParks.Where(p => p.mon.Handle == mon.Handle).ToList())
        {
            _pendingParks.Remove(p);
            bool hasSlot = strip.IsShown && strip.HasSlot(p.slot);
            var slot = strip.SlotOnScreen(p.slot, flat: hasSlot);
            for (int i = 0; i < p.ghosts.Count; i++)
            {
                var g = p.ghosts[i];
                g.To = Anim.Fit(slot, g.From.Width, g.From.Height);
                g.OpacityTo = 0;
                g.DelayMs = 26 * (p.ghosts.Count - 1 - i);   // the front window leads, the ones behind follow
            }
            var owner = p.slot;
            // The spring has covered almost the whole distance after ~40 % of its time. From there the strip's own
            // thumbnail takes over (fading in flat, then swinging into its tilt) while the flying picture fades out.
            if (hasSlot) Anim.After(SwapMs * 0.38, () => strip.ArriveItem(owner));
            Flyer.Animate(p.ghosts, SwapMs, Easing.Soft, hasSlot ? null : () => strip.RevealItem(owner), holdMs: 0, opacityEase: hasSlot ? Easing.LateFade : Easing.Standard);
        }
    }

    /// <summary>Where a parked window will sit on the stage (its old frame, nudged right of the strip when it fits).</summary>
    private RECT TargetFrame(WindowSet set, IntPtr h, MonitorInfo mon)
    {
        RECT frame;
        if (User32.IsIconic(h))
        {
            var wp = new WINDOWPLACEMENT { length = System.Runtime.InteropServices.Marshal.SizeOf<WINDOWPLACEMENT>() };
            User32.GetWindowPlacement(h, ref wp);
            if ((wp.flags & 0x2 /* WPF_RESTORETOMAXIMIZED */) != 0) return mon.WorkArea;
            frame = set.Frames.TryGetValue(h, out var f) && f.Width > 0 ? f : wp.rcNormalPosition;
        }
        else
        {
            if (User32.IsZoomed(h)) return Dwm.GetFrameBounds(h);
            frame = set.Frames.TryGetValue(h, out var f) && f.Width > 0 ? f : Dwm.GetFrameBounds(h);
        }
        if (!App.Settings.Current.ShowRecentApps) return frame;
        int stripRight = EnsureStrip(mon).ScreenRect.Right + (int)(10 * mon.Scale);
        if (frame.Left >= stripRight || stripRight + frame.Width > mon.WorkArea.Right) return frame;
        return new RECT(stripRight, frame.Top, stripRight + frame.Width, frame.Bottom);
    }

    /// <summary>
    /// Brings parked windows onto the stage: ghosts fly out of the strip slot (or continue from where the user dragged
    /// them), then the real windows are revealed underneath.
    /// </summary>
    private void UnparkWindows(WindowSet owner, IReadOnlyList<IntPtr> windows, MonitorInfo mon, bool animate, bool activate, RECT fromSlot, Func<bool> stillOnStage,
        IReadOnlyDictionary<IntPtr, Flyer.Ghost>? carried = null)
    {
        Log.Debug($"unpark [{owner.AppName}] {string.Join(",", windows.Select(h => h.ToString("X")))} activate={activate}");
        owner.LastActive = Environment.TickCount64;
        var front = windows.FirstOrDefault(User32.IsWindow);
        var parked = windows.Where(h => User32.IsWindow(h) && IsParked(h)).ToList();
        foreach (var h in windows.Where(h => User32.IsWindow(h) && !IsParked(h))) EnsureRightOfStrip(h, mon);
        // Hidden windows can be put in place right now (invisibly), so the frame the ghost lands on is the real one.
        var targets = new Dictionary<IntPtr, RECT>();
        foreach (var h in parked)
        {
            targets[h] = TargetFrame(owner, h, mon);
            if (!User32.IsIconic(h))
            {
                MoveFrameTo(h, targets[h]);
                var actual = Dwm.GetFrameBounds(h);
                if (actual.Width > 0 && actual.Height > 0) targets[h] = actual;   // never animate to a place the window will not be
            }
        }

        void RevealReal()
        {
            foreach (var h in Enumerable.Reverse(parked))
            {
                if (!User32.IsWindow(h)) continue;
                if (User32.IsIconic(h))
                {
                    _ourRestore.Add(h);
                    User32.ShowWindow(h, User32.SW_SHOWNOACTIVATE);
                    MoveFrameTo(h, targets[h]);
                }
                Cloak.Show(h);
                _restFrames[h] = Dwm.GetFrameBounds(h);
            }
            // Re-assert the stacking of the whole set (front window last), including windows that were never hidden.
            foreach (var h in Enumerable.Reverse(windows))
                if (User32.IsWindow(h)) User32.SetWindowPos(h, User32.HWND_TOP, 0, 0, 0, 0, User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
            if (activate && front != IntPtr.Zero) { _suppressForegroundUntil = Environment.TickCount64 + 300; App.Activate(front); }
            EvaluateStrips();
        }

        var ghosts = new List<Flyer.Ghost>();
        if (animate)
        {
            int n = 0;
            foreach (var h in Enumerable.Reverse(parked))
            {
                var target = targets[h];
                Flyer.Ghost? g = null;
                bool wasCarried = carried != null && carried.TryGetValue(h, out g);
                g ??= Flyer.Add(h, Anim.Fit(fromSlot, target.Width, target.Height), 215, User32.IsIconic(h) ? owner.Crops.GetValueOrDefault(h) : null);
                if (g == null) continue;
                g.To = target; g.OpacityTo = 255;
                g.DelayMs = wasCarried ? 0 : 28 * (parked.Count - 1 - n);   // the front window leads
                n++;
                ghosts.Add(g);
            }
        }
        // Ghosts the user was dragging that have no window to land on.
        if (carried != null) foreach (var g in carried.Values) if (!ghosts.Contains(g)) Flyer.Remove(g);
        if (ghosts.Count == 0) { RevealReal(); return; }
        _suppressForegroundUntil = Environment.TickCount64 + (long)SwapMs + 400;
        // A hidden window shows exactly what its ghost shows, so the hand-over needs only a couple of frames.
        Flyer.Animate(ghosts, carried != null ? 420 : SwapMs, Easing.Bouncy, () =>
        {
            if (_enabled && stillOnStage()) RevealReal();
        }, holdMs: 50, opacityEase: Easing.OutCubic);
    }

    // ---- queries used by Mission Control, which shows stowed windows too ----

    public bool IsStowed(IntPtr hwnd) => _enabled && Cloak.IsHidden(hwnd) && FindStripSet(hwnd, out _, out _) != null;

    private WindowSet? FindStripSet(IntPtr hwnd, out StageState? state, out MonitorInfo? mon)
    {
        state = null; mon = null;
        var cur = _vd.CurrentId;
        foreach (var ((space, monHandle), st) in _states)
        {
            if (space != cur) continue;
            var set = st.Strip.FirstOrDefault(x => x.Contains(hwnd));
            if (set != null) { state = st; mon = Monitors.Get(monHandle); return set; }
        }
        return null;
    }

    /// <summary>Screen rectangle of the strip item that holds a stowed window (null when it has no visible item).</summary>
    public RECT? StripSlotOf(IntPtr hwnd)
    {
        var set = FindStripSet(hwnd, out _, out var mon);
        if (set == null || mon == null || !_strips.TryGetValue(mon.Handle, out var strip) || !strip.HasSlot(set)) return null;
        return strip.SlotRectFlat(set);
    }

    public RECT? TopSlot(MonitorInfo mon) => _strips.TryGetValue(mon.Handle, out var strip) ? strip.SlotRectFlat(null) : null;

    /// <summary>Where a stowed window will sit once it is back on the stage.</summary>
    public RECT StageFrameOf(IntPtr hwnd)
    {
        var set = FindStripSet(hwnd, out _, out var mon);
        return set != null && mon != null ? TargetFrame(set, hwnd, mon) : Dwm.GetFrameBounds(hwnd);
    }

    public IReadOnlyCollection<IntPtr> SetWindowsOf(IntPtr hwnd) => FindStripSet(hwnd, out _, out _)?.Windows.ToList() ?? new List<IntPtr>();

    public IReadOnlyCollection<IntPtr> StageWindows(MonitorInfo mon) => GetState(mon, build: false).Stage?.Windows.ToList() ?? new List<IntPtr>();

    public void BringToStage(MonitorInfo mon, WindowSet set, IReadOnlyDictionary<IntPtr, Flyer.Ghost>? carried = null, bool animate = true)
    {
        if (!_enabled) { if (carried != null) foreach (var g in carried.Values) Flyer.Remove(g); return; }
        var st = GetState(mon, build: true);
        var strip = EnsureStrip(mon);
        if (st.Stage == set) { UnparkWindows(set, set.Windows.ToList(), mon, animate: carried != null, activate: true, default, () => true, carried); return; }
        var fromSlot = strip.SlotOnScreen(set);
        st.Strip.Remove(set);
        var old = st.Stage;
        st.Stage = set; st.DesktopRevealed = false;
        if (old != null && !old.IsEmpty) { st.Strip.Insert(0, old); ParkSet(old, mon, animate: animate); }
        RenderStrip(mon);
        UnparkWindows(set, set.Windows.ToList(), mon, animate: animate, activate: true, fromSlot, () => st.Stage == set, carried);
    }

    public void MergeToStage(MonitorInfo mon, WindowSet set, IReadOnlyDictionary<IntPtr, Flyer.Ghost>? carried = null)
    {
        if (!_enabled) { if (carried != null) foreach (var g in carried.Values) Flyer.Remove(g); return; }
        var st = GetState(mon, build: true);
        if (st.Stage == null || st.Stage == set) { BringToStage(mon, set, carried); return; }
        var strip = EnsureStrip(mon);
        var fromSlot = strip.SlotOnScreen(set);
        var stage = st.Stage;
        var merged = set.Windows.ToList();
        st.Strip.Remove(set);
        foreach (var h in Enumerable.Reverse(merged))
        {
            if (!set.Info.TryGetValue(h, out var wi)) continue;
            stage.Add(wi, front: true);
            if (set.Snapshots.TryGetValue(h, out var b)) stage.Snapshots[h] = b;
            if (set.Frames.TryGetValue(h, out var f)) stage.Frames[h] = f;
            if (set.Crops.TryGetValue(h, out var c)) stage.Crops[h] = c;
        }
        st.DesktopRevealed = false;
        RenderStrip(mon);
        UnparkWindows(stage, merged, mon, animate: true, activate: true, fromSlot, () => st.Stage == stage, carried);
    }

    // ---- dragging a thumbnail out of the strip: its windows follow the pointer and land where they are dropped ----
    private sealed class ThumbDrag
    {
        public WindowSet Set = null!;
        public MonitorInfo Mon = null!;
        public RECT Slot;
        public List<(IntPtr h, Flyer.Ghost g, RECT frame)> Items = new();   // bottom-most first
    }
    private ThumbDrag? _thumbDrag;

    public void BeginThumbDrag(MonitorInfo mon, WindowSet set)
    {
        CancelThumbDrag();
        var strip = EnsureStrip(mon);
        var d = new ThumbDrag { Set = set, Mon = mon, Slot = strip.SlotOnScreen(set) };
        foreach (var h in Enumerable.Reverse(set.Windows.Take(4)))
        {
            if (!User32.IsWindow(h) || !IsParked(h)) continue;
            var frame = TargetFrame(set, h, mon);
            if (frame.Width <= 0 || frame.Height <= 0) continue;
            var g = Flyer.Add(h, Anim.Fit(d.Slot, frame.Width, frame.Height), 240, User32.IsIconic(h) ? set.Crops.GetValueOrDefault(h) : null);
            if (g != null) d.Items.Add((h, g, frame));
        }
        _thumbDrag = d;
    }

    private static RECT BoundsOf(IEnumerable<RECT> rects)
    {
        int l = int.MaxValue, t = int.MaxValue, r = int.MinValue, b = int.MinValue;
        foreach (var x in rects) { l = Math.Min(l, x.Left); t = Math.Min(t, x.Top); r = Math.Max(r, x.Right); b = Math.Max(b, x.Bottom); }
        return l == int.MaxValue ? default : new RECT(l, t, r, b);
    }

    /// <summary>
    /// The windows follow the pointer in their real arrangement (side by side, overlapping – whatever it is), scaled
    /// as one group that grows from thumbnail size towards a comfortable preview size as it leaves the strip.
    /// </summary>
    public void MoveThumbDrag(int x, int y)
    {
        var d = _thumbDrag; if (d == null || d.Items.Count == 0) return;
        var strip = EnsureStrip(d.Mon);
        double travel = Math.Clamp((x - strip.ScreenRect.Right + 60 * d.Mon.Scale) / (520.0 * d.Mon.Scale), 0, 1);
        double k = travel * travel * (3 - 2 * travel);
        var box = BoundsOf(d.Items.Select(i => i.frame));
        if (box.Width <= 0 || box.Height <= 0) return;
        var small = Anim.Fit(d.Slot, box.Width, box.Height);
        double gw = small.Width + (box.Width * 0.62 - small.Width) * k;
        double sc = gw / box.Width, gh = box.Height * sc;
        // The pointer holds the group near its top edge, like a title bar.
        double ox = x - gw / 2, oy = y - gh * 0.12;
        foreach (var (_, g, frame) in d.Items)
        {
            double l = ox + (frame.Left - box.Left) * sc, t = oy + (frame.Top - box.Top) * sc;
            Flyer.Place(g, new RECT((int)l, (int)t, (int)(l + frame.Width * sc), (int)(t + frame.Height * sc)), 245);
        }
    }

    /// <summary>Returns true when the set was dropped on the stage (it then joins the current group where it was dropped).</summary>
    public bool EndThumbDrag(int x, int y)
    {
        var d = _thumbDrag; _thumbDrag = null;
        if (d == null) return false;
        var strip = EnsureStrip(d.Mon);
        if (!_enabled || d.Items.Count == 0 || x <= strip.ScreenRect.Right)
        {
            foreach (var it in d.Items) { it.g.To = Anim.Fit(d.Slot, it.frame.Width, it.frame.Height); it.g.OpacityTo = 255; }
            var set = d.Set;
            Flyer.Animate(d.Items.Select(i => i.g).ToList(), 420, Easing.Bouncy, () => strip.RevealItem(set), holdMs: 80);
            return false;
        }
        // Land where dropped: the group keeps its arrangement and is placed under the pointer, inside the work area.
        var box = BoundsOf(d.Items.Select(i => i.frame));
        var wa = d.Mon.WorkArea;
        int left = Math.Clamp(x - box.Width / 2, wa.Left, Math.Max(wa.Left, wa.Right - box.Width));
        int top = Math.Clamp(y - (int)(box.Height * 0.12), wa.Top, Math.Max(wa.Top, wa.Bottom - box.Height));
        int dx = left - box.Left, dy = top - box.Top;
        var carried = new Dictionary<IntPtr, Flyer.Ghost>();
        var shown = d.Items.Select(i => i.h).ToHashSet();
        foreach (var h in d.Set.Windows)
        {
            // Windows of the set that had no ghost keep their place relative to the others.
            if (shown.Contains(h) || !User32.IsWindow(h) || User32.IsZoomed(h)) continue;
            var f = TargetFrame(d.Set, h, d.Mon);
            if (f.Width > 0) d.Set.Frames[h] = new RECT(f.Left + dx, f.Top + dy, f.Right + dx, f.Bottom + dy);
        }
        foreach (var (h, g, frame) in d.Items)
        {
            if (!User32.IsZoomed(h)) d.Set.Frames[h] = new RECT(frame.Left + dx, frame.Top + dy, frame.Right + dx, frame.Bottom + dy);
            carried[h] = g;
        }
        MergeToStage(d.Mon, d.Set, carried);
        return true;
    }

    private void CancelThumbDrag()
    {
        var d = _thumbDrag; _thumbDrag = null;
        if (d != null) foreach (var it in d.Items) Flyer.Remove(it.g);
    }

    private void SendWindowToStrip(StageState st, MonitorInfo mon, IntPtr hwnd, bool animate)
        => SendWindowsToStrip(st, mon, new[] { hwnd }, animate);

    /// <summary>Moves one or more stage windows (a window and the companions that belong to it) into one strip item.</summary>
    private void SendWindowsToStrip(StageState st, MonitorInfo mon, IReadOnlyList<IntPtr> hwnds, bool animate)
    {
        var stage = st.Stage;
        if (stage == null) return;
        // Keep the stage's front-to-back order.
        var group = stage.Windows.Where(hwnds.Contains).ToList();
        if (group.Count == 0) return;
        var infos = group.ToDictionary(h => h, h => stage.Info[h]);
        foreach (var h in group) stage.Remove(h);
        if (stage.IsEmpty) st.Stage = null;
        var target = StripSetFor(st, infos[group[0]]);
        var unit = new WindowSet();
        foreach (var h in group) unit.Add(infos[h], front: false);
        ParkSet(unit, mon, animate, slotOwner: target);
        foreach (var h in Enumerable.Reverse(unit.Windows.ToList()))
        {
            target.Add(infos[h], front: true);
            if (unit.Snapshots.TryGetValue(h, out var bmp)) target.Snapshots[h] = bmp;
            if (unit.Frames.TryGetValue(h, out var f)) target.Frames[h] = f;
            if (unit.Crops.TryGetValue(h, out var c)) target.Crops[h] = c;
        }
        RenderStrip(mon);
    }

    /// <summary>The strip set a window belongs to when it leaves the stage (its app's set, moved to the top, or a new one).</summary>
    private static WindowSet StripSetFor(StageState st, WindowInfo wi)
    {
        bool allAtOnce = App.Settings.Current.ShowWindowsFromApplication == ShowWindowsMode.AllAtOnce;
        var target = allAtOnce ? st.FindStripSetForApp(wi.AppKey) : null;
        if (target == null) target = new WindowSet(); else st.Strip.Remove(target);
        st.Strip.Insert(0, target);
        return target;
    }

    private void RevealDesktop(StageState st, MonitorInfo mon)
    {
        if (st.Stage == null) return;
        var set = st.Stage;
        st.Stage = null; st.DesktopRevealed = true;
        st.Strip.Insert(0, set);
        ParkSet(set, mon, animate: true);
        RenderStrip(mon);
    }

    /// <summary>Show Desktop while Stage Manager is on: the stage empties into the strip, and comes back on the next toggle.</summary>
    public void ToggleDesktop()
    {
        if (!_enabled) return;
        foreach (var mon in Monitors.All())
        {
            var st = GetState(mon, build: true);
            if (st.Stage != null) RevealDesktop(st, mon);
            else if (st.DesktopRevealed && st.Strip.Count > 0) BringToStage(mon, st.Strip[0]);
        }
    }

    private void HandleNewWindow(WindowInfo wi)
    {
        var mon = Monitors.Get(wi.Monitor); if (mon == null) return;
        var st = GetState(mon, build: true);
        if (st.FindSet(wi.Hwnd) != null) return;
        if (wi.IsFullscreen || _unmanageable.Contains(wi.Hwnd)) return;
        if (!OnCurrentSpace(wi.Hwnd)) return;
        Log.Debug($"new window {wi.Hwnd:X} '{wi.Title}' key={wi.AppKey}");
        bool allAtOnce = App.Settings.Current.ShowWindowsFromApplication == ShowWindowsMode.AllAtOnce;
        if (st.Stage != null && allAtOnce && st.Stage.AppKey == wi.AppKey)
        {
            st.Stage.Add(wi, front: true);
            EnsureRightOfStrip(wi.Hwnd, mon);
            EvaluateStrips();
            return;
        }
        var existing = allAtOnce ? st.FindStripSetForApp(wi.AppKey) : null;
        if (existing != null)
        {
            existing.Add(wi, front: true);
            BringToStage(mon, existing);
            return;
        }
        var set = new WindowSet(); set.Add(wi);
        var old = st.Stage;
        st.Stage = set; st.DesktopRevealed = false;
        if (old != null && !old.IsEmpty) { st.Strip.Insert(0, old); ParkSet(old, mon, animate: true); }
        EnsureRightOfStrip(wi.Hwnd, mon);
        RenderStrip(mon);
    }

    /// <summary>Moves a restored window so that its visible frame sits at the rectangle the animation flew to.</summary>
    private static void MoveFrameTo(IntPtr hwnd, RECT target)
    {
        if (!User32.IsWindow(hwnd) || User32.IsIconic(hwnd) || User32.IsZoomed(hwnd)) return;
        var frame = Dwm.GetFrameBounds(hwnd);
        if (Math.Abs(frame.Left - target.Left) <= 1 && Math.Abs(frame.Top - target.Top) <= 1) return;
        if (Math.Abs(frame.Width - target.Width) > 8 || Math.Abs(frame.Height - target.Height) > 8) return; // size differs: leave it alone
        User32.GetWindowRect(hwnd, out var wr);
        User32.SetWindowPos(hwnd, IntPtr.Zero, wr.Left + (target.Left - frame.Left), wr.Top + (target.Top - frame.Top), 0, 0, User32.SWP_NOSIZE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);
    }

    /// <summary>Shift a window right so it doesn't sit under the strip, when it fits.</summary>
    private void EnsureRightOfStrip(IntPtr hwnd, MonitorInfo mon)
    {
        if (!User32.IsWindow(hwnd) || User32.IsIconic(hwnd) || User32.IsZoomed(hwnd)) return;
        var strip = EnsureStrip(mon);
        if (!App.Settings.Current.ShowRecentApps) return;
        int stripRight = strip.ScreenRect.Right + (int)(10 * mon.Scale);
        var frame = Dwm.GetFrameBounds(hwnd);
        if (frame.Left >= stripRight) return;
        if (stripRight + frame.Width > mon.WorkArea.Right) return; // doesn't fit → the strip will auto-hide instead
        User32.GetWindowRect(hwnd, out var wr);
        int delta = frame.Left - wr.Left;
        User32.SetWindowPos(hwnd, IntPtr.Zero, stripRight - delta, wr.Top, 0, 0, User32.SWP_NOSIZE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);
    }

    // ================================================================ strip rendering & visibility

    private void RenderStrip(MonitorInfo mon)
    {
        var strip = EnsureStrip(mon);
        var st = GetState(mon, build: false);
        // Drop dead windows.
        foreach (var set in st.AllSets.ToList())
        {
            foreach (var h in set.Windows.ToList())
            {
                if (!User32.IsWindow(h)) { set.Remove(h); continue; }
                if (!OnCurrentSpace(h))
                {
                    // Moved to another space (by the user, or because it went full screen): it is that space's window now.
                    Log.Debug($"window {h:X} left this space");
                    set.Remove(h);
                    Cloak.ReleaseElsewhere(h);
                }
            }
            if (set.IsEmpty) { st.Strip.Remove(set); if (st.Stage == set) st.Stage = null; }
        }
        // Items whose windows are still flying towards them stay invisible until the ghosts land.
        var incoming = _pendingParks.Where(p => p.mon.Handle == mon.Handle).Select(p => p.slot).ToHashSet();
        strip.SetItems(st.Strip.Take(strip.MaxItems).ToList(), incoming);
        EvaluateStrips();
        FlushParks(mon, strip);
        ScheduleSnapshotFill(mon);
    }

    private void ScheduleSnapshotFill(MonitorInfo mon)
    {
        if (!_fillScheduled.Add(mon.Handle)) return;
        Anim.After(900, () => { _fillScheduled.Remove(mon.Handle); Safe(() => FillMissingSnapshots(mon)); });
    }

    /// <summary>
    /// Thumbnails that could not be captured (the window was minimized when Stage Manager started, belongs to an
    /// elevated app, was partly off screen …) are taken from the compositor instead: a live thumbnail of the window
    /// is shown for two frames exactly on its strip slot and read back from the screen.
    /// </summary>
    private void FillMissingSnapshots(MonitorInfo mon)
    {
        if (!_enabled || !_strips.TryGetValue(mon.Handle, out var strip)) return;
        if (_overlayOpen || !strip.IsShown || _dragHwnd != IntPtr.Zero || _thumbDrag != null || _pendingParks.Count > 0
            || Environment.TickCount64 < _suppressForegroundUntil)
        {
            // Not a good moment (animation running, strip away): try again later if something is still missing.
            var pending = GetState(mon, build: false).Strip.Any(x => x.Windows.Take(4).Any(h => x.Snapshots.GetValueOrDefault(h) == null && !_snapshotTried.Contains(h)));
            if (pending && _enabled) Anim.After(1500, () => { if (_enabled) ScheduleSnapshotFill(mon); });
            return;
        }
        var st = GetState(mon, build: false);
        bool any = false;
        foreach (var set in st.Strip)
        {
            if (!strip.HasSlot(set)) continue;
            var slot = strip.SlotOnScreen(set, flat: true);
            foreach (var h in set.Windows.Take(4))
            {
                if (set.Snapshots.GetValueOrDefault(h) != null || !User32.IsWindow(h) || !_snapshotTried.Add(h)) continue;
                var bmp = GrabViaThumbnail(h, slot);
                if (bmp != null) { set.Snapshots[h] = bmp; any = true; }
            }
        }
        if (any) RenderStrip(mon);
    }

    private static BitmapSource? GrabViaThumbnail(IntPtr h, RECT box)
    {
        RECT src;
        if (User32.IsIconic(h))
        {
            var wp = new WINDOWPLACEMENT { length = System.Runtime.InteropServices.Marshal.SizeOf<WINDOWPLACEMENT>() };
            User32.GetWindowPlacement(h, ref wp);
            src = wp.rcNormalPosition;
        }
        else src = Dwm.GetFrameBounds(h);
        var rect = src.Width > 0 && src.Height > 0 ? Anim.Fit(box, src.Width, src.Height) : box;
        var g = Flyer.Add(h, rect);
        if (g == null) return null;
        try
        {
            DwmThumb.DwmFlush(); DwmThumb.DwmFlush();
            return WindowCapture.CaptureScreen(rect);
        }
        finally { Flyer.Remove(g); }
    }

    private void EvaluateStrips()
    {
        if (!_enabled) { foreach (var s in _strips.Values) s.SetVisible(false); return; }
        var fg = User32.GetForegroundWindow();
        var fgInfo = WindowEnumerator.Describe(fg);
        foreach (var (monHandle, strip) in _strips)
        {
            var mon = Monitors.Get(monHandle); if (mon == null) { strip.SetVisible(false); continue; }
            var st = GetState(mon, build: false);
            // A window dragged towards the strip keeps it visible (and reveals it) so it can be dropped there.
            bool dragNear = _dragHwnd != IntPtr.Zero && _dragIsMove && _dragNearMon == monHandle;
            bool visible = !_overlayOpen && (st.Strip.Count > 0 || dragNear);
            if (visible && !App.Settings.Current.ShowRecentApps && !_hoverReveal.Contains(monHandle) && !dragNear) visible = false;
            if (visible && fgInfo != null && fgInfo.Monitor == monHandle && fgInfo.IsFullscreen) visible = false;
            if (visible && !_hoverReveal.Contains(monHandle) && !dragNear && st.Stage != null)
            {
                var sr = strip.ScreenRect;
                foreach (var h in st.Stage.Windows)
                {
                    if (!User32.IsWindow(h) || User32.IsIconic(h)) continue;
                    var r = Dwm.GetFrameBounds(h);
                    bool overlap = r.Left < sr.Right && r.Right > sr.Left && r.Top < sr.Bottom && r.Bottom > sr.Top;
                    if (overlap) { visible = false; break; }
                }
            }
            strip.SetVisible(visible);
            // The thumbnails face the stage; they turn to face the user while a window is dragged towards them.
            strip.SetFlat(dragNear);
        }
    }

    /// <summary>Keeps a recent picture of the stage windows so a user-minimized window still gets a real strip thumbnail.</summary>
    private void SnapshotStage()
    {
        if (!_enabled || _overlayOpen) return;
        var hwnds = new List<IntPtr>();
        foreach (var ((space, _), st) in _states)
            if (space == _vd.CurrentId && st.Stage != null) hwnds.AddRange(st.Stage.Windows);
        if (_dragHwnd == IntPtr.Zero)
            foreach (var h in hwnds) if (User32.IsWindow(h) && !User32.IsIconic(h)) _restFrames[h] = Dwm.GetFrameBounds(h);
        if (hwnds.Count == 0) return;
        Worker.Post(() => { foreach (var h in hwnds) if (User32.IsWindow(h) && !User32.IsIconic(h)) WindowCapture.Capture(h); });
    }

    // ================================================================ event handlers

    private void OnForeground(IntPtr hwnd)
    {
        if (!_enabled || _overlayOpen) return;
        if (Environment.TickCount64 < _suppressForegroundUntil) return;
        var wi = WindowEnumerator.Describe(hwnd, includeOtherDesktops: true);
        if (wi == null || (wi.IsCloaked && !Cloak.Tracks(hwnd))) { EvaluateStrips(); return; }
        if (!OnCurrentSpace(hwnd)) return;   // it lives on another space: handled after the switch to that space
        Log.Debug($"foreground {hwnd:X} '{wi.Title}' hidden={wi.IsCloaked}");
        var st = StateFor(hwnd, out var mon); if (st == null || mon == null) return;
        if (st.Stage != null && st.Stage.Contains(hwnd)) { st.Stage.MoveToFront(hwnd); st.Stage.Info[hwnd] = wi; EvaluateStrips(); return; }
        var set = st.Strip.FirstOrDefault(s => s.Contains(hwnd));
        if (set != null) { set.MoveToFront(hwnd); BringToStage(mon, set); return; }
        HandleNewWindow(wi);
    }

    /// <summary>
    /// The shell reveals a hidden window the instant the user activates it (Alt+Tab, taskbar click). By the time the
    /// foreground notification arrives it is already sitting in place, which would leave only the "old set leaves"
    /// half of the transition. So the window is hidden again right away and brought in with the normal fly-in, in
    /// step with the set that is leaving.
    /// </summary>
    private void OnShellUncloaked(IntPtr hwnd)
    {
        if (!_enabled || _overlayOpen || !Cloak.Tracks(hwnd)) return;   // our own reveals are untracked before they happen
        if (!User32.IsWindow(hwnd) || Dwm.GetCloaked(hwnd) != 0 || !OnCurrentSpace(hwnd)) return;
        var mon = Monitors.FromWindow(hwnd); if (mon == null) return;
        var st = GetState(mon, build: false);
        var set = st.Strip.FirstOrDefault(x => x.Contains(hwnd));
        if (set == null) return;
        if (!Cloak.Hide(hwnd)) return;
        Log.Debug($"window {hwnd:X} was activated from the strip: animating it in");
        set.MoveToFront(hwnd);
        BringToStage(mon, set);
    }

    private void OnWindowShown(IntPtr hwnd)
    {
        if (!_enabled) return;
        var wi = WindowEnumerator.Describe(hwnd);
        if (wi == null) { Delay(250, () => { var w2 = WindowEnumerator.Describe(hwnd); if (w2 != null && _enabled) HandleNewWindow(w2); }); return; }
        HandleNewWindow(wi);
    }

    private void OnWindowGone(IntPtr hwnd)
    {
        if (!_enabled) return;
        if (User32.IsWindow(hwnd) && User32.IsWindowVisible(hwnd)) return; // transient hide
        foreach (var ((space, monHandle), st) in _states.ToList())
        {
            bool changed = false;
            foreach (var set in st.AllSets.ToList())
            {
                if (!set.Contains(hwnd)) continue;
                set.Remove(hwnd); changed = true;
                if (set.IsEmpty) { st.Strip.Remove(set); if (st.Stage == set) st.Stage = null; }
            }
            if (changed && space == _vd.CurrentId && Monitors.Get(monHandle) is { } mon) RenderStrip(mon);
        }
        _ourMinimize.Remove(hwnd); _ourRestore.Remove(hwnd); _unmanageable.Remove(hwnd); _snapshotTried.Remove(hwnd);
    }

    private void OnMinimizeStart(IntPtr hwnd)
    {
        if (_ourMinimize.Remove(hwnd) || !_enabled) return;
        var st = StateFor(hwnd, out var mon); if (st == null || mon == null) return;
        if (st.Stage == null || !st.Stage.Contains(hwnd)) return;
        // User minimized (Cmd+M equivalent): the window goes into the strip instead of the taskbar.
        var wi = st.Stage.Info[hwnd];
        var rect = (WindowEnumerator.Describe(hwnd) ?? wi).Bounds;
        var crop = st.Stage.Crops.GetValueOrDefault(hwnd);
        st.Stage.Remove(hwnd); if (st.Stage.IsEmpty) st.Stage = null;
        var target = StripSetFor(st, wi);
        target.Add(wi, front: true);
        target.Frames[hwnd] = rect; target.Crops[hwnd] = crop;
        var bmp = WindowCapture.Cached(hwnd);
        if (bmp != null) target.Snapshots[hwnd] = bmp;
        var g = Flyer.Add(hwnd, rect, 255, crop);
        if (g != null) _pendingParks.Add((target, mon, new List<Flyer.Ghost> { g }));
        RenderStrip(mon);
    }

    private void OnMinimizeEnd(IntPtr hwnd)
    {
        if (_ourRestore.Remove(hwnd) || !_enabled) return;
        // Restored by the user (taskbar) – the foreground event brings its set to the stage.
    }

    /// <summary>
    /// A stage window may be starting to move. Mouse hooks never see input aimed at elevated windows (Task Manager,
    /// apps run as administrator), and apps with custom title bars raise no move-loop events; window location events and
    /// the pointer position work for all of them, so the drag is recognised from those.
    /// </summary>
    private void BeginDragWatch(IntPtr hwnd)
    {
        if (!_enabled || _overlayOpen || _dragHwnd != IntPtr.Zero || hwnd == IntPtr.Zero || _thumbDrag != null) return;
        if (Environment.TickCount64 < _suppressForegroundUntil) return;   // our own swap animation is moving windows
        var mon = Monitors.FromWindow(hwnd); if (mon == null) return;
        var st = GetState(mon, build: false);
        if (st.Stage == null || !st.Stage.Contains(hwnd) || User32.IsIconic(hwnd) || !OnCurrentSpace(hwnd)) return;
        var frame = Dwm.GetFrameBounds(hwnd);
        User32.GetCursorPos(out var cp);
        if (!Inflate(frame, (int)(80 * mon.Scale)).Contains(cp.X, cp.Y)) return;   // moved by a program, not by the pointer
        _dragHwnd = hwnd; _dragIsMove = false; _dragRigidTicks = 0; _dragStillTicks = 0; _dragNearMon = IntPtr.Zero;
        _dragButtonSeen = User32.IsKeyDown(User32.VK_LBUTTON);
        _dragPreFrame = _restFrames.TryGetValue(hwnd, out var rest) && rest.Width == frame.Width && rest.Height == frame.Height ? rest : frame;
        _dragLastFrame = _dragMoveFrame = _dragStartFrame = frame;
        // Other windows of the same app on the stage: if they travel along, they belong to the dragged window.
        _dragPeers.Clear();
        User32.GetWindowThreadProcessId(hwnd, out uint pid);
        foreach (var o in st.Stage.Windows)
        {
            if (o == hwnd || !User32.IsWindow(o) || User32.IsIconic(o)) continue;
            User32.GetWindowThreadProcessId(o, out uint opid);
            if (opid == pid || SameOwnerTree(o, hwnd)) _dragPeers[o] = Dwm.GetFrameBounds(o);
        }
        _dragTimer.Start();
    }

    private static RECT Inflate(RECT r, int d) => new(r.Left - d, r.Top - d, r.Right + d, r.Bottom + d);

    private static bool SameOwnerTree(IntPtr a, IntPtr b)
    {
        var ra = User32.GetAncestor(a, User32.GA_ROOTOWNER); var rb = User32.GetAncestor(b, User32.GA_ROOTOWNER);
        return ra != IntPtr.Zero && (ra == rb || ra == b || rb == a);
    }

    /// <summary>Follows a dragged stage window: reveals the strip when it comes near, opens a slot when it is over it,
    /// and keeps the pointer off the screen edge so Windows' own snap layout is not triggered.</summary>
    private void DragTick()
    {
        if (_dragHwnd == IntPtr.Zero || !User32.IsWindow(_dragHwnd) || !_enabled) { CancelDragWatch(); return; }
        var frame = Dwm.GetFrameBounds(_dragHwnd);
        User32.GetCursorPos(out var cp);
        bool buttonDown = User32.IsKeyDown(User32.VK_LBUTTON);
        if (buttonDown) _dragButtonSeen = true;
        // A move changes the position but never the size; every kind of resize changes the size.
        bool sameSize = Math.Abs(frame.Width - _dragLastFrame.Width) <= 2 && Math.Abs(frame.Height - _dragLastFrame.Height) <= 2;
        bool moved = frame.Left != _dragLastFrame.Left || frame.Top != _dragLastFrame.Top;
        bool pointerOnWindow = Inflate(frame, 120).Contains(cp.X, cp.Y);
        if (moved && sameSize && pointerOnWindow)
        {
            _dragStillTicks = 0;
            if (++_dragRigidTicks >= 2 && !_dragIsMove) { _dragIsMove = true; Log.Debug($"drag: window {_dragHwnd:X} is being moved"); }
        }
        else if (!sameSize) { _dragRigidTicks = 0; _dragIsMove = false; _dragStillTicks = 0; }   // resizing, or an un-snap at the start of a move
        else _dragStillTicks++;
        if (sameSize && _dragIsMove) _dragMoveFrame = frame;
        _dragLastFrame = frame;

        // The drag is over when the button is released. For elevated windows the button state cannot be read, so a
        // window that has come to rest counts as released.
        if (_dragButtonSeen && !buttonDown) { EndDragWatch(); return; }
        if (!_dragButtonSeen && _dragStillTicks * 30 > (_dragIsMove ? 360 : 700)) { EndDragWatch(); return; }
        if (!_dragIsMove) return;

        var mon = Monitors.FromPoint(cp.X, cp.Y);
        StripWindow? strip = null;
        if (mon != null) _strips.TryGetValue(mon.Handle, out strip);
        // Only the pointer counts: a window that merely comes close to (or covers) the strip makes the strip get out
        // of the way; the strip comes back as a drop target when the pointer itself is over the strip area.
        bool inZone = strip != null && strip.ScreenRect.Contains(cp.X, cp.Y);
        bool near = inZone;
        var nearMon = near && mon != null ? mon.Handle : IntPtr.Zero;
        if (nearMon != _dragNearMon) { _dragNearMon = nearMon; EvaluateStrips(); }
        var hint = inZone ? strip : null;
        if (hint != _dragHintStrip)
        {
            _dragHintStrip?.SetDropHint(false);
            if (hint != null && mon != null)
            {
                // If the window's app already has an item in the strip, the window will join it: highlight that item
                // instead of opening a slot that would stay empty.
                var st = GetState(mon, build: false);
                var info = st.Stage?.Info.GetValueOrDefault(_dragHwnd);
                bool allAtOnce = App.Settings.Current.ShowWindowsFromApplication == ShowWindowsMode.AllAtOnce;
                var existing = info != null && allAtOnce ? st.FindStripSetForApp(info.AppKey) : null;
                if (existing != null && hint.HasSlot(existing)) hint.SetDropTarget(existing);
                else hint.SetDropHint(true);
            }
            _dragHintStrip = hint;
        }

    }

    private void ReleaseClip() { if (_dragClip) { User32.ClipCursor(IntPtr.Zero); _dragClip = false; } }

    private void CancelDragWatch()
    {
        _dragTimer.Stop();
        ReleaseClip();
        _dragHintStrip?.SetDropHint(false);
        bool wasNear = _dragNearMon != IntPtr.Zero;
        _dragHwnd = IntPtr.Zero; _dragIsMove = false; _dragNearMon = IntPtr.Zero; _dragHintStrip = null;
        if (wasNear) EvaluateStrips();
    }

    /// <summary>The drag ended: drop the window into the strip if that is where the pointer is.</summary>
    private void EndDragWatch()
    {
        var hwnd = _dragHwnd;
        if (hwnd == IntPtr.Zero) return;
        bool wasMove = _dragIsMove;
        var preFrame = _dragPreFrame; var moveFrame = _dragMoveFrame; var hintStrip = _dragHintStrip;
        _dragTimer.Stop();
        ReleaseClip();
        _dragHwnd = IntPtr.Zero; _dragIsMove = false; _dragNearMon = IntPtr.Zero; _dragHintStrip = null;
        User32.GetCursorPos(out var cp);
        int mx = cp.X, my = cp.Y;
        // Companions: windows of the same app that moved by the same amount (the app keeps them attached), or that
        // are tied to the dragged window by ownership. They are stowed, and later brought back, together with it.
        var group = new List<IntPtr> { hwnd };
        var peerStart = new Dictionary<IntPtr, RECT>();
        if (User32.IsWindow(hwnd))
        {
            var cur = Dwm.GetFrameBounds(hwnd);
            int ddx = cur.Left - _dragStartFrame.Left, ddy = cur.Top - _dragStartFrame.Top;
            foreach (var (o, start) in _dragPeers)
            {
                if (!User32.IsWindow(o)) continue;
                var oc = Dwm.GetFrameBounds(o);
                bool travelled = Math.Abs(ddx) + Math.Abs(ddy) > 12 && Math.Abs((oc.Left - start.Left) - ddx) <= 10 && Math.Abs((oc.Top - start.Top) - ddy) <= 10;
                if (travelled || SameOwnerTree(o, hwnd)) { group.Add(o); peerStart[o] = start; }
            }
        }
        _dragPeers.Clear();

        var cursorMon = Monitors.FromPoint(mx, my);
        StripWindow? dropStrip = null;
        if (cursorMon != null) _strips.TryGetValue(cursorMon.Handle, out dropStrip);
        bool drop = wasMove && _enabled && cursorMon != null && dropStrip != null && dropStrip.ScreenRect.Contains(mx, my);
        Log.Debug($"drag end {hwnd:X}: move={wasMove} drop={drop}");
        if (!drop) { hintStrip?.SetDropHint(false); if (User32.IsWindow(hwnd) && !User32.IsIconic(hwnd)) _restFrames[hwnd] = Dwm.GetFrameBounds(hwnd); EvaluateStrips(); return; }

        // Give Windows a moment to finish its own move loop, then stow the window.
        Anim.After(70, () => Safe(() =>
        {
            var st = GetState(cursorMon!, build: false);
            if (!_enabled || !User32.IsWindow(hwnd) || st.Stage == null || !st.Stage.Contains(hwnd)) { hintStrip?.SetDropHint(false); EvaluateStrips(); return; }
            var now = Dwm.GetFrameBounds(hwnd);
            if (User32.IsZoomed(hwnd)) User32.ShowWindow(hwnd, User32.SW_SHOWNOACTIVATE);
            if (Math.Abs(now.Width - moveFrame.Width) > 8 || Math.Abs(now.Height - moveFrame.Height) > 8)
            {
                // The system snapped it after all: give the window its own size back.
                User32.GetWindowRect(hwnd, out var wr);
                int padL = now.Left - wr.Left, padT = now.Top - wr.Top, padW = wr.Width - now.Width, padH = wr.Height - now.Height;
                int x = Math.Max(cursorMon!.Bounds.Left, mx - moveFrame.Width / 4), y = Math.Max(cursorMon.WorkArea.Top, my - 20);
                User32.SetWindowPos(hwnd, IntPtr.Zero, x - padL, y - padT, moveFrame.Width + padW, moveFrame.Height + padH, User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);
            }
            hintStrip?.SetDropHint(false, relayout: false);   // the real item takes the reserved slot
            Log.Debug($"window {hwnd:X} dropped on the strip");
            if (group.Count > 1) Log.Debug($"stowing {group.Count} windows that move together");
            SendWindowsToStrip(st, cursorMon!, group, animate: true);
            // When it comes back to the stage it returns to where it was before the drag, not to the screen edge.
            var parked = st.Strip.FirstOrDefault(x => x.Contains(hwnd));
            if (parked != null)
            {
                foreach (var (o, start) in peerStart) if (parked.Contains(o)) parked.Frames[o] = start;
                bool sameSizeAsBefore = Math.Abs(preFrame.Width - moveFrame.Width) <= 8 && Math.Abs(preFrame.Height - moveFrame.Height) <= 8;
                if (sameSizeAsBefore) parked.Frames[hwnd] = preFrame;
                else
                {
                    var wa = cursorMon!.WorkArea;
                    int cx = wa.Left + (wa.Width - moveFrame.Width) / 2, cy = wa.Top + (wa.Height - moveFrame.Height) / 2;
                    parked.Frames[hwnd] = new RECT(cx, cy, cx + moveFrame.Width, cy + moveFrame.Height);
                }
            }
        }));
    }

    private void OnMoveSizeEnd(IntPtr hwnd)
    {
        if (!_enabled) return;
        if (_dragHwnd == hwnd) EndDragWatch();
        var wi = WindowEnumerator.Describe(hwnd); if (wi == null) return;
        var newMon = Monitors.Get(wi.Monitor); if (newMon == null) return;
        StageState? owner = null; MonitorInfo? ownerMon = null;
        foreach (var ((space, monHandle), st) in _states)
        {
            if (space != _vd.CurrentId) continue;
            if (st.FindSet(hwnd) != null) { owner = st; ownerMon = Monitors.Get(monHandle); break; }
        }
        if (owner == null || ownerMon == null) { HandleNewWindow(wi); return; }
        if (ownerMon.Handle != newMon.Handle)
        {
            // Moved to another display: leave the old stage, join the new display's stage.
            var set = owner.FindSet(hwnd)!; set.Remove(hwnd);
            if (set.IsEmpty) { owner.Strip.Remove(set); if (owner.Stage == set) owner.Stage = null; }
            RenderStrip(ownerMon);
            HandleNewWindow(wi);
            return;
        }
        EvaluateStrips();
    }

    private void OnSpaceChanged()
    {
        if (!_enabled) return;
        CancelDragWatch(); CancelThumbDrag();
        _pendingParks.RemoveAll(p => { foreach (var g in p.ghosts) Flyer.Remove(g); return true; });
        foreach (var mon in Monitors.All()) { GetState(mon, build: true); RenderStrip(mon); }
        // Whatever is active on the space we arrived at takes its stage.
        Delay(200, () => OnForeground(User32.GetForegroundWindow()));
        // The desktop switch made the shell recompute its own cloak flag: put ours back on the windows in the strip.
        Anim.After(250, Cloak.Reinforce);
    }

    private void OnWallpaperClicked(MonitorInfo mon)
    {
        var s = App.Settings.Current;
        if (_enabled)
        {
            var st = GetState(mon, build: true);
            if (st.Stage != null) RevealDesktop(st, mon);
        }
        else if (s.ShowDesktopOnWallpaperClick == ShowDesktopClick.Always)
        {
            App.ShowDesktop.Toggle();
        }
    }

    /// <summary>Called by Mission Control before activating a window so its set comes to the stage.</summary>
    public void RequestActivate(IntPtr hwnd, bool animate = true)
    {
        if (!_enabled) return;
        var st = StateFor(hwnd, out var mon); if (st == null || mon == null) return;
        var set = st.Strip.FirstOrDefault(s => s.Contains(hwnd));
        if (set != null) { set.MoveToFront(hwnd); BringToStage(mon, set, null, animate); }
    }

    // ---- spring-loading: hold a drag over a thumbnail to bring its windows forward ----
    private void SpringTick()
    {
        if (!_enabled || !_mouse.LeftButtonDown || _pressStartedInStrip || _tracker.DraggingWindow != IntPtr.Zero) { _springSet = null; return; }
        WindowSet? over = null; MonitorInfo? mon = null;
        foreach (var strip in _strips.Values)
        {
            over = strip.SetAtScreenPoint(_mouse.X, _mouse.Y);
            if (over != null) { mon = strip.Monitor; break; }
        }
        if (over == null) { _springSet = null; return; }
        if (_springSet != over) { _springSet = over; _springSince = Environment.TickCount64; return; }
        if (Environment.TickCount64 - _springSince > 700 && mon != null)
        {
            _springSet = null;
            BringToStage(mon, over);
        }
    }

    // ---- Cmd+` : cycle windows of the current app ----
    private void CycleAppWindows()
    {
        var fg = User32.GetForegroundWindow();
        var wi = WindowEnumerator.Describe(fg); if (wi == null) return;
        var same = WindowEnumerator.GetWindows(includeMinimized: true, includeOtherDesktops: true)
            .Where(w => w.AppKey == wi.AppKey && (!w.IsCloaked || Cloak.Tracks(w.Hwnd))).ToList();
        if (same.Count < 2) return;
        int idx = same.FindIndex(w => w.Hwnd == fg);
        var next = same[(idx + 1) % same.Count];
        RequestActivate(next.Hwnd);
        App.Activate(next.Hwnd);
    }

    public IReadOnlyDictionary<IntPtr, StripWindow> Strips => _strips;
}

/// <summary>
/// Windows' "drag a window to a screen edge to snap it" setting. In Stage Manager the left edge belongs to the strip,
/// and the system only reads this setting when a drag starts, so it is switched off for the whole session and restored
/// afterwards (a marker file makes sure it is restored even after a crash).
/// </summary>
public static class EdgeSnap
{
    private static readonly string Marker = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WacOS", "edge-snap-suspended");
    private static bool _suspended;

    public static void Suspend(bool suspend)
    {
        if (suspend == _suspended) return;
        try
        {
            if (suspend)
            {
                int on = 0;
                User32.SystemParametersInfo(User32.SPI_GETDOCKMOVING, 0, ref on, 0);
                if (on == 0) return;   // already off by the user's own choice: nothing to restore later
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Marker)!);
                System.IO.File.WriteAllText(Marker, "1");
                User32.SystemParametersInfo(User32.SPI_SETDOCKMOVING, 0, IntPtr.Zero, 0);
                _suspended = true;
            }
            else
            {
                User32.SystemParametersInfo(User32.SPI_SETDOCKMOVING, 1, IntPtr.Zero, 0);
                _suspended = false;
                if (System.IO.File.Exists(Marker)) System.IO.File.Delete(Marker);
            }
        }
        catch (Exception ex) { Log.Warn("edge snap: " + ex.Message); }
    }

    /// <summary>Restores the setting if a previous session ended without doing so.</summary>
    public static void RecoverFromCrash()
    {
        try
        {
            if (!System.IO.File.Exists(Marker)) return;
            User32.SystemParametersInfo(User32.SPI_SETDOCKMOVING, 1, IntPtr.Zero, 0);
            System.IO.File.Delete(Marker);
        }
        catch { }
    }
}

public static class DesktopIcons
{
    public static void SetVisible(bool visible)
    {
        var lv = WindowEnumerator.DesktopListView();
        if (lv == IntPtr.Zero) return;
        bool isVisible = User32.IsWindowVisible(lv);
        if (isVisible == visible) return;
        User32.ShowWindow(lv, visible ? User32.SW_SHOW : User32.SW_HIDE);
    }
}
