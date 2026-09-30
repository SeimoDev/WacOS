using System.Windows;
using System.Windows.Threading;
using WacOS.Desktop;
using WacOS.Features;
using WacOS.Features.MissionControl;
using WacOS.Features.Spaces;
using WacOS.Features.StageManager;
using WacOS.Input;
using WacOS.Native;
using WacOS.Settings;
using WacOS.Tray;
using WacOS.VirtualDesktops;

namespace WacOS;

public sealed class App : Application
{
    public static new App Current => (App)Application.Current;
    public static SettingsStore Settings { get; } = new();
    public static bool ForceStageManager { get; set; }
    public static bool OpenSettingsAtStart { get; set; }

    public static VirtualDesktopService Desktops { get; private set; } = null!;
    public static WindowTracker Tracker { get; private set; } = null!;
    public static LowLevelHooks Hooks { get; private set; } = null!;
    public static HotkeyService Hotkeys { get; private set; } = null!;
    public static TouchpadGestureService Gestures { get; private set; } = null!;
    public static MouseService Mouse { get; private set; } = null!;
    public static MissionControlService MissionControl { get; private set; } = null!;
    public static StageManagerService StageManager { get; private set; } = null!;
    public static SpacesFeature Spaces { get; private set; } = null!;
    public static ShowDesktopService ShowDesktop { get; private set; } = null!;
    public static HotCornerService HotCorners { get; private set; } = null!;
    public static TrayIcon Tray { get; private set; } = null!;

    private SettingsWindow? _settingsWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += (_, ex) => { Log.Error("Unhandled", ex.Exception); ex.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => { Log.Error("Fatal", ex.ExceptionObject as Exception); try { Cloak.ShowAll(); } catch { } };

        Settings.Load();
        Log.Info($"WacOS starting on Windows {Environment.OSVersion.Version}, administrator mode: {(Elevation.IsElevated ? "on" : "off")}");

        Desktops = new VirtualDesktopService();
        Cloak.RecoverFromJournal();   // a crashed session must never leave windows hidden
        EdgeSnap.RecoverFromCrash();
        ApplyTouchpadBlock();
        Settings.Changed += ApplyTouchpadBlock;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Cloak.ShowAll(); EdgeSnap.Suspend(false); SystemTouchpadGestures.Restore(); } catch { } };
        Tracker = new WindowTracker();
        Hooks = new LowLevelHooks();
        Hotkeys = new HotkeyService(Hooks);
        Gestures = new TouchpadGestureService();
        Mouse = new MouseService(Hooks, Tracker);
        ShowDesktop = new ShowDesktopService();
        // Order matters: StageManager must exist before MissionControl/Spaces reference it through App.
        StageManager = new StageManagerService(Desktops, Tracker, Mouse, Hotkeys);
        MissionControl = new MissionControlService(Desktops, Tracker, Hotkeys, Gestures, Mouse);
        Spaces = new SpacesFeature(Desktops, Tracker, Hotkeys, Gestures, Mouse);
        HotCorners = new HotCornerService(Mouse);
        Hotkeys.Register("ShowDesktop", () => Settings.Current.ShowDesktop, ShowDesktop.Toggle);
        Tracker.ForegroundChanged += h => { if (ShowDesktop.IsShown && WindowEnumerator.IsAppWindow(h, includeMinimized: false)) ShowDesktop.OnUserActivity(); };

        Tray = new TrayIcon();
        StartupRegistration.Apply(Settings.Current.StartWithWindows);
        Settings.Changed += () => StartupRegistration.Apply(Settings.Current.StartWithWindows);

        if (Settings.Current.StageManagerEnabled) StageManager.SetEnabled(true);
        else if (ForceStageManager) StageManager.SetEnabled(true, persist: false);
        else DesktopIcons.SetVisible(Settings.Current.ShowItemsOnDesktop);

        if (!Desktops.IsAvailable)
            MessageBox.Show(L.T("WacOS could not connect to the Windows virtual desktop service.\nThis build of Windows may be unsupported (Windows 11 24H2 / 25H2 required)."),
                "WacOS", MessageBoxButton.OK, MessageBoxImage.Warning);

        if (!Settings.Current.FirstRunDone)
        {
            Settings.Update(s => s.FirstRunDone = true);
            OpenSettings();
        }
        else if (OpenSettingsAtStart) OpenSettings();
    }

    /// <summary>
    /// Brings another application's window to the foreground reliably: the shell does it on our behalf (as for a
    /// taskbar click); the classic SetForegroundWindow work-around is only the fallback.
    /// </summary>
    public static void Activate(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !User32.IsWindow(hwnd)) return;
        if (User32.IsIconic(hwnd)) User32.ShowWindow(hwnd, User32.SW_RESTORE);
        if (User32.GetForegroundWindow() == hwnd) return;
        if (Desktops.ActivateView(hwnd) && User32.GetForegroundWindow() == hwnd) return;
        User32.ForceForeground(hwnd);
    }

    private static void ApplyTouchpadBlock()
    {
        var s = Settings.Current;
        bool three = s.SwipeBetweenSpacesFingers == 3 || s.MissionControlGestureFingers == 3;
        bool four = s.SwipeBetweenSpacesFingers == 4 || s.MissionControlGestureFingers == 4 || s.ShowDesktopGesture || s.LaunchpadGesture;
        SystemTouchpadGestures.Apply(s.BlockWindowsTouchpadGestures, three, four);
    }

    public void OpenSettings()
    {
        if (_settingsWindow == null || !_settingsWindow.IsLoaded)
        {
            _settingsWindow = new SettingsWindow();
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        _settingsWindow.Show();
        _settingsWindow.Activate();
        User32.ForceForeground(new System.Windows.Interop.WindowInteropHelper(_settingsWindow).Handle);
    }

    /// <summary>Restarts WacOS elevated (used when administrator mode is switched on in Settings).</summary>
    public void RestartElevated()
    {
        if (Elevation.IsElevated) return;
        if (Elevation.Relaunch(new[] { "--no-elevate", "--wait-pid", Environment.ProcessId.ToString() })) Quit();
        else System.Windows.MessageBox.Show(L.T("WacOS could not be started as administrator."), "WacOS");
    }

    public void Quit()
    {
        try
        {
            StageManager.Shutdown();          // synchronous: restores every parked window (the on/off setting is kept)
            Cloak.ShowAll();
            SystemTouchpadGestures.Restore();
            DesktopIcons.SetVisible(true);
            MissionControl.Shutdown();
            Tray.Dispose();
            Gestures.Dispose();
            Hooks.Dispose();
            Tracker.Dispose();
            Desktops.Dispose();
        }
        catch (Exception ex) { Log.Error("Shutdown", ex); }
        Shutdown();
    }
}

public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// Normal mode: a "Run" registry entry. Administrator mode: a scheduled task with highest privileges instead,
    /// because a Run entry cannot start an elevated program.
    /// </summary>
    public static void Apply(bool enable)
    {
        try
        {
            bool admin = App.Settings.Current.RunAsAdministrator;
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
            {
                if (key != null)
                {
                    if (enable && !admin) key.SetValue("WacOS", $"\"{Environment.ProcessPath}\"");
                    else if (key.GetValue("WacOS") != null) key.DeleteValue("WacOS");
                }
            }
            if (!Elevation.IsElevated) return;   // the task can only be changed from an elevated instance
            bool wantTask = enable && admin;
            if (wantTask != Elevation.LogonTaskExists()) Elevation.SetLogonTask(wantTask);
        }
        catch (Exception ex) { Log.Warn("Startup registration: " + ex.Message); }
    }

}
