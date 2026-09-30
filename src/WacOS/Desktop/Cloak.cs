using System.IO;
using WacOS.Native;

namespace WacOS.Desktop;

/// <summary>
/// Hides windows the way Windows hides windows of other virtual desktops: the shell cloaks them. A cloaked window keeps
/// rendering (its live thumbnail stays valid) and re-appears instantly without the app having to repaint, unlike a
/// minimized window. Two cloak flags are combined so that neither activation (taskbar, Alt+Tab) nor a desktop switch
/// reveals a window behind our back: WacOS alone decides when it appears, and animates it in.
/// Every hidden window is journaled on disk so a crash can never leave windows invisible.
/// </summary>
public static class Cloak
{
    // The shell keeps one cloak flag per reason, and a window is visible only when none is set. Two are used:
    //  - the "app" flag (1) survives virtual-desktop switches, but the shell clears it when the window is activated;
    //  - the "desktop" flag (2) survives activation (Alt+Tab, taskbar), but is recomputed on desktop switches.
    // With both set the window stays hidden in either case, so WacOS decides when it appears (with an animation).
    private const int AppCloak = 1, DesktopCloak = 2;
    private static readonly HashSet<IntPtr> _hidden = new();
    private static readonly string JournalPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WacOS", "hidden-windows.txt");

    /// <summary>Hides a window. Returns false when the shell refuses (then the caller falls back to minimizing).</summary>
    public static bool Hide(IntPtr hwnd)
    {
        if (!User32.IsWindow(hwnd)) return false;
        int hr = App.Desktops.SetViewCloak(hwnd, AppCloak, 1);
        if (hr != 0 || Dwm.GetCloaked(hwnd) == 0) return false;
        App.Desktops.SetViewCloak(hwnd, DesktopCloak, 1);
        lock (_hidden) _hidden.Add(hwnd);
        Save();
        return true;
    }

    public static void Show(IntPtr hwnd)
    {
        bool tracked;
        lock (_hidden) tracked = _hidden.Remove(hwnd);
        if (User32.IsWindow(hwnd)) Reveal(hwnd);
        if (tracked) Save();
    }

    /// <summary>True while the window is hidden by us (false once the shell revealed it because it was activated).</summary>
    public static bool IsHidden(IntPtr hwnd)
    {
        lock (_hidden) if (!_hidden.Contains(hwnd)) return false;
        if (User32.IsWindow(hwnd) && Dwm.GetCloaked(hwnd) != 0) return true;
        lock (_hidden) _hidden.Remove(hwnd);
        Save();
        return false;
    }

    /// <summary>
    /// Stops hiding a window that now lives on another desktop. Only our own flag is cleared; the desktop flag is left
    /// to the shell, so the window does not pop up on the desktop the user is looking at.
    /// </summary>
    public static void ReleaseElsewhere(IntPtr hwnd)
    {
        bool tracked;
        lock (_hidden) tracked = _hidden.Remove(hwnd);
        if (!tracked) return;
        if (User32.IsWindow(hwnd)) App.Desktops.SetViewCloak(hwnd, AppCloak, 0);
        Save();
    }

    private static void Reveal(IntPtr hwnd)
    {
        App.Desktops.SetViewCloak(hwnd, DesktopCloak, 0);
        App.Desktops.SetViewCloak(hwnd, AppCloak, 0);
    }

    /// <summary>Sets both flags again on every hidden window (the shell drops one of them on a desktop switch).</summary>
    public static void Reinforce()
    {
        IntPtr[] all;
        lock (_hidden) all = _hidden.ToArray();
        foreach (var h in all)
        {
            if (!User32.IsWindow(h) || Dwm.GetCloaked(h) == 0) continue;
            App.Desktops.SetViewCloak(h, AppCloak, 1);
            App.Desktops.SetViewCloak(h, DesktopCloak, 1);
        }
    }

    public static bool Tracks(IntPtr hwnd) { lock (_hidden) return _hidden.Contains(hwnd); }

    public static void ShowAll()
    {
        IntPtr[] all;
        lock (_hidden) { all = _hidden.ToArray(); _hidden.Clear(); }
        foreach (var h in all) { try { if (User32.IsWindow(h)) Reveal(h); } catch { } }
        Save();
    }

    /// <summary>Reveals windows that a previous (crashed or killed) instance left hidden.</summary>
    public static void RecoverFromJournal()
    {
        try
        {
            if (!File.Exists(JournalPath)) return;
            int n = 0;
            foreach (var line in File.ReadAllLines(JournalPath))
            {
                if (!long.TryParse(line.Trim(), out var v)) continue;
                var h = new IntPtr(v);
                if (User32.IsWindow(h) && Dwm.GetCloaked(h) != 0) { Reveal(h); n++; }
            }
            File.Delete(JournalPath);
            if (n > 0) Log.Warn($"Recovered {n} window(s) left hidden by a previous session");
        }
        catch (Exception ex) { Log.Error("cloak recovery", ex); }
    }

    private static void Save()
    {
        try
        {
            string[] lines;
            lock (_hidden) lines = _hidden.Select(h => h.ToInt64().ToString()).ToArray();
            if (lines.Length == 0) { if (File.Exists(JournalPath)) File.Delete(JournalPath); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(JournalPath)!);
            File.WriteAllLines(JournalPath, lines);
        }
        catch { /* best effort */ }
    }
}
