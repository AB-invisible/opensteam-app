namespace ManifestApp.Pages;

public enum HomeLandingMode
{
    /// <summary>Steam Store workflow (combo index 0).</summary>
    StoreSearch,

    /// <summary>Tracked installs from installed_manifests.json (combo index 2).</summary>
    ManifestLibrary,
}

/// <summary>Passed when the shell navigates Home from <see cref="MainWindow"/> (Search vs Library).</summary>
public sealed record HomeNavigationArgs(HomeLandingMode Mode);
