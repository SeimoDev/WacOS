using System.Runtime.InteropServices;

namespace WacOS.Native;

public static class Dwm
{
    public const int DWMWA_CLOAKED = 14, DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAK = 13, DWMWA_WINDOW_CORNER_PREFERENCE = 33,
        DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_SYSTEMBACKDROP_TYPE = 38, DWMWA_TRANSITIONS_FORCEDISABLED = 3;

    public const int DWM_CLOAKED_APP = 1, DWM_CLOAKED_SHELL = 2, DWM_CLOAKED_INHERITED = 4;

    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmIsCompositionEnabled(out bool enabled);

    public static int GetCloaked(IntPtr hwnd)
    {
        return DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int v, sizeof(int)) == 0 ? v : 0;
    }

    /// <summary>Window bounds without the invisible resize borders (what the user perceives).</summary>
    public static RECT GetFrameBounds(IntPtr hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) == 0 && r.Width > 0 && r.Height > 0)
            return r;
        User32.GetWindowRect(hwnd, out r);
        return r;
    }

    public static void SetCornerPreference(IntPtr hwnd, int pref)
    {
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
    }

    public static void DisableTransitions(IntPtr hwnd, bool disable)
    {
        int v = disable ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_TRANSITIONS_FORCEDISABLED, ref v, sizeof(int));
    }
}
