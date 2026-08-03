using ManifestApp.Core.Models;

namespace ManifestApp.Core;

/// <summary>Matches online-fix catalog entries to a Steam game display name.</summary>
public static class OnlineFixMatcher
{
    public static OnlineFixItem? FindMatch(IEnumerable<OnlineFixItem> fixes, string displayName)
    {
        var searchName = AlphanumericOnly(displayName);
        if (string.IsNullOrEmpty(searchName)) return null;

        return fixes.FirstOrDefault(f =>
            AlphanumericOnly(f.Title).Contains(searchName, StringComparison.OrdinalIgnoreCase)
            || AlphanumericOnly(f.Name).Contains(searchName, StringComparison.OrdinalIgnoreCase));
    }

    private static string AlphanumericOnly(string value) =>
        new string(value.Where(char.IsLetterOrDigit).ToArray());
}
