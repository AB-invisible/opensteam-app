namespace ManifestApp.Core.Models;

/// <summary>One Steam library title discovered from disk.</summary>
public sealed class SteamGameInfo : IComparable<SteamGameInfo>
{
    public required uint AppId { get; init; }

    public required string DisplayName { get; init; }

    public int CompareTo(SteamGameInfo? other)
    {
        if (other is null) return 1;
        return string.Compare(DisplayName, other.DisplayName, StringComparison.OrdinalIgnoreCase);
    }
}
