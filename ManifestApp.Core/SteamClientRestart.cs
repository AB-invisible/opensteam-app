using System.Diagnostics;

namespace ManifestApp.Core;

/// <summary>Graceful Steam client restart via <c>steam.exe -shutdown</c> then relaunch.</summary>
public static class SteamClientRestart
{
    public sealed record RestartResult(bool Succeeded, string Message);

    /// <summary>Asks Steam to exit, waits for client processes to stop, then starts Steam again.</summary>
    public static async Task<RestartResult> TryGracefulRestartAsync(
        string steamInstallRoot,
        TimeSpan maxWaitForExit,
        CancellationToken cancellationToken = default)
    {
        var root = steamInstallRoot.Trim().TrimEnd('\\');
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            return new RestartResult(false, "Steam install folder is not set or does not exist.");

        var steamExe = Path.Combine(root, "steam.exe");
        if (!File.Exists(steamExe))
            return new RestartResult(false, "steam.exe was not found in the Steam folder.");

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = steamExe,
                Arguments = "-shutdown",
                WorkingDirectory = root,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            return new RestartResult(false, $"Could not request Steam shutdown: {ex.Message}");
        }

        var deadline = DateTime.UtcNow.Add(maxWaitForExit);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!AnySteamExeProcessLikelyRunning())
                break;
            await Task.Delay(400, cancellationToken).ConfigureAwait(false);
        }

        if (AnySteamExeProcessLikelyRunning())
        {
            return new RestartResult(false,
                "Steam did not exit in time after -shutdown. Close Steam manually from the tray or Task Manager, then open it again.");
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = steamExe,
                WorkingDirectory = root,
                UseShellExecute = true,
            });

            return new RestartResult(true,
                "Steam was closed and started again from your Steam folder. Allow a few moments for it to reconnect.");
        }
        catch (Exception ex)
        {
            return new RestartResult(false,
                $"Steam exited, but relaunch failed: {ex.Message}\nOpen Steam manually: {steamExe}");
        }
    }

    /// <remarks>Uses process name steam (Steam client main EXE).</remarks>
    private static bool AnySteamExeProcessLikelyRunning()
    {
        Process[]? procs = null;
        try
        {
            procs = Process.GetProcessesByName("steam");
            return procs.Length > 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (procs is not null)
            {
                foreach (var p in procs)
                {
                    try
                    {
                        p.Dispose();
                    }
                    catch
                    {
                        /* ignore */
                    }
                }
            }
        }
    }
}
