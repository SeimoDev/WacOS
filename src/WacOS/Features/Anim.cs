using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows.Media;
using WacOS.Native;

namespace WacOS.Features;

public static class Easing
{
    public static double Linear(double t) => t;
    public static double OutCubic(double t) => 1 - Math.Pow(1 - t, 3);
    public static double OutQuart(double t) => 1 - Math.Pow(1 - t, 4);
    public static double OutQuint(double t) => 1 - Math.Pow(1 - t, 5);
    public static double InCubic(double t) => t * t * t;
    public static double InOutCubic(double t) => t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;

    /// <summary>CSS-style cubic Bézier timing function through (0,0) (x1,y1) (x2,y2) (1,1).</summary>
    public static Func<double, double> Bezier(double x1, double y1, double x2, double y2)
    {
        double cx = 3 * x1, bx = 3 * (x2 - x1) - cx, ax = 1 - cx - bx;
        double cy = 3 * y1, by = 3 * (y2 - y1) - cy, ay = 1 - cy - by;
        double X(double u) => ((ax * u + bx) * u + cx) * u;
        double Y(double u) => ((ay * u + by) * u + cy) * u;
        double DX(double u) => (3 * ax * u + 2 * bx) * u + cx;
        return t =>
        {
            if (t <= 0) return 0;
            if (t >= 1) return 1;
            double u = t;
            for (int i = 0; i < 8; i++)
            {
                double err = X(u) - t;
                if (Math.Abs(err) < 1e-6) return Y(u);
                double d = DX(u);
                if (Math.Abs(d) < 1e-6) break;
                u -= err / d;
            }
            double lo = 0, hi = 1; u = t;
            for (int i = 0; i < 24; i++)
            {
                double x = X(u);
                if (Math.Abs(x - t) < 1e-6) break;
                if (x < t) lo = u; else hi = u;
                u = (lo + hi) / 2;
            }
            return Y(u);
        };
    }

    /// <summary>
    /// A real damped spring (damping ratio zeta &lt; 1), normalised to settle at t = 1. Lower zeta = more bounce:
    /// 0.86 barely overshoots (~0.5 %), 0.72 overshoots ~4 % like a macOS window settling into place.
    /// </summary>
    public static Func<double, double> SpringOf(double zeta)
    {
        zeta = Math.Clamp(zeta, 0.2, 0.99);
        const double decay = 6.9;                        // envelope falls to 0.1 % at t = 1
        double wd = decay / zeta * Math.Sqrt(1 - zeta * zeta);
        double F(double t) => 1 - Math.Exp(-decay * t) * (Math.Cos(wd * t) + decay / wd * Math.Sin(wd * t));
        double end = F(1);
        return t => t <= 0 ? 0 : t >= 1 ? 1 : F(t) / end;
    }

    /// <summary>Long silky deceleration (expo-out): things flying out of the way into an overview.</summary>
    public static readonly Func<double, double> Glide = Bezier(0.16, 1, 0.3, 1);
    /// <summary>Quick start, gentle landing: things returning to where they belong.</summary>
    public static readonly Func<double, double> Settle = Bezier(0.22, 0.9, 0.24, 1);
    /// <summary>Balanced ease-in-out for fades and chrome.</summary>
    public static readonly Func<double, double> Standard = Bezier(0.4, 0, 0.2, 1);
    /// <summary>Accelerating exit: things leaving the screen.</summary>
    public static readonly Func<double, double> Depart = Bezier(0.5, 0, 0.85, 0.4);
    /// <summary>Stays at 0 for the first 40 % of the time, then rises smoothly to 1 by 70 % (for cross-fades late in a move).</summary>
    public static readonly Func<double, double> LateFade = t =>
    {
        double u = Math.Clamp((t - 0.40) / 0.30, 0, 1);
        return u * u * (3 - 2 * u);
    };
    /// <summary>Spring with a small, lively overshoot.</summary>
    public static readonly Func<double, double> Bouncy = SpringOf(0.72);
    /// <summary>Spring that lands softly with an almost imperceptible overshoot.</summary>
    public static readonly Func<double, double> Soft = SpringOf(0.86);

    /// <summary>macOS-like critically damped spring: fast start, long soft landing, no overshoot.</summary>
    public static double Spring(double t)
    {
        const double k = 9.0;
        double v = 1 - (1 + k * t) * Math.Exp(-k * t);
        double end = 1 - (1 + k) * Math.Exp(-k);
        return v / end;
    }
}

public sealed class AnimHandle
{
    internal volatile bool Cancelled;
    /// <summary>Another animation that belongs to this one (cancelled together).</summary>
    internal AnimHandle? Linked;
    public bool IsRunning { get; internal set; } = true;
    public void Cancel() { Cancelled = true; IsRunning = false; Linked?.Cancel(); }
}

/// <summary>Frame-synchronised animation driver (one callback per composed frame, time based).</summary>
public static class Anim
{
    private sealed class Entry
    {
        public AnimHandle Handle = new();
        public long StartTicks = -1;
        public double DurationMs;
        public Func<double, double> Ease = Easing.OutCubic;
        public Action<double> Tick = _ => { };
        public Action? Done;
        // frame pacing statistics (verbose logging only)
        public long LastTicks = -1; public int Frames, WorstAt, Slow; public double MaxGapMs, TickCostMs, ElapsedMs;
    }

    private const double MaxStepMs = 34;
    private static readonly List<Entry> _active = new();
    private static bool _hooked;

    public static AnimHandle Run(double durationMs, Func<double, double> ease, Action<double> tick, Action? done = null)
    {
        var e = new Entry { DurationMs = Math.Max(1, durationMs), Ease = ease, Tick = tick, Done = done };
        _active.Add(e);
        if (!_hooked) { CompositionTarget.Rendering += OnFrame; _hooked = true; }
        return e.Handle;
    }

    /// <summary>Runs an action after a delay without blocking the UI thread.</summary>
    public static void After(double ms, Action a) => Run(ms, Easing.Linear, _ => { }, a);

    private static void OnFrame(object? sender, EventArgs e)
    {
        long now = Stopwatch.GetTimestamp();
        foreach (var en in _active.ToArray())
        {
            if (en.Handle.Cancelled) { _active.Remove(en); continue; }
            // The clock starts on the first frame, and a long frame only advances it by MaxStepMs: a stall (another
            // process hogging the GPU, a first-time texture upload) pauses the motion instead of making it jump.
            if (en.StartTicks < 0) en.StartTicks = now;
            if (en.LastTicks >= 0)
            {
                double gap = (now - en.LastTicks) * 1000.0 / Stopwatch.Frequency;
                if (gap > en.MaxGapMs) { en.MaxGapMs = gap; en.WorstAt = en.Frames; }
                if (gap > 25) en.Slow++;
                en.ElapsedMs += Math.Min(gap, MaxStepMs);
            }
            en.LastTicks = now; en.Frames++;
            double t = en.ElapsedMs / en.DurationMs;
            bool finished = t >= 1;
            try { en.Tick(en.Ease(Math.Clamp(t, 0, 1))); } catch (Exception ex) { Log.Error("anim tick", ex); finished = true; }
            en.TickCostMs += (Stopwatch.GetTimestamp() - now) * 1000.0 / Stopwatch.Frequency;
            if (finished)
            {
                if (Log.Verbose && en.DurationMs >= 150 && en.Frames > 2)
                    Log.Debug($"anim {en.DurationMs:F0}ms: {en.Frames} frames, avg {en.DurationMs / (en.Frames - 1):F1}ms/frame, worst gap {en.MaxGapMs:F1}ms before frame #{en.WorstAt}, {en.Slow} slow frames, tick cost {en.TickCostMs / en.Frames:F2}ms");
                _active.Remove(en);
                en.Handle.IsRunning = false;
                try { en.Done?.Invoke(); } catch (Exception ex) { Log.Error("anim done", ex); }
            }
        }
        if (_active.Count == 0 && _hooked) { CompositionTarget.Rendering -= OnFrame; _hooked = false; }
    }

    public static RECT Lerp(RECT a, RECT b, double t) => new(
        (int)Math.Round(a.Left + (b.Left - a.Left) * t), (int)Math.Round(a.Top + (b.Top - a.Top) * t),
        (int)Math.Round(a.Right + (b.Right - a.Right) * t), (int)Math.Round(a.Bottom + (b.Bottom - a.Bottom) * t));

    public static byte Lerp(byte a, byte b, double t) => (byte)Math.Clamp(Math.Round(a + (b - a) * t), 0, 255);

    /// <summary>Largest rect with the aspect of (w,h) centred inside box.</summary>
    public static RECT Fit(RECT box, int w, int h)
    {
        if (w <= 0 || h <= 0 || box.Width <= 0 || box.Height <= 0) return box;
        double s = Math.Min(box.Width / (double)w, box.Height / (double)h);
        int fw = (int)Math.Round(w * s), fh = (int)Math.Round(h * s);
        int x = box.Left + (box.Width - fw) / 2, y = box.Top + (box.Height - fh) / 2;
        return new RECT(x, y, x + fw, y + fh);
    }
}

/// <summary>Flies live window thumbnails across the screen on an invisible, GPU-composited host window.</summary>
public static class Flyer
{
    public sealed class Ghost
    {
        public Thumb Thumb = null!;
        public RECT From, To;
        public byte OpacityFrom = 255, OpacityTo = 255;
        /// <summary>Start delay within the animation (stagger).</summary>
        public double DelayMs;
        internal bool Removed;
    }

    private static ThumbHost? _host;
    private static int _count;

    public static RECT VirtualScreen() => new(
        User32.GetSystemMetrics(User32.SM_XVIRTUALSCREEN), User32.GetSystemMetrics(User32.SM_YVIRTUALSCREEN),
        User32.GetSystemMetrics(User32.SM_XVIRTUALSCREEN) + User32.GetSystemMetrics(User32.SM_CXVIRTUALSCREEN),
        User32.GetSystemMetrics(User32.SM_YVIRTUALSCREEN) + User32.GetSystemMetrics(User32.SM_CYVIRTUALSCREEN));

    private static ThumbHost Host()
    {
        var vs = VirtualScreen();
        if (_host != null && _count == 0 && (_host.Bounds.Left != vs.Left || _host.Bounds.Top != vs.Top || _host.Bounds.Width != vs.Width || _host.Bounds.Height != vs.Height))
        { _host.Dispose(); _host = null; }
        return _host ??= new ThumbHost(vs);
    }

    /// <summary>Pre-creates the host so the first animation has no start-up cost.</summary>
    public static void Warmup() => Host();

    private static RECT ToHost(RECT screen)
    {
        var o = Host().Bounds;
        return new RECT(screen.Left - o.Left, screen.Top - o.Top, screen.Right - o.Left, screen.Bottom - o.Top);
    }

    /// <summary>Creates a ghost of a window at a screen rectangle. It stays put until animated or removed.</summary>
    public static Ghost? Add(IntPtr src, RECT screenRect, byte opacity = 255, RECT? crop = null)
    {
        var host = Host();
        var t = Thumb.Create(host.Handle, src);
        if (t == null) return null;
        if (crop != null) t.Crop = crop;
        t.Set(ToHost(screenRect), opacity);
        if (_count++ == 0) host.Show();
        return new Ghost { Thumb = t, From = screenRect, To = screenRect, OpacityFrom = opacity, OpacityTo = opacity };
    }

    /// <summary>Moves a ghost immediately (used while it follows the pointer).</summary>
    public static void Place(Ghost g, RECT screenRect, byte opacity = 255)
    {
        if (g.Removed) return;
        g.From = screenRect; g.OpacityFrom = opacity;
        g.Thumb.Set(ToHost(screenRect), opacity);
    }

    public static void Remove(Ghost? g)
    {
        if (g == null || g.Removed) return;
        g.Removed = true;
        g.Thumb.Dispose();
        if (--_count <= 0) { _count = 0; _host?.Hide(); }
    }

    /// <summary>Animates ghosts from their From to their To rectangle. Ghosts are removed holdMs after the end.</summary>
    public static AnimHandle Animate(IReadOnlyList<Ghost> ghosts, double ms, Func<double, double> ease, Action? done = null, double holdMs = 0,
        bool removeAtEnd = true, Func<double, double>? opacityEase = null)
    {
        var targets = new List<(Thumb, RECT, byte)>();
        var delays = new List<double>();
        foreach (var g in ghosts)
            if (!g.Removed) { targets.Add((g.Thumb, ToHost(g.To), g.OpacityTo)); delays.Add(g.DelayMs); }
        return ThumbAnimator.Run(targets, ms, ease, () =>
        {
            try { done?.Invoke(); } catch (Exception ex) { Log.Error("fly done", ex); }
            if (!removeAtEnd) return;
            if (holdMs <= 0) { foreach (var g in ghosts) Remove(g); }
            else Anim.After(holdMs, () => { foreach (var g in ghosts) Remove(g); });
        }, opacityEase, delays);
    }
}

/// <summary>Temporarily switches off the system minimize/restore animation (so our own animation is the only one).</summary>
public static class SystemMinimizeAnimation
{
    private static uint Size => (uint)System.Runtime.InteropServices.Marshal.SizeOf<ANIMATIONINFO>();

    public static bool Get()
    {
        var ai = new ANIMATIONINFO { cbSize = Size };
        User32.SystemParametersInfo(User32.SPI_GETANIMATION, ai.cbSize, ref ai, 0);
        return ai.iMinAnimate != 0;
    }

    public static void Set(bool on)
    {
        var ai = new ANIMATIONINFO { cbSize = Size, iMinAnimate = on ? 1 : 0 };
        User32.SystemParametersInfo(User32.SPI_SETANIMATION, ai.cbSize, ref ai, 0);
    }

    /// <summary>Runs an action with the animation disabled and restores the previous state.</summary>
    public static void Suppressed(Action a)
    {
        bool prev = Get();
        if (prev) Set(false);
        try { a(); } finally { if (prev) Set(true); }
    }
}

/// <summary>A background STA thread for slow work (window captures, thumbnail composition) that must not stall animations.</summary>
public static class Worker
{
    private static readonly BlockingCollection<Action> _queue = new();
    private static Thread? _thread;
    private static readonly object _lock = new();

    public static void Post(Action a)
    {
        lock (_lock)
        {
            if (_thread == null)
            {
                _thread = new Thread(() =>
                {
                    foreach (var job in _queue.GetConsumingEnumerable())
                    {
                        try { job(); } catch (Exception ex) { Log.Error("worker", ex); }
                    }
                }) { IsBackground = true, Name = "WacOS.Worker", Priority = ThreadPriority.BelowNormal };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
            }
        }
        _queue.Add(a);
    }
}
