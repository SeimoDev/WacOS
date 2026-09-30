using System.Diagnostics;
using WacOS.Native;

namespace WacOS.Features;

/// <summary>
/// Animates DWM thumbnails on a dedicated thread that is paced by the compositor itself (DwmFlush returns once per
/// composed frame). Motion therefore runs at the display refresh rate and is immune to stalls of the UI thread.
/// Each thumbnail can start with its own delay (stagger), and opacity can follow a different curve than geometry.
/// </summary>
public static class ThumbAnimator
{
    private sealed class Item
    {
        public Thumb Thumb = null!;
        public RECT From, To;
        public byte OpFrom, OpTo;
        public double DelayMs;
    }

    private sealed class Job
    {
        public List<Item> Items = new();
        public double DurationMs, TotalMs, ElapsedMs;
        public Func<double, double> Ease = Easing.Spring;
        public Func<double, double> OpacityEase = Easing.OutCubic;
        public Action? Done;
        public AnimHandle Handle = new();
        public long Last = -1;
        public int Frames, Slow, WorstAt;
        public double MaxGap;
    }

    private const double MaxStepMs = 34;
    private static readonly List<Job> _jobs = new();
    private static readonly AutoResetEvent _wake = new(false);
    private static Thread? _thread;

    /// <summary>Animates each thumbnail from where it is now to the given rectangle/opacity.</summary>
    public static AnimHandle Run(IReadOnlyList<(Thumb th, RECT to, byte op)> targets, double ms, Func<double, double> ease, Action? done = null,
        Func<double, double>? opacityEase = null, IReadOnlyList<double>? delaysMs = null)
    {
        var job = new Job { DurationMs = Math.Max(1, ms), Ease = ease, OpacityEase = opacityEase ?? Easing.OutCubic, Done = done };
        double maxDelay = 0;
        for (int i = 0; i < targets.Count; i++)
        {
            var (th, to, op) = targets[i];
            double d = delaysMs != null && i < delaysMs.Count ? Math.Max(0, delaysMs[i]) : 0;
            maxDelay = Math.Max(maxDelay, d);
            job.Items.Add(new Item { Thumb = th, From = th.Current, To = to, OpFrom = th.Opacity, OpTo = op, DelayMs = d });
        }
        job.TotalMs = job.DurationMs + maxDelay;
        lock (_jobs)
        {
            _jobs.Add(job);
            if (_thread == null)
            {
                _thread = new Thread(Loop) { IsBackground = true, Name = "WacOS.ThumbAnimator", Priority = ThreadPriority.AboveNormal };
                _thread.Start();
            }
        }
        _wake.Set();
        return job.Handle;
    }

    private static void Loop()
    {
        var batch = new List<Job>();
        while (true)
        {
            lock (_jobs) { batch.Clear(); batch.AddRange(_jobs); }
            if (batch.Count == 0) { _wake.WaitOne(); continue; }
            // Block until DWM has composed a frame; fall back to a short sleep if composition is unavailable.
            if (DwmThumb.DwmFlush() != 0) Thread.Sleep(7);
            long now = Stopwatch.GetTimestamp();
            foreach (var job in batch)
            {
                if (job.Handle.Cancelled) { lock (_jobs) _jobs.Remove(job); continue; }
                if (job.Last >= 0)
                {
                    double gap = (now - job.Last) * 1000.0 / Stopwatch.Frequency;
                    if (gap > job.MaxGap) { job.MaxGap = gap; job.WorstAt = job.Frames; }
                    if (gap > 25) job.Slow++;
                    job.ElapsedMs += Math.Min(gap, MaxStepMs);
                }
                job.Last = now; job.Frames++;
                bool finished = job.ElapsedMs >= job.TotalMs;
                try
                {
                    foreach (var it in job.Items)
                    {
                        double t = Math.Clamp((job.ElapsedMs - it.DelayMs) / job.DurationMs, 0, 1);
                        double p = finished ? 1 : job.Ease(t);
                        double po = finished ? 1 : Math.Clamp(job.OpacityEase(t), 0, 1);
                        it.Thumb.Set(Anim.Lerp(it.From, it.To, p), Anim.Lerp(it.OpFrom, it.OpTo, po));
                    }
                }
                catch (Exception ex) { Log.Error("thumb anim", ex); finished = true; }
                if (finished)
                {
                    lock (_jobs) _jobs.Remove(job);
                    job.Handle.IsRunning = false;
                    if (Log.Verbose && job.Frames > 2)
                        Log.Debug($"thumb anim {job.TotalMs:F0}ms x{job.Items.Count}: {job.Frames} frames, avg {job.ElapsedMs / (job.Frames - 1):F1}ms/frame, worst gap {job.MaxGap:F1}ms before frame #{job.WorstAt}, {job.Slow} slow frames");
                    var done = job.Done; var handle = job.Handle;
                    if (done != null)
                        App.Current?.Dispatcher.BeginInvoke(() =>
                        {
                            if (handle.Cancelled) return;
                            try { done(); } catch (Exception ex) { Log.Error("thumb anim done", ex); }
                        });
                }
            }
        }
    }
}
