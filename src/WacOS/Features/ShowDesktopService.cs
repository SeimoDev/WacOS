using WacOS.Desktop;
using WacOS.Native;

namespace WacOS.Features;

/// <summary>
/// macOS "Show Desktop": windows slide off the screen edges; the same command slides them back.
/// Live ghosts of the windows do the sliding while the real windows are hidden (shell-cloaked, so they keep rendering
/// and come back without repainting); activating any app ends the mode and brings every window back, like macOS.
/// </summary>
public sealed class ShowDesktopService
{
    private sealed class Entry
    {
        public IntPtr Hwnd;
        public RECT Frame, Off;
        public RECT? Crop;
        public bool Minimized;   // fallback for windows the shell would not cloak
    }

    private bool _shown, _busy;
    private readonly List<Entry> _hidden = new();   // bottom-most first
    private IntPtr _prevForeground;

    public bool IsShown => _shown;

    public void Toggle()
    {
        if (App.StageManager.IsEnabled) { App.StageManager.ToggleDesktop(); return; }
        if (_busy) return;
        if (_shown) Restore(); else Show();
    }

    public void Show()
    {
        if (_shown || _busy) return;
        var windows = WindowEnumerator.GetWindows(includeMinimized: false);
        _hidden.Clear();
        _shown = true;
        if (windows.Count == 0) return;
        _prevForeground = User32.GetForegroundWindow();
        _busy = true;

        // 1) A live ghost exactly on top of every window (bottom-most first so the stacking is preserved).
        var made = new List<(Entry e, Flyer.Ghost? g)>();
        foreach (var w in Enumerable.Reverse(windows))
        {
            var e = new Entry { Hwnd = w.Hwnd, Frame = w.Bounds, Off = OffscreenRect(w) };
            var g = Flyer.Add(w.Hwnd, e.Frame);
            e.Crop = g?.Thumb.Crop ?? Thumb.ComputeCrop(w.Hwnd);
            made.Add((e, g));
        }
        DwmThumb.DwmFlush();

        // 2) Hide the real windows underneath the ghosts.
        var ghosts = new List<Flyer.Ghost>();
        var toMinimize = new List<(Entry e, Flyer.Ghost? g)>();
        foreach (var (e, g) in made)
        {
            if (Cloak.Hide(e.Hwnd)) { _hidden.Add(e); if (g != null) ghosts.Add(g); }
            else toMinimize.Add((e, g));
        }
        if (toMinimize.Count > 0)
            SystemMinimizeAnimation.Suppressed(() =>
            {
                foreach (var (e, g) in Enumerable.Reverse(toMinimize))
                {
                    User32.ShowWindow(e.Hwnd, User32.SW_SHOWMINNOACTIVE);
                    if (User32.IsIconic(e.Hwnd)) { e.Minimized = true; _hidden.Insert(0, e); if (g != null) ghosts.Add(g); }
                    else Flyer.Remove(g); // a window we cannot control: leave it alone
                }
            });
        // Keyboard focus must not stay in an invisible window.
        User32.ForceForeground(User32.GetShellWindow());

        // 3) The ghosts accelerate off screen; the ones nearest to an edge leave first.
        foreach (var (e, g) in made) if (g != null) g.To = e.Off;
        Stagger(ghosts, g => Distance(g.From, g.To), 70);
        Flyer.Animate(ghosts, 380, Easing.Depart, () => _busy = false, opacityEase: Easing.Linear);
    }

    public void Restore()
    {
        if (!_shown || _busy) return;
        _shown = false;
        var list = _hidden.Where(x => User32.IsWindow(x.Hwnd) && (x.Minimized ? User32.IsIconic(x.Hwnd) : Cloak.IsHidden(x.Hwnd))).ToList();
        // Windows that were revealed by the shell meanwhile are simply no longer ours to restore.
        foreach (var e in _hidden.Where(x => !x.Minimized)) if (!list.Contains(e)) Cloak.Show(e.Hwnd);
        _hidden.Clear();
        if (list.Count == 0) return;
        _busy = true;
        var ghosts = new List<Flyer.Ghost>();
        foreach (var e in list)
        {
            var g = Flyer.Add(e.Hwnd, e.Off, 255, e.Minimized ? e.Crop : null);
            if (g == null) continue;
            g.To = e.Frame;
            ghosts.Add(g);
        }
        Stagger(ghosts, g => -Distance(g.From, g.To), 60);
        var prev = _prevForeground;
        Flyer.Animate(ghosts, 520, Easing.Soft, () =>
        {
            // Reveal the real windows underneath the ghosts (bottom-most first); hidden windows need no repaint.
            foreach (var e in list.Where(x => !x.Minimized)) Cloak.Show(e.Hwnd);
            var minimized = list.Where(x => x.Minimized).ToList();
            if (minimized.Count > 0)
                SystemMinimizeAnimation.Suppressed(() =>
                {
                    foreach (var e in minimized)
                    {
                        User32.ShowWindow(e.Hwnd, User32.SW_SHOWNOACTIVATE);
                        User32.SetWindowPos(e.Hwnd, User32.HWND_TOP, 0, 0, 0, 0, User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
                    }
                });
            var fg = User32.GetForegroundWindow();
            if (prev != IntPtr.Zero && User32.IsWindow(prev) && (fg == User32.GetShellWindow() || WindowEnumerator.IsDesktopWindow(fg))) App.Activate(prev);
            _busy = false;
        }, holdMs: 60);
    }

    /// <summary>Called when the user activates a window while the desktop is shown – like macOS, everything comes back.</summary>
    public void OnUserActivity()
    {
        if (_shown && !_busy) { _prevForeground = IntPtr.Zero; Restore(); }
    }

    private static double Distance(RECT a, RECT b) => Math.Abs(a.Left - b.Left) + Math.Abs(a.Top - b.Top);

    /// <summary>Spreads start delays over the ghosts, ordered by the given key (smallest first).</summary>
    private static void Stagger(List<Flyer.Ghost> ghosts, Func<Flyer.Ghost, double> key, double totalMs)
    {
        if (ghosts.Count < 2) return;
        var ordered = ghosts.OrderBy(key).ToList();
        for (int i = 0; i < ordered.Count; i++) ordered[i].DelayMs = totalMs * i / (ordered.Count - 1);
    }

    /// <summary>Where a window ends up: just beyond the nearest edge of its monitor that has no other display behind it.</summary>
    private static RECT OffscreenRect(WindowInfo w)
    {
        var mon = Monitors.Get(w.Monitor);
        var r = w.Bounds;
        if (mon == null) return new RECT(r.Left, r.Top - 4000, r.Right, r.Bottom - 4000);
        var m = mon.Bounds;
        int cx = (r.Left + r.Right) / 2, cy = (r.Top + r.Bottom) / 2;
        const int pad = 40;
        var others = Monitors.All().Where(o => o.Handle != mon.Handle).Select(o => o.Bounds).ToList();
        bool Free(int x0, int y0, int x1, int y1) => !others.Any(o => o.Left < x1 && o.Right > x0 && o.Top < y1 && o.Bottom > y0);
        var candidates = new List<(int dist, int dx, int dy)>();
        if (Free(m.Left - 50, m.Top, m.Left, m.Bottom)) candidates.Add((cx - m.Left, m.Left - r.Right - pad, 0));
        if (Free(m.Right, m.Top, m.Right + 50, m.Bottom)) candidates.Add((m.Right - cx, m.Right - r.Left + pad, 0));
        if (Free(m.Left, m.Top - 50, m.Right, m.Top)) candidates.Add((cy - m.Top, 0, m.Top - r.Bottom - pad));
        if (Free(m.Left, m.Bottom, m.Right, m.Bottom + 50)) candidates.Add((m.Bottom - cy, 0, m.Bottom - r.Top + pad));
        if (candidates.Count == 0) candidates.Add((0, 0, m.Bottom - r.Top + pad));
        var best = candidates.OrderBy(c => c.dist).First();
        return new RECT(r.Left + best.dx, r.Top + best.dy, r.Right + best.dx, r.Bottom + best.dy);
    }
}
