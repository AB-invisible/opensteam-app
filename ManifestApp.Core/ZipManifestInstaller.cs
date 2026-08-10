using System.IO.Compression;
using System.Security.Cryptography;
using ManifestApp.Core.Models;

namespace ManifestApp.Core;

/// <summary>Unzips archives from OpenSteam and copies files into Steam config folders tracked for removal.</summary>
public sealed class ZipManifestInstaller(InstalledManifestStore store, SteamPathsResolver pathsResolver)
{
    /// <exception cref="IOException">Disk errors.</exception>
    public async Task<InstalledManifestRecord> InstallForAppAsync(
        uint appId,
        byte[] zipBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(zipBytes);

        zipBytes = ManifestLuaCleaner.CleanManifestZip(zipBytes);

        using var zipHash = SHA256.Create();
        var hashHex = Convert.ToHexString(zipHash.ComputeHash(zipBytes)).ToLowerInvariant();

        var plugin = pathsResolver.ResolveStPluginFolder();
        var depot = pathsResolver.ResolveDepotCacheFolder();

        if (string.IsNullOrWhiteSpace(plugin) || string.IsNullOrWhiteSpace(depot))
            throw new InvalidOperationException(
                "Steam plugin or depot cache folder cannot be resolved. Set paths in Settings.");

        Directory.CreateDirectory(plugin);
        Directory.CreateDirectory(depot);

        var workRoot = Path.Combine(Path.GetTempPath(), "OpenSteamApp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workRoot);

        try
        {
            await WriteAllBytesAtomicallyAsync(Path.Combine(workRoot, "manifest.zip"), zipBytes, cancellationToken)
                .ConfigureAwait(false);

            var extractRoot = Path.Combine(workRoot, "extract");
            Directory.CreateDirectory(extractRoot);
            ZipFile.ExtractToDirectory(Path.Combine(workRoot, "manifest.zip"), extractRoot, overwriteFiles: true);

            var deployed = new List<string>();

            foreach (var filePath in Directory.EnumerateFiles(extractRoot, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var ext = Path.GetExtension(filePath);
                string targetDir;

                if (string.Equals(ext, ".lua", StringComparison.OrdinalIgnoreCase))
                    targetDir = plugin!;
                else if (string.Equals(ext, ".manifest", StringComparison.OrdinalIgnoreCase))
                    targetDir = depot!;
                else
                    continue;

                var dest = Path.Combine(targetDir, Path.GetFileName(filePath));
                File.Copy(filePath, dest, overwrite: true);
                deployed.Add(Path.GetFullPath(dest));
            }

            if (deployed.Count == 0)
                throw new InvalidOperationException(
                    "The downloaded ZIP did not contain any .lua or .manifest files.");

            var record = new InstalledManifestRecord
            {
                AppId = appId,
                InstalledUtc = DateTimeOffset.UtcNow,
                ZipSha256Hex = hashHex,
                DeployedAbsolutePaths = deployed,
            };

            store.Upsert(record);
            return record;
        }
        finally
        {
            DeleteDirectoryQuietly(workRoot);
        }
    }

    public void RemoveForApp(uint appId)
    {
        var existing = store.FindByAppId(appId);
        if (existing == null)
            return;

        foreach (var abs in existing.DeployedAbsolutePaths)
        {
            try
            {
                if (File.Exists(abs))
                    File.Delete(abs);
            }
            catch
            {
                // Best effort
            }
        }

        store.Remove(appId);
    }

    private static async Task WriteAllBytesAtomicallyAsync(string path, byte[] data, CancellationToken ct)
    {
        var folder = Path.GetDirectoryName(path) ?? "";
        Directory.CreateDirectory(folder);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllBytesAsync(temp, data, ct).ConfigureAwait(false);
        if (File.Exists(path))
            File.Delete(path);
        File.Move(temp, path);
    }

    private static void DeleteDirectoryQuietly(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch
        {
            /* ignore cleanup failures */
        }
    }
}
