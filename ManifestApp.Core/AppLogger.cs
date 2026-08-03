using System;
using System.IO;
using System.Globalization;

namespace ManifestApp.Core;

public static class AppLogger
{
    private static readonly object LockObj = new();

    private static string LogPath => Path.Combine(AppPaths.LocalRoot, "app.log");

    public static void Log(string message, string level = "INFO")
    {
        try
        {
            AppPaths.EnsureLayout();
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            var line = $"[{timestamp}] [{level}] [PID {Environment.ProcessId}] {message}{Environment.NewLine}";

            lock (LockObj)
            {
                File.AppendAllText(LogPath, line);
            }
        }
        catch
        {
            // Fail-safe to prevent app crashes due to logging issues
        }
    }

    public static void LogException(string source, Exception? ex)
    {
        if (ex == null) return;
        Log($"Exception in {source}: {ex.GetType().FullName} - {ex.Message}{Environment.NewLine}Stack Trace: {ex.StackTrace}", "ERROR");
    }
}
