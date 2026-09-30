using WacOS.Desktop;
using WacOS.Input;
using WacOS.Native;
using WacOS.Settings;

namespace WacOS.Features;

public sealed class HotCornerService
{
    public HotCornerService(MouseService mouse) => mouse.HotCorner += OnCorner;

    private void OnCorner(Corner corner, MonitorInfo mon)
    {
        var s = App.Settings.Current;
        var action = corner switch
        {
            Corner.TopLeft => s.HotCornerTopLeft,
            Corner.TopRight => s.HotCornerTopRight,
            Corner.BottomLeft => s.HotCornerBottomLeft,
            _ => s.HotCornerBottomRight,
        };
        Run(action, mon);
    }

    public static void Run(HotCornerAction action, MonitorInfo? mon = null)
    {
        switch (action)
        {
            case HotCornerAction.MissionControl: App.MissionControl.Toggle(); break;
            case HotCornerAction.ApplicationWindows: App.MissionControl.ToggleAppExpose(); break;
            case HotCornerAction.Desktop: App.ShowDesktop.Toggle(); break;
            case HotCornerAction.NotificationCenter: User32.SendKeyChord((ushort)User32.VK_LWIN, (ushort)'N'); break;
            case HotCornerAction.QuickNote: User32.SendKeyChord((ushort)User32.VK_LWIN, (ushort)'N'); break;
            case HotCornerAction.Launchpad: User32.SendKeyTap(User32.VK_LWIN); break;
            case HotCornerAction.LockScreen: User32.LockWorkStation(); break;
            case HotCornerAction.SleepDisplay: PowrProf.SleepDisplay(); break;
        }
    }
}
