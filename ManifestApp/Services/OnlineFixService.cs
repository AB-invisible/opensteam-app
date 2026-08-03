using ManifestApp.Core;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace ManifestApp.Services;

/// <summary>Downloads and extracts online-fix archives into a game install folder.</summary>
internal static class OnlineFixService
{
    internal static void ExtractArchive(Stream archiveStream, string gameInstallDir)
    {
        ArgumentNullException.ThrowIfNull(archiveStream);
        if (string.IsNullOrWhiteSpace(gameInstallDir))
            throw new ArgumentException("Game install directory is required.", nameof(gameInstallDir));

        var root = Path.GetFullPath(gameInstallDir);
        if (!Directory.Exists(root))
            Directory.CreateDirectory(root);

        using var archive = ArchiveFactory.OpenArchive(archiveStream);

        var allFileKeys = archive.Entries
            .Where(e => !e.IsDirectory && !string.IsNullOrEmpty(e.Key))
            .Select(e => e.Key!.Replace('\\', '/'))
            .ToList();

        string? commonRoot = null;
        if (allFileKeys.Count > 0)
        {
            var firstKeyParts = allFileKeys[0].Split('/');
            if (firstKeyParts.Length > 1)
            {
                var potentialRoot = firstKeyParts[0] + "/";
                if (allFileKeys.All(k => k.StartsWith(potentialRoot, StringComparison.Ordinal)))
                    commonRoot = potentialRoot;
            }
        }

        foreach (var entry in archive.Entries)
        {
            if (entry.IsDirectory || string.IsNullOrEmpty(entry.Key)) continue;

            var entryKey = entry.Key.Replace('\\', '/');
            if (commonRoot != null && entryKey.StartsWith(commonRoot, StringComparison.Ordinal))
                entryKey = entryKey[commonRoot.Length..];

            if (string.IsNullOrEmpty(entryKey)) continue;

            var relative = entryKey.Replace('/', Path.DirectorySeparatorChar);
            if (!SafePathUnderRoot.TryResolve(root, relative, out var targetPath))
            {
                throw new InvalidOperationException(
                    $"Archive entry escapes the game folder: {entry.Key}");
            }

            var targetDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                Directory.CreateDirectory(targetDir);

            entry.WriteToFile(targetPath, new ExtractionOptions { Overwrite = true });
        }
    }
}
