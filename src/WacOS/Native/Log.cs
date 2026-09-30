using System.IO;

namespace WacOS;

public static class Log
{
    private static readonly object _lock = new();
    public static string LogPath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WacOS", "wacos.log");
    public static bool Verbose { get; set; }

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Debug(string msg) { if (Verbose) Write("DBG ", msg); }
    public static void Error(string msg, Exception? ex = null) => Write("ERR ", ex == null ? msg : msg + ": " + ex);

    private static void Write(string level, string msg)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {level} {msg}";
        System.Diagnostics.Debug.WriteLine(line);
        Console.WriteLine(line);
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 2_000_000) File.Delete(LogPath);
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
        }
        catch { /* ignore */ }
    }
}
