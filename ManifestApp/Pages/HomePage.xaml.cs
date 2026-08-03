using System.Collections.ObjectModel;
using ManifestApp.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Dispatching;

namespace ManifestApp.Pages;

public sealed partial class HomePage : Page
{
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();

    private bool _suppressSourceComboChanged;

    private App TypedApp => (App)Application.Current;

    public HomePage()
    {
        InitializeComponent();
        Loaded += (_, _) => StatusPulse.Begin();
    }

    /// <summary>Offline / API-failure fallback list — used only when Steam's featured endpoint is unreachable.</summary>
    private static readonly (uint AppId, string Name)[] PopularSteamGames =
    {
        (730,     "Counter-Strike 2"),
        (570,     "Dota 2"),
        (271590,  "Grand Theft Auto V"),
        (1086940, "Baldur's Gate 3"),
        (1245620, "Elden Ring"),
        (1091500, "Cyberpunk 2077"),
        (252490,  "Rust"),
        (578080,  "PUBG: BATTLEGROUNDS"),
        (1888930, "The Last of Us Part I"),
        (2358720, "Black Myth: Wukong"),
        (292030,  "The Witcher 3: Wild Hunt"),
        (1174180, "Red Dead Redemption 2"),
    };

    private void LoadTrendingGames() => _ = LoadTrendingGamesAsync();

    private async Task LoadTrendingGamesAsync()
    {
        ResultsHeading.Text = "Loading trending games…";
        GamesGrid.ItemsSource = new ObservableCollection<GameRowVm>();

        try
        {
            using var resp = await TypedApp.Svcs.Http
                .GetAsync("https://store.steampowered.com/api/featuredcategories/?cc=us&l=en")
                .ConfigureAwait(true);
            resp.EnsureSuccessStatusCode();

            await using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(true);
            using var doc = await System.Text.Json.JsonDocument
                .ParseAsync(stream).ConfigureAwait(true);

            var view = new ObservableCollection<GameRowVm>();
            if (doc.RootElement.TryGetProperty("top_sellers", out var top) &&
                top.TryGetProperty("items", out var items) &&
                items.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (!item.TryGetProperty("id", out var idEl)) continue;
                    if (!idEl.TryGetUInt32(out var appId) || appId == 0) continue;

                    var name = item.TryGetProperty("name", out var n)
                        ? (n.GetString() ?? $"Steam App {appId}")
                        : $"Steam App {appId}";

                    var vm = new GameRowVm(appId, name);
                    HydrateTrackedState(vm);

                    // Prefer header_image (460x215, ~2.14:1) — fits our 224x105 (~2.13:1) tile
                    // almost pixel-perfect with UniformToFill, no embedded title text gets cut.
                    // small_capsule_image (184x69, 2.67:1) is wider so its sides crop.
                    var capsuleUrl =
                        ReadJsonStringProp(item, "header_image") ??
                        ReadJsonStringProp(item, "large_capsule_image") ??
                        ReadJsonStringProp(item, "small_capsule_image");

                    if (!string.IsNullOrEmpty(capsuleUrl))
                        vm.AttachRemoteHttpsThumbnail(capsuleUrl!);
                    else
                        _ = FallbackCapsule184Async(vm).ConfigureAwait(false);

                    view.Add(vm);
                    if (view.Count >= 18) break;
                }
            }

            if (view.Count == 0)
            {
                LoadPopularGames();
                return;
            }

            GamesGrid.ItemsSource  = view;
            ResultsHeading.Text    = $"TRENDING ON STEAM · {view.Count:N0} TITLES";
        }
        catch
        {
            LoadPopularGames();
        }
    }

    private void LoadPopularGames()
    {
        var view = new ObservableCollection<GameRowVm>();
        foreach (var (appId, name) in PopularSteamGames)
        {
            var vm = new GameRowVm(appId, name);
            HydrateTrackedState(vm);
            _ = FallbackCapsule184Async(vm).ConfigureAwait(false);
            view.Add(vm);
        }

        GamesGrid.ItemsSource = view;
        ResultsHeading.Text   = "POPULAR ON STEAM";
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        UpdateSteamBanner();

        // When returning from GameDetailsPage, keep current grid state — just refresh tracked flags
        // so any installs/removes are reflected on tiles. If back-navigation lands on a Home mode
        // that doesn't match the current SourceCombo (multi-step back through nav-tab switches),
        // fall through to a full reload so the UI matches the args.
        if (e.NavigationMode == NavigationMode.Back && !HomeArgsDifferFromCurrentSource(e))
        {
            RefreshAllTrackedState();
            SyncDiscordPresenceFromHome();
            return;
        }

        try
        {
            RunHomeNavigatedToCore(e);
        }
        finally
        {
            SyncDiscordPresenceFromHome();
        }
    }

    private void RunHomeNavigatedToCore(NavigationEventArgs e)
    {
        if (e.Parameter is HomeNavigationArgs { Mode: HomeLandingMode.ManifestLibrary })
        {
            _suppressSourceComboChanged = true;
            SourceCombo.SelectedIndex = 2;
            _suppressSourceComboChanged = false;
            ApplyTrackedLibrarySourceUiOnly();
            LoadTrackedLibrary();
            RefreshHomeIntroText();
            return;
        }

        if (e.Parameter is HomeNavigationArgs { Mode: HomeLandingMode.StoreSearch })
        {
            _suppressSourceComboChanged = true;
            SourceCombo.SelectedIndex = 0;
            _suppressSourceComboChanged = false;
            ApplyStoreSourceUiOnly();
            LoadTrendingGames();
            RefreshHomeIntroText();
            return;
        }

        if (SourceCombo.SelectedIndex == 1)
        {
            ApplyInstalledSourceUiOnly();
            LoadInstalledGames();
            RefreshHomeIntroText();
            return;
        }

        if (SourceCombo.SelectedIndex == 2)
        {
            ApplyTrackedLibrarySourceUiOnly();
            LoadTrackedLibrary();
            RefreshHomeIntroText();
            return;
        }

        ApplyStoreSourceUiOnly();
        LoadTrendingGames();
        RefreshHomeIntroText();
    }

    private bool HomeArgsDifferFromCurrentSource(NavigationEventArgs e)
    {
        if (e.Parameter is not HomeNavigationArgs ha) return false;
        return ha.Mode switch
        {
            HomeLandingMode.ManifestLibrary => SourceCombo.SelectedIndex != 2,
            HomeLandingMode.StoreSearch     => SourceCombo.SelectedIndex != 0,
            _                               => false,
        };
    }

    private void RefreshAllTrackedState()
    {
        if (GamesGrid.ItemsSource is ObservableCollection<GameRowVm> view)
        {
            foreach (var vm in view)
                HydrateTrackedState(vm);
        }
    }

    private void SyncDiscordPresenceFromHome()
    {
        try { TypedApp.Svcs.DiscordPresence.NotifyHomeSource(SourceCombo.SelectedIndex); }
        catch { /* optional RPC only */ }
    }

    private void UpdateSteamBanner()
    {
        var steamKnown = TypedApp.Svcs.PathsResolver.ResolveSteamInstall() != null;
        SteamPathBanner.IsOpen = !steamKnown;
        SteamPathBanner.Visibility = steamKnown ? Visibility.Collapsed : Visibility.Visible;
    }

    private void FocusSearch_Click(object sender, RoutedEventArgs e)
    {
        if (SourceCombo.SelectedIndex != 0)
        {
            _suppressSourceComboChanged = true;
            SourceCombo.SelectedIndex = 0;
            _suppressSourceComboChanged = false;
            ApplyStoreSourceUiOnly();
        }
        SearchBox.Focus(FocusState.Programmatic);
    }

    private async void SearchBox_QuerySubmitted(AutoSuggestBox sender,
        AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        _ = args;
        await RunSteamStoreSearchAsync(sender.Text);
    }

    private async Task RunSteamStoreSearchAsync(string? rawQuery)
    {
        var query = rawQuery?.Trim() ?? "";
        if (SourceCombo.SelectedIndex != 0)
        {
            _suppressSourceComboChanged = true;
            SourceCombo.SelectedIndex = 0;
            _suppressSourceComboChanged = false;
            ApplyStoreSourceUiOnly();
        }

        UpdateSteamBanner();

        if (query.Length == 0)
        {
            ResultsHeading.Text = "ENTER A SEARCH TERM";
            GamesGrid.ItemsSource = new ObservableCollection<GameRowVm>();
            SyncDiscordPresenceFromHome();
            return;
        }

        // Direct App ID entry — numeric query skips store search
        if (uint.TryParse(query, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var directId) && directId > 0)
        {
            var directVm = new GameRowVm(directId, $"Steam App {directId}");
            HydrateTrackedState(directVm);
            _ = FallbackCapsule184Async(directVm).ConfigureAwait(false);

            GamesGrid.ItemsSource = new ObservableCollection<GameRowVm> { directVm };
            ResultsHeading.Text = $"DIRECT APP ID · {directId}";
            SyncDiscordPresenceFromHome();
            return;
        }

        ResultsHeading.Text = $"SEARCHING STEAM STORE · \"{query}\"";
        TypedApp.Svcs.DiscordPresence.NotifySearchingStore(TruncateForDiscordPresence(query));

        try
        {
            var hits = await TypedApp.Svcs.SteamStoreSearch.SearchAppsAsync(query).ConfigureAwait(true);

            if (hits.Count == 0)
            {
                ResultsHeading.Text = $"NO RESULTS FOR \"{query}\"";
                GamesGrid.ItemsSource = new ObservableCollection<GameRowVm>();
                return;
            }

            var view = new ObservableCollection<GameRowVm>();
            foreach (var hit in hits)
            {
                var vm = new GameRowVm(hit.AppId, hit.Name);
                HydrateTrackedState(vm);
                ApplyCapsule(vm, hit);
                view.Add(vm);
            }

            GamesGrid.ItemsSource = view;
            _ = TypedApp.Svcs.AdminReporter.ReportSearchAsync(query);

            ResultsHeading.Text = $"{hits.Count:N0} RESULTS · \"{query}\"";
        }
        catch (Exception ex)
        {
            ResultsHeading.Text = $"STORE SEARCH FAILED · {ex.Message}";
        }
        finally
        {
            SyncDiscordPresenceFromHome();
        }
    }

    private static string TruncateForDiscordPresence(string s, int max = 96)
    {
        if (s.Length <= max) return s;
        return string.Concat(s.AsSpan(0, max - 1), "…");
    }

    private static string? ReadJsonStringProp(System.Text.Json.JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var el) ||
            el.ValueKind != System.Text.Json.JsonValueKind.String)
            return null;
        var s = el.GetString();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private void HydrateTrackedState(GameRowVm rowVm)
    {
        var rec = TypedApp.Svcs.InstalledRecords.FindByAppId(rowVm.AppId);
        rowVm.IsConfigured = rec != null;
        if (rec != null)
            rowVm.AttachTrackingMetadata(rec);
        else
            rowVm.ClearTrackingMetadata();
    }

    private void SourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Guard against firing before the page is fully initialized (XAML parser
        // may raise SelectionChanged before SearchBox / RefreshInstalledButton exist).
        if (_suppressSourceComboChanged || SourceCombo.SelectedItem is null)
            return;
        if (SearchBox is null || RefreshInstalledButton is null)
            return;

        try
        {
            if (SourceCombo.SelectedIndex == 1)
            {
                ApplyInstalledSourceUiOnly();
                LoadInstalledGames();
                RefreshHomeIntroText();
                return;
            }

            if (SourceCombo.SelectedIndex == 2)
            {
                ApplyTrackedLibrarySourceUiOnly();
                LoadTrackedLibrary();
                RefreshHomeIntroText();
                return;
            }

            ApplyStoreSourceUiOnly();
            LoadTrendingGames();
            RefreshHomeIntroText();
        }
        finally
        {
            SyncDiscordPresenceFromHome();
        }
    }

    private void RefreshHomeIntroText()
    {
        HomeIntroText.Text = SourceCombo.SelectedIndex switch
        {
            1 => "Pick an installed Steam game to install or manage its OpenSteam manifests.",
            2 => "Titles you installed through OpenSteam appear here (newest first). Open one to see files or remove.",
            _ => "Click a game to open its page — screenshots, info, and manifest install live there.",
        };
    }

    private void ApplyStoreSourceUiOnly()
    {
        SearchBox.Visibility = Visibility.Visible;
        RefreshInstalledButton.Visibility = Visibility.Collapsed;
    }

    private void ApplyInstalledSourceUiOnly()
    {
        SearchBox.Visibility = Visibility.Collapsed;
        RefreshInstalledButton.Visibility = Visibility.Visible;
    }

    private void ApplyTrackedLibrarySourceUiOnly()
    {
        SearchBox.Visibility = Visibility.Collapsed;
        RefreshInstalledButton.Visibility = Visibility.Visible;
    }

    private void RefreshInstalled_Click(object sender, RoutedEventArgs e)
    {
        if (SourceCombo.SelectedIndex == 2)
            LoadTrackedLibrary();
        else
            LoadInstalledGames();
    }

    private void LoadInstalledGames()
    {
        UpdateSteamBanner();

        var steamKnown = TypedApp.Svcs.PathsResolver.ResolveSteamInstall() != null;
        if (!steamKnown)
        {
            GamesGrid.ItemsSource = new ObservableCollection<GameRowVm>();
            ResultsHeading.Text = "STEAM NOT DETECTED — SET IT IN SETTINGS";
            return;
        }

        var games = TypedApp.Svcs.SteamLibrary.ListGames();
        if (games.Count == 0)
        {
            GamesGrid.ItemsSource = new ObservableCollection<GameRowVm>();
            ResultsHeading.Text = "NO INSTALLED STEAM GAMES";
            return;
        }

        var view = new ObservableCollection<GameRowVm>();
        foreach (var g in games)
        {
            var vm = new GameRowVm(g.AppId, g.DisplayName);
            HydrateTrackedState(vm);
            _ = FallbackCapsule184Async(vm).ConfigureAwait(false);
            view.Add(vm);
        }

        GamesGrid.ItemsSource = view;
        ResultsHeading.Text = $"{games.Count:N0} INSTALLED STEAM GAMES";
    }

    private void LoadTrackedLibrary()
    {
        UpdateSteamBanner();

        var sorted = TypedApp.Svcs.InstalledRecords
            .Load()
            .Records.OrderByDescending(r => r.InstalledUtc)
            .ToList();

        if (sorted.Count == 0)
        {
            GamesGrid.ItemsSource = new ObservableCollection<GameRowVm>();
            ResultsHeading.Text = "LIBRARY EMPTY — INSTALL A MANIFEST TO START";
            return;
        }

        var nameByAppId = new Dictionary<uint, string>();
        foreach (var g in TypedApp.Svcs.SteamLibrary.ListGames())
            nameByAppId[g.AppId] = g.DisplayName;

        var view = new ObservableCollection<GameRowVm>();
        foreach (var r in sorted)
        {
            var display = nameByAppId.TryGetValue(r.AppId, out var n) && !string.IsNullOrWhiteSpace(n)
                ? n
                : $"Steam App ID {r.AppId}";

            var vm = new GameRowVm(r.AppId, display);
            vm.IsConfigured = true;
            vm.AttachTrackingMetadata(r);
            _ = FallbackCapsule184Async(vm).ConfigureAwait(false);
            view.Add(vm);
        }

        GamesGrid.ItemsSource = view;
        ResultsHeading.Text = $"{sorted.Count:N0} TRACKED INSTALLS";
    }

    private void ApplyCapsule(GameRowVm rowVm, ManifestApp.Core.SteamStoreAppHit hit)
    {
        try
        {
            if (!string.IsNullOrEmpty(hit.TinyImageHttpsUrl))
            {
                rowVm.AttachRemoteHttpsThumbnail(hit.TinyImageHttpsUrl);
                return;
            }
        }
        catch
        {
            /* fall through to CDN */
        }

        _ = FallbackCapsule184Async(rowVm).ConfigureAwait(false);
    }

    private async Task FallbackCapsule184Async(GameRowVm rowVm)
    {
        var path = await TypedApp.Svcs.ArtworkCache
            .GetCapsule184PathAsync(rowVm.AppId, CancellationToken.None)
            .ConfigureAwait(false);

        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return;

        var ok = _dispatcher.TryEnqueue(() => rowVm.AttachLocalThumbnail(path));
        _ = ok;
    }

    // ── Card click → details page ────────────────────────────────────────────

    private void GamesGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not GameRowVm vm) return;
        if (TypedApp.MainShell is MainWindow mw)
            mw.NavigateToGameDetails(vm.AppId, vm.DisplayName, vm.IsConfigured);
    }

    // ── Keyboard accelerators ────────────────────────────────────────────────

    private void FocusSearch_Accelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (SearchBox.Visibility == Visibility.Visible)
        {
            SearchBox.Focus(FocusState.Keyboard);
            args.Handled = true;
        }
    }
}
