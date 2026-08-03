using ManifestApp.Core.Models;

namespace ManifestApp.Core;

public sealed class SteamPathsResolver(SettingsStore settingsStore)
{
    public string? ResolveSteamInstall()
    {
        var o = settingsStore.Load().SteamInstallPathOverride?.Trim().TrimEnd('\\');
        if (!string.IsNullOrEmpty(o) && Directory.Exists(o) && File.Exists(Path.Combine(o, "steam.exe")))
            return o;

        foreach (var sub in new[] { @"SOFTWARE\WOW6432Node\Valve\Steam", @"SOFTWARE\Valve\Steam" })
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(sub);
            var path = key?.GetValue("InstallPath") as string;
            path = path?.Trim().TrimEnd('\\');
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path) && File.Exists(Path.Combine(path, "steam.exe")))
                return path;
        }

        return null;
    }

    public string? ResolveStPluginFolder()
    {
        var s = settingsStore.Load();
        var o = s.StPluginFolderOverride?.Trim().TrimEnd('\\').Trim('"');
        if (!string.IsNullOrEmpty(o))
            return o;

        var steam = ResolveSteamInstall();
        if (string.IsNullOrEmpty(steam))
            return null;

        var d = Path.Combine(steam, "config", "stplug-in");
        return d;
    }

    public string? ResolveDepotCacheFolder()
    {
        var s = settingsStore.Load();
        var o = s.DepotCacheFolderOverride?.Trim().TrimEnd('\\').Trim('"');
        if (!string.IsNullOrEmpty(o))
            return o;

        var steam = ResolveSteamInstall();
        if (string.IsNullOrEmpty(steam))
            return null;

        return Path.Combine(steam, "config", "depotcache");
    }

    public string? DefaultStPluginFromSteamRoot(string steamRoot) =>
        Path.Combine(steamRoot, "config", "stplug-in");

    public string? DefaultDepotCacheFromSteamRoot(string steamRoot) =>
        Path.Combine(steamRoot, "config", "depotcache");

    public static void EnsureDirectoryExists(string path) =>
        Directory.CreateDirectory(path);
}
