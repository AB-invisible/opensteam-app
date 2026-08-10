using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using ValveKeyValue;

namespace ManifestApp.Core;

public sealed class SteamGameMonitor(SettingsStore settingsStore)
{
    /// <summary>
    /// Waits until the specified Steam App ID is fully installed, or the cancellation token is triggered.
    /// Returns the absolute path to the game's installation directory if successful, otherwise null.
    /// </summary>
    public async Task<string?> WaitForInstallationAsync(uint appId, CancellationToken ct)
    {
        var resolver = new SteamPathsResolver(settingsStore);
        var steamRoot = resolver.ResolveSteamInstall();
        if (string.IsNullOrEmpty(steamRoot)) return null;

        var libraryFolders = CollectSteamAppsDirectories(steamRoot);
        if (libraryFolders.Count == 0) return null;

        while (!ct.IsCancellationRequested)
        {
            foreach (var dir in libraryFolders)
            {
                if (!Directory.Exists(dir)) continue;

                var acfPath = Path.Combine(dir, $"appmanifest_{appId}.acf");
                if (File.Exists(acfPath))
                {
                    if (TryParseAppManifest(acfPath, out var stateFlags, out var installDir))
                    {
                        // StateFlags == 4 means StateFullyInstalled
                        if (stateFlags == 4 && !string.IsNullOrWhiteSpace(installDir))
                        {
                            var gamePath = Path.Combine(dir, "common", installDir);
                            if (Directory.Exists(gamePath))
                                return gamePath;
                        }
                    }
                }
            }

            try
            {
                await Task.Delay(3000, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the install directory when the game is already fully installed, without waiting.
    /// </summary>
    public string? TryGetInstalledGamePath(uint appId)
    {
        var resolver = new SteamPathsResolver(settingsStore);
        var steamRoot = resolver.ResolveSteamInstall();
        if (string.IsNullOrEmpty(steamRoot)) return null;

        foreach (var dir in CollectSteamAppsDirectories(steamRoot))
        {
            if (!Directory.Exists(dir)) continue;

            var acfPath = Path.Combine(dir, $"appmanifest_{appId}.acf");
            if (!File.Exists(acfPath)) continue;

            if (!TryParseAppManifest(acfPath, out var stateFlags, out var installDir)) continue;
            if (stateFlags != 4 || string.IsNullOrWhiteSpace(installDir)) continue;

            var gamePath = Path.Combine(dir, "common", installDir);
            if (Directory.Exists(gamePath))
                return gamePath;
        }

        return null;
    }

    private static List<string> CollectSteamAppsDirectories(string steamInstall)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var main = Path.Combine(steamInstall, "steamapps");
        if (Directory.Exists(main))
            set.Add(main);

        var vdf = Path.Combine(main, "libraryfolders.vdf");
        if (File.Exists(vdf))
        {
            try
            {
                using var fs = File.OpenRead(vdf);
                var serializer = KVSerializer.Create(KVSerializationFormat.KeyValues1Text);
                var root = serializer.Deserialize(fs);
                var acc = new List<string>();
                CollectPathStrings(root, acc);
                foreach (var p in acc)
                {
                    if (!string.IsNullOrWhiteSpace(p))
                    {
                        var normalized = NormalizeToSteamApps(p.Replace(@"\\", @"\", StringComparison.Ordinal));
                        if (Directory.Exists(normalized))
                            set.Add(normalized);
                    }
                }
            }
            catch { /* best effort */ }
        }

        return [.. set];
    }

    private static void CollectPathStrings(KVObject node, List<string> acc)
    {
        if (node.TryGetValue("path", out var pathChild) && pathChild.ValueType == KVValueType.String)
            acc.Add((string)pathChild);

        foreach (var kvp in node)
            CollectPathStrings(kvp.Value, acc);
    }

    private static string NormalizeToSteamApps(string pathFromVdf)
    {
        var p = pathFromVdf.Trim().TrimEnd('\\');
        if (p.EndsWith("steamapps", StringComparison.OrdinalIgnoreCase))
            return p;
        return Path.Combine(p, "steamapps");
    }

    private static bool TryParseAppManifest(string acfPath, out int stateFlags, out string? installDir)
    {
        stateFlags = 0;
        installDir = null;
        try
        {
            using var fs = File.OpenRead(acfPath);
            var serializer = KVSerializer.Create(KVSerializationFormat.KeyValues1Text);
            var root = serializer.Deserialize(fs);

            var appState = FindAppState(root);
            if (appState is null) return false;

            if (appState.TryGetValue("StateFlags", out var sfNode))
            {
                var sfStr = sfNode.ValueType == KVValueType.String ? (string)sfNode : Convert.ToString(sfNode, CultureInfo.InvariantCulture) ?? "";
                int.TryParse(sfStr.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out stateFlags);
            }

            if (appState.TryGetValue("installdir", out var dirNode) && dirNode.ValueType == KVValueType.String)
            {
                installDir = (string)dirNode;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static KVObject? FindAppState(KVObject? node)
    {
        if (node is null) return null;
        if (node.TryGetValue("StateFlags", out _) || node.TryGetValue("installdir", out _)) return node;

        foreach (var kvp in node)
        {
            var found = FindAppState(kvp.Value);
            if (found is not null) return found;
        }

        return null;
    }
}
