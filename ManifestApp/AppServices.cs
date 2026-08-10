using ManifestApp.Services;

namespace ManifestApp;

/// <summary>Process-wide services available to WinUI pages.</summary>
internal sealed class AppServices
{
    internal AppServices(HttpClient http)
    {
        Http = http;
        SettingsStore = new();
        PathsResolver = new(SettingsStore);
        SteamStoreSearch = new ManifestApp.Core.SteamStoreSearchClient(Http);
        SteamLibrary = new ManifestApp.Core.SteamLibraryService(SettingsStore);
        SteamStoreDetails = new ManifestApp.Core.SteamStoreAppDetailsClient(Http);
        ArtworkCache = new(Http);
        InstalledRecords = new();
        OpenSteamApi   = new(Http, SettingsStore);
        Activation   = new(Http, SettingsStore);
        Pairing      = new ManifestApp.Core.PairingClient(Http, SettingsStore);
        ZipInstaller = new(InstalledRecords, PathsResolver);
        SteamToolsLocator = new(SettingsStore, PathsResolver);
        DiscordPresence = new DiscordPresenceService(SettingsStore);
        UpdateChecker   = new UpdateService(Http);
        AdminReporter   = new AdminReporterService(Http, SettingsStore);
    }

    internal HttpClient Http { get; }

    internal ManifestApp.Core.SettingsStore SettingsStore { get; }

    internal ManifestApp.Core.SteamPathsResolver PathsResolver { get; }

    internal ManifestApp.Core.SteamStoreSearchClient SteamStoreSearch { get; }

    internal ManifestApp.Core.SteamStoreAppDetailsClient SteamStoreDetails { get; }

    internal ManifestApp.Core.SteamLibraryService SteamLibrary { get; }

    internal ManifestApp.Core.SteamArtworkCache ArtworkCache { get; }

    internal ManifestApp.Core.InstalledManifestStore InstalledRecords { get; }

    internal ManifestApp.Core.OpenSteamApiClient  OpenSteamApi  { get; }
    internal ManifestApp.Core.ActivationClient  Activation  { get; }
    internal ManifestApp.Core.PairingClient Pairing { get; }

    internal ManifestApp.Core.ZipManifestInstaller ZipInstaller { get; }

    internal ManifestApp.Core.SteamToolsLocator SteamToolsLocator { get; }

    internal DiscordPresenceService DiscordPresence { get; }

    internal UpdateService UpdateChecker { get; }

    /// <summary>
    /// Populated by the background startup update check in <see cref="App.OnLaunched"/>.
    /// Non-null and <see cref="Services.UpdateResult.IsUpdateAvailable"/> only when an update was found.
    /// </summary>
    internal UpdateResult? StartupUpdateResult { get; set; }

    internal AdminReporterService AdminReporter { get; }
}
