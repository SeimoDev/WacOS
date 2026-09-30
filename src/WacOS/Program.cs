using System.Runtime.InteropServices;
using System.Text;
using WacOS.Desktop;
using WacOS.VirtualDesktops;

namespace WacOS;

public static class Program
{
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int pid);
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);

    public const string QuitEventName = "WacOS.QuitEvent";

    [STAThread]
    public static int Main(string[] args)
    {
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); /* PER_MONITOR_AWARE_V2 */ } catch { }
        if (args.Contains("--selftest")) return SelfTest();
        if (args.Contains("--verbose")) Log.Verbose = true;
        App.ForceStageManager = args.Contains("--stage");
        App.OpenSettingsAtStart = args.Contains("--settings");
        int langAt = Array.IndexOf(args, "--lang");
        if (langAt >= 0 && langAt + 1 < args.Length) L.Override = args[langAt + 1];

        if (args.Contains("--quit"))
        {
            // Ask the running instance to shut down gracefully (restores Stage Manager windows, hooks, desktop icons).
            try { using var ev = EventWaitHandle.OpenExisting(QuitEventName); ev.Set(); return 0; }
            catch (UnauthorizedAccessException)
            {
                // The running instance is elevated and this one is not: ask again from an elevated copy.
                return !Elevation.IsElevated && Elevation.Relaunch(new[] { "--quit" }) ? 0 : 1;
            }
            catch { return 1; }
        }

        // A relaunched copy waits for the instance that started it to go away.
        int waitAt = Array.IndexOf(args, "--wait-pid");
        if (waitAt >= 0 && waitAt + 1 < args.Length && int.TryParse(args[waitAt + 1], out int waitPid))
        {
            try { System.Diagnostics.Process.GetProcessById(waitPid).WaitForExit(8000); } catch { }
        }

        // Administrator mode: hand over to an elevated copy before anything else is set up.
        App.Settings.Load();
        bool wantElevated = (App.Settings.Current.RunAsAdministrator || args.Contains("--elevate")) && !args.Contains("--no-elevate");
        if (wantElevated && !Elevation.IsElevated)
        {
            var pass = args.Where(a => a != "--elevate").Append("--no-elevate");
            if (Elevation.Relaunch(pass)) return 0;
            // Declined or failed: carry on without elevation.
        }

        Mutex mutex;
        bool created;
        try { mutex = new Mutex(true, "WacOS.SingleInstance", out created); }
        catch (UnauthorizedAccessException) { mutex = null!; created = false; }   // an elevated instance owns it
        if (!created)
        {
            System.Windows.MessageBox.Show(L.T("WacOS is already running (see the tray icon)."), "WacOS");
            return 0;
        }
        using var mutexHold = mutex;
        using var quitEvent = new EventWaitHandle(false, EventResetMode.ManualReset, QuitEventName);
        var quitWatcher = new Thread(() => { quitEvent.WaitOne(); App.Current?.Dispatcher.BeginInvoke(() => App.Current.Quit()); }) { IsBackground = true };
        quitWatcher.Start();
        var app = new App();
        return app.Run();
    }

    /// <summary>Verifies the undocumented COM API and window enumeration on this machine without starting the UI.</summary>
    private static int SelfTest()
    {
        AttachConsole(-1);
        var sb = new StringBuilder();
        sb.AppendLine($"Windows {Environment.OSVersion.Version}");
        try
        {
            using var vd = new VirtualDesktopService();
            if (!vd.IsAvailable) { sb.AppendLine("Virtual desktop COM API: NOT AVAILABLE"); Console.WriteLine(sb); return 2; }
            var spaces = vd.GetSpaces();
            sb.AppendLine($"Virtual desktop COM API: OK ({spaces.Count} spaces, current = {vd.Current?.DisplayName})");
            foreach (var s in spaces) sb.AppendLine($"  [{s.Index}] {s.DisplayName} {s.Id} wallpaper='{s.WallpaperPath}'");
            foreach (var m in Monitors.All()) sb.AppendLine($"Monitor: {m}");
            var windows = WindowEnumerator.GetWindows(includeMinimized: true, includeOtherDesktops: true);
            sb.AppendLine($"App windows: {windows.Count}");
            foreach (var w in windows.Take(40))
            {
                var sp = vd.GetWindowSpaceId(w.Hwnd);
                var spName = spaces.FirstOrDefault(x => x.Id == sp)?.DisplayName ?? (vd.IsWindowPinned(w.Hwnd) ? "pinned" : sp.ToString());
                var bmp = WindowCapture.Capture(w.Hwnd);
                sb.AppendLine($"  {w.Hwnd:X8} '{Trunc(w.Title, 40)}' app='{w.AppName}' {w.Bounds} min={w.IsMinimized} fs={w.IsFullscreen} space={spName} capture={(bmp == null ? "none" : bmp.PixelWidth + "x" + bmp.PixelHeight)}");
            }
            Console.WriteLine(sb);
            Log.Info("Self test:\n" + sb);
            return 0;
        }
        catch (Exception ex)
        {
            sb.AppendLine("FAILED: " + ex);
            Console.WriteLine(sb);
            return 1;
        }
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
