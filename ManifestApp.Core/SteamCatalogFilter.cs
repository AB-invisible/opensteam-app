namespace ManifestApp.Core;

/// <summary>
/// Filters Steam hardware / utility apps that are not games from library and search grids.
/// </summary>
public static class SteamCatalogFilter
{
    private static readonly HashSet<uint> BlockedAppIds =
    [
        4165910, // Steam Machine
        1675200, // Steam Deck (hardware)
    ];

    public static bool IsExcluded(uint appId, string? displayName = null)
    {
        if (BlockedAppIds.Contains(appId))
            return true;

        if (string.IsNullOrWhiteSpace(displayName))
            return false;

        var name = displayName.Trim();
        return name.Equals("Steam Machine", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Steam Deck", StringComparison.OrdinalIgnoreCase);
    }
}
