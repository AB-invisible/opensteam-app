namespace ManifestApp.Pages;

/// <summary>Parameter for navigating from the search grid into the per-game details page.</summary>
public sealed record GameDetailsNavigationArgs(uint AppId, string DisplayName, bool IsConfigured);
