namespace ManifestApp.Core.Models;

/// <summary>User-adjustable paths and optional SteamTools override.</summary>
public sealed class AppSettings
{
    /// <summary>Folder containing steam.exe (no trailing slash required).</summary>
    public string? SteamInstallPathOverride { get; set; }

    public string? StPluginFolderOverride { get; set; }

    public string? DepotCacheFolderOverride { get; set; }

    /// <summary>Explicit path to SteamTools.exe.</summary>
    public string? SteamToolsExeOverride { get; set; }

    /// <summary>Set after the first-run setup wizard completes.</summary>
    public bool OnboardingCompleted { get; set; }

    /// <summary>Optional override; default https://opensteam.lol (no trailing slash).</summary>
    public string? OpenSteamApiBaseUrl { get; set; }

    /// <summary>When false, follows Windows theme. When true, forces dark Fluent theme.</summary>
    public bool PreferDarkUi { get; set; }

    /// <summary>When true, Discord Rich Presence IPC is not used (default false keeps presence on for existing settings files).</summary>
    public bool DiscordRichPresenceDisabled { get; set; }

    /// <summary>How trailer videos behave when a game details page opens: muted, paused, or sound.</summary>
    public string GameDetailsVideoStartupBehavior { get; set; } = "muted";

    /// <summary>Whether to automatically trigger Steam installation and extract online fix after generating manifest.</summary>
    public bool AutoInstallOnlineFix { get; set; }

    /// <summary>
    /// Base URL for the report endpoint. Leave empty or null to disable telemetry reporting entirely.
    /// When set, defaults to the main OpenSteam server at first use via Settings UI only.
    /// </summary>
    public string? AdminEndpointUrl { get; set; }

    /// <summary>
    /// Stable per-machine identifier generated on first launch and persisted here.
    /// Sent with every activation request so the server can track unique installs.
    /// </summary>
    public string? MachineId { get; set; }

    /// <summary>Timestamp of the user's last direct activity / interaction.</summary>
    public DateTime? LastUsedTime { get; set; }

    /// <summary>Timestamp of when the last user-inactivity toast notification was fired.</summary>
    public DateTime? LastInactivityNotificationTime { get; set; }
}
