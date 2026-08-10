namespace ManifestApp.Core;

public sealed class SteamToolsLocator(SettingsStore settingsStore, SteamPathsResolver pathsResolver)
{
    public bool TryFindSteamTools([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? path)
    {
        path = null;

        var s = settingsStore.Load().SteamToolsExeOverride?.Trim().Trim('"');
        if (!string.IsNullOrEmpty(s) && File.Exists(s))
        {
            path = s;
            return true;
        }

        var steam = pathsResolver.ResolveSteamInstall();
        if (!string.IsNullOrEmpty(steam))
        {
            var direct = Path.Combine(steam, "SteamTools.exe");
            if (File.Exists(direct))
            {
                path = direct;
                return true;
            }

            foreach (var f in Directory.EnumerateFiles(steam, "*.exe", SearchOption.TopDirectoryOnly))
            {
                if (string.Equals(Path.GetFileName(f), "SteamTools.exe", StringComparison.OrdinalIgnoreCase))
                {
                    path = f;
                    return true;
                }
            }
        }

        var programFilesX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
        var programFiles    = Environment.GetEnvironmentVariable("ProgramFiles");
        foreach (var baseDir in new[] { programFiles, programFilesX86 })
        {
            if (string.IsNullOrWhiteSpace(baseDir))
                continue;

            // Primary: %ProgramFiles%\SteamTools\SteamTools.exe
            var inSubfolder = Path.Combine(baseDir, "SteamTools", "SteamTools.exe");
            if (File.Exists(inSubfolder))
            {
                path = inSubfolder;
                return true;
            }

            // Fallback: %ProgramFiles%\SteamTools.exe (flat install)
            var flat = Path.Combine(baseDir, "SteamTools.exe");
            if (File.Exists(flat))
            {
                path = flat;
                return true;
            }
        }

        return false;
    }
}
