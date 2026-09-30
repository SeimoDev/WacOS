using System.Diagnostics;
using WacOS.Native;

namespace WacOS.Desktop;

/// <summary>Enumerates "real" application windows (the Alt‑Tab set) with metadata.</summary>
public static class WindowEnumerator
{
    private static readonly uint _ownPid = (uint)Environment.ProcessId;
    private static readonly HashSet<string> _excludedClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow",
        "MultitaskingViewFrame", "ForegroundStaging", "Windows.Internal.Shell.TabProxyWindow", "tooltips_class32",
        "MSCTFIME UI", "IME", "SysShadow", "Button", "DummyDWMListenerWindow", "EdgeUiInputTopWndClass", "NativeHWNDHost", "ApplicationManager_DesktopShellWindow",
        "Shell_InputSwitchTopLevelWindow", "Shell_LightDismissOverlay", "ApplicationManager_ImmersiveShellWindow", "PopupHost", "Xaml_WindowedPopupClass",
    };

    /// <summary>
    /// Returns top-level windows that would appear in Alt‑Tab.
    /// </summary>
    /// <param name="includeMinimized">Include minimized windows.</param>
    /// <param name="includeOtherDesktops">Include windows that are cloaked because they live on another virtual desktop.</param>
    public static List<WindowInfo> GetWindows(bool includeMinimized = true, bool includeOtherDesktops = false)
    {
        var list = new List<WindowInfo>();
        User32.EnumWindows((hwnd, _) =>
        {
            var wi = Describe(hwnd, includeMinimized, includeOtherDesktops);
            if (wi != null) list.Add(wi);
            return true;
        }, IntPtr.Zero);
        return list; // EnumWindows yields Z‑order, top first
    }

    public static bool IsAppWindow(IntPtr hwnd, bool includeMinimized = true, bool includeOtherDesktops = false)
        => Describe(hwnd, includeMinimized, includeOtherDesktops) != null;

    public static WindowInfo? Describe(IntPtr hwnd, bool includeMinimized = true, bool includeOtherDesktops = false)
    {
        if (hwnd == IntPtr.Zero || !User32.IsWindow(hwnd) || !User32.IsWindowVisible(hwnd)) return null;
        if (User32.GetAncestor(hwnd, User32.GA_ROOT) != hwnd) return null;
        long style = User32.GetWindowLong(hwnd, User32.GWL_STYLE);
        long ex = User32.GetWindowLong(hwnd, User32.GWL_EXSTYLE);
        if ((style & User32.WS_CHILD) != 0) return null;
        if ((ex & User32.WS_EX_TOOLWINDOW) != 0 && (ex & User32.WS_EX_APPWINDOW) == 0) return null;
        if ((ex & User32.WS_EX_NOACTIVATE) != 0 && (ex & User32.WS_EX_APPWINDOW) == 0) return null;

        // Alt‑Tab rule: owned windows are represented by their root owner, unless the owner is invisible.
        var owner = User32.GetWindow(hwnd, User32.GW_OWNER);
        if (owner != IntPtr.Zero && User32.IsWindowVisible(owner) && (ex & User32.WS_EX_APPWINDOW) == 0) return null;

        User32.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == _ownPid) return null;

        string cls = User32.GetClassNameSafe(hwnd);
        if (_excludedClasses.Contains(cls)) return null;

        int cloaked = Dwm.GetCloaked(hwnd);
        bool minimized = User32.IsIconic(hwnd);
        if (cloaked != 0)
        {
            // DWM_CLOAKED_SHELL = on another virtual desktop; anything else is a hidden UWP shell window.
            if (cloaked != Dwm.DWM_CLOAKED_SHELL || !includeOtherDesktops) return null;
        }
        if (minimized && !includeMinimized) return null;

        string title = User32.GetWindowTextSafe(hwnd);
        if (title.Length == 0) return null;
        if (cls == "ApplicationFrameWindow" && !HasUwpContent(hwnd)) return null;

        var (path, name) = ProcessInfo.Get(pid);
        string appKey = path.Length > 0 ? path.ToLowerInvariant() : cls;
        if (cls == "ApplicationFrameWindow")
        {
            // UWP: identify by the hosted CoreWindow process so different store apps don't group together.
            var core = User32.FindWindowEx(hwnd, IntPtr.Zero, "Windows.UI.Core.CoreWindow", null);
            if (core != IntPtr.Zero)
            {
                User32.GetWindowThreadProcessId(core, out uint corePid);
                var (cp, cn) = ProcessInfo.Get(corePid);
                if (cp.Length > 0) { appKey = cp.ToLowerInvariant(); name = cn; path = cp; }
            }
            else appKey = "uwp:" + title;
        }

        var bounds = Dwm.GetFrameBounds(hwnd);
        if (minimized)
        {
            // Minimized windows sit at (-32000,-32000); use the restored position instead.
            var wp = new WINDOWPLACEMENT { length = System.Runtime.InteropServices.Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (User32.GetWindowPlacement(hwnd, ref wp) && wp.rcNormalPosition.Width > 0) bounds = wp.rcNormalPosition;
        }
        return new WindowInfo
        {
            Hwnd = hwnd, Title = title, ClassName = cls, ProcessId = pid, ExePath = path, AppName = name, AppKey = appKey,
            Bounds = bounds, IsMinimized = minimized, IsMaximized = User32.IsZoomed(hwnd), IsCloaked = cloaked != 0,
            Monitor = User32.MonitorFromWindow(hwnd, User32.MONITOR_DEFAULTTONEAREST),
        };
    }

    private static bool HasUwpContent(IntPtr frame)
    {
        // ApplicationFrameWindow without a CoreWindow child is a stale placeholder (unless minimized/cloaked handled already).
        var core = User32.FindWindowEx(frame, IntPtr.Zero, "Windows.UI.Core.CoreWindow", null);
        return core != IntPtr.Zero || User32.IsIconic(frame);
    }

    public static IntPtr DesktopListView()
    {
        // The desktop icons live in a SysListView32 under SHELLDLL_DefView, which is under Progman or a WorkerW.
        var progman = User32.FindWindow("Progman", null);
        var defView = User32.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView == IntPtr.Zero)
        {
            IntPtr worker = IntPtr.Zero;
            while ((worker = User32.FindWindowEx(IntPtr.Zero, worker, "WorkerW", null)) != IntPtr.Zero)
            {
                defView = User32.FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (defView != IntPtr.Zero) break;
            }
        }
        return defView == IntPtr.Zero ? IntPtr.Zero : User32.FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
    }

    /// <summary>True when the given window is the desktop background (wallpaper / icon view).</summary>
    public static bool IsDesktopWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        var cls = User32.GetClassNameSafe(hwnd);
        if (cls == "Progman" || cls == "WorkerW") return true;
        if (cls == "SysListView32" || cls == "SHELLDLL_DefView")
        {
            var root = User32.GetAncestor(hwnd, User32.GA_ROOT);
            var rc = User32.GetClassNameSafe(root);
            return rc == "Progman" || rc == "WorkerW";
        }
        return false;
    }
}
