using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using WacOS.Native;

namespace WacOS.Input;

/// <summary>
/// Windows has its own three- and four-finger touchpad swipes (switch apps, show desktop, switch desktops). They fire
/// alongside WacOS' gestures, so while WacOS handles a finger count the matching system gesture is set to "Nothing"
/// (the same values the Settings app writes). The previous values are kept in a small file and put back when WacOS
/// exits, or at the next start if it did not exit cleanly.
/// </summary>
public static class SystemTouchpadGestures
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad";
    private static readonly string[] Names = { "ThreeFingerSlideEnabled", "FourFingerSlideEnabled" };
    private static readonly string Backup = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WacOS", "touchpad-gestures.bak");

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SendNotifyMessage(IntPtr hWnd, uint msg, IntPtr wParam, string? lParam);

    /// <summary>Blocks the system swipes for the finger counts WacOS uses (or restores them when block is false).</summary>
    public static void Apply(bool block, bool three, bool four)
    {
        try
        {
            if (!block || (!three && !four)) { Restore(); return; }
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
            if (key == null) return;
            // Remember the user's own values once.
            if (!File.Exists(Backup))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Backup)!);
                File.WriteAllLines(Backup, Names.Select(n => n + "=" + (key.GetValue(n) is int v ? v.ToString() : "unset")));
            }
            var saved = ReadBackup();
            bool changed = false;
            changed |= Set(key, Names[0], three ? 0 : saved.GetValueOrDefault(Names[0]));
            changed |= Set(key, Names[1], four ? 0 : saved.GetValueOrDefault(Names[1]));
            if (changed) { Notify(); Log.Info($"Windows touchpad swipes set to Nothing (three-finger: {three}, four-finger: {four})"); }
        }
        catch (Exception ex) { Log.Warn("System touchpad gestures: " + ex.Message); }
    }

    /// <summary>Puts the user's own gesture settings back.</summary>
    public static void Restore()
    {
        try
        {
            if (!File.Exists(Backup)) return;
            var saved = ReadBackup();
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
            if (key != null) foreach (var n in Names) Set(key, n, saved.GetValueOrDefault(n));
            File.Delete(Backup);
            Notify();
        }
        catch (Exception ex) { Log.Warn("System touchpad gestures restore: " + ex.Message); }
    }

    private static Dictionary<string, int?> ReadBackup()
    {
        var d = new Dictionary<string, int?>();
        foreach (var line in File.ReadAllLines(Backup))
        {
            var p = line.Split('=');
            if (p.Length == 2) d[p[0]] = int.TryParse(p[1], out var v) ? v : null;
        }
        return d;
    }

    /// <summary>Writes a value (null = delete it, i.e. the system default). Returns true when something changed.</summary>
    private static bool Set(RegistryKey key, string name, int? value)
    {
        var cur = key.GetValue(name) as int?;
        if (cur == value) return false;
        if (value == null) key.DeleteValue(name, throwOnMissingValue: false);
        else key.SetValue(name, value.Value, RegistryValueKind.DWord);
        return true;
    }

    private static void Notify()
    {
        // The input stack re-reads the touchpad settings on this broadcast. It is posted, not sent: a synchronous
        // broadcast waits on every top-level window and can stall start-up and exit for seconds.
        SendNotifyMessage(new IntPtr(0xFFFF), User32.WM_SETTINGCHANGE, IntPtr.Zero, "PrecisionTouchPad");
    }
}
