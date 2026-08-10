namespace ManifestApp.Core;

/// <summary>Validates that a relative path resolves under a root directory (ZipSlip guard).</summary>
public static class SafePathUnderRoot
{
    /// <summary>
    /// Combines <paramref name="rootDir"/> with a relative path and returns the full path
    /// when it stays under the root. Returns false for traversal attempts.
    /// </summary>
    public static bool TryResolve(string rootDir, string relativePath, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(rootDir) || string.IsNullOrWhiteSpace(relativePath))
            return false;

        var root = Path.GetFullPath(rootDir);
        var combined = Path.GetFullPath(Path.Combine(root, relativePath));

        if (string.Equals(combined, root, StringComparison.OrdinalIgnoreCase))
        {
            fullPath = combined;
            return true;
        }

        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        if (!combined.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        fullPath = combined;
        return true;
    }
}
