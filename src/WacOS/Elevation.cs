using System.Diagnostics;
using System.Security.Principal;

namespace WacOS;

/// <summary>
/// Administrator mode. A normal process cannot move, resize or screenshot the windows of elevated apps (Task Manager,
/// apps run as administrator), and its input hooks do not see input aimed at them. Running WacOS elevated removes
/// those limits.
/// </summary>
public static class Elevation
{
    public const string TaskName = "WacOS (administrator)";

    public static bool IsElevated
    {
        get
        {
            try { using var id = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); }
            catch { return false; }
        }
    }

    /// <summary>Starts another copy of WacOS elevated. Returns false when the user declined or it could not start.</summary>
    public static bool Relaunch(IEnumerable<string> args)
    {
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
            foreach (var a in args) psi.ArgumentList.Add(a);
            return Process.Start(psi) != null;
        }
        catch (Exception ex) { Log.Warn("Could not start elevated: " + ex.Message); return false; }
    }

    /// <summary>
    /// Sign-in start-up for administrator mode: a scheduled task with highest privileges starts WacOS elevated without
    /// a consent prompt (a "Run" registry entry cannot do that). Creating or deleting it needs elevation.
    /// </summary>
    public static bool SetLogonTask(bool enable)
    {
        try
        {
            string args = enable
                ? $"/Create /TN \"{TaskName}\" /TR \"\\\"{Environment.ProcessPath}\\\"\" /SC ONLOGON /RL HIGHEST /IT /F"
                : $"/Delete /TN \"{TaskName}\" /F";
            var psi = new ProcessStartInfo("schtasks.exe", args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            using var p = Process.Start(psi)!;
            p.WaitForExit(8000);
            return p.ExitCode == 0;
        }
        catch (Exception ex) { Log.Warn("Logon task: " + ex.Message); return false; }
    }

    public static bool LogonTaskExists()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", $"/Query /TN \"{TaskName}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            using var p = Process.Start(psi)!;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }
}
