using System.Collections.ObjectModel;
using ManifestApp.Core;
using ManifestApp.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace ManifestApp.Pages;

public sealed partial class GameRequestsPage : Page
{
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();

    private App TypedApp => (App)Application.Current;

    public GameRequestsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // If navigated here from a failed install, pre-populate the selected game.
        if (e.Parameter is GameRequestsNavigationArgs { AppId: not null } args)
        {
            var vm = new GameRowVm(args.AppId.Value, args.GameName ?? $"Steam App {args.AppId.Value}");
            _ = LoadThumbnailAsync(vm);
            RequestGamesGrid.ItemsSource = new ObservableCollection<GameRowVm> { vm };
            RequestGamesGrid.SelectedItem = vm;
            RequestSearchBox.Text = vm.DisplayName;
        }
    }

    // ── Search ────────────────────────────────────────────────────────────────

    private async void RequestSearch_QuerySubmitted(AutoSuggestBox sender,
        AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var query = sender.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(query)) return;

        RequestDetailPanel.Visibility = Visibility.Collapsed;
        RequestResultBar.IsOpen       = false;
        SearchStatusText.Visibility   = Visibility.Collapsed;

        // Direct App ID entry
        if (uint.TryParse(query,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var directId) && directId > 0)
        {
            var vm = new GameRowVm(directId, $"Steam App {directId}");
            _ = LoadThumbnailAsync(vm);
            RequestGamesGrid.ItemsSource = new ObservableCollection<GameRowVm> { vm };
            RequestGamesGrid.SelectedItem = vm;
            return;
        }

        SearchStatusText.Text       = $"Searching for \"{query}\"…";
        SearchStatusText.Visibility = Visibility.Visible;

        try
        {
            var hits = await TypedApp.Svcs.SteamStoreSearch
                .SearchAppsAsync(query).ConfigureAwait(true);

            SearchStatusText.Visibility = Visibility.Collapsed;

            if (hits.Count == 0)
            {
                SearchStatusText.Text       = $"No results for \"{query}\". Try a different name or paste a numeric App ID.";
                SearchStatusText.Visibility = Visibility.Visible;
                RequestGamesGrid.ItemsSource = new ObservableCollection<GameRowVm>();
                return;
            }

            var view = new ObservableCollection<GameRowVm>();
            foreach (var hit in hits)
            {
                var vm = new GameRowVm(hit.AppId, hit.Name);
                ApplyCapsule(vm, hit);
                view.Add(vm);
            }
            RequestGamesGrid.ItemsSource = view;
        }
        catch (Exception ex)
        {
            SearchStatusText.Text       = $"Search failed: {ex.Message}";
            SearchStatusText.Visibility = Visibility.Visible;
            RequestGamesGrid.ItemsSource = new ObservableCollection<GameRowVm>();
        }
    }

    // ── Grid selection ────────────────────────────────────────────────────────

    private void RequestGamesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var sel = RequestGamesGrid.SelectedItem as GameRowVm;
        if (sel is null)
        {
            RequestDetailPanel.Visibility = Visibility.Collapsed;
            return;
        }

        RequestDetailTitle.Text = sel.DisplayName;
        RequestDetailAppId.Text = $"App ID {sel.AppId:N0}";
        ReasonBox.Text          = "";
        RequestResultBar.IsOpen = false;
        SendRequestButton.IsEnabled   = true;
        RequestDetailPanel.Visibility = Visibility.Visible;
    }

    // ── Send request ──────────────────────────────────────────────────────────

    private async void SendRequest_Click(object sender, RoutedEventArgs e)
    {
        var sel = RequestGamesGrid.SelectedItem as GameRowVm;
        if (sel is null) return;

        if (!OpenSteamApiKeyStore.TryRetrieve(out var apiKey) || string.IsNullOrWhiteSpace(apiKey))
        {
            ShowResult(InfoBarSeverity.Error,
                "No API key",
                "Go to Settings → OpenSteam API to save your key first.");
            return;
        }

        SendRequestButton.IsEnabled = false;
        RequestResultBar.IsOpen     = false;

        try
        {
            var result = await TypedApp.Svcs.OpenSteamApi
                .RequestGameAsync(apiKey.Trim(), sel.AppId, ReasonBox.Text?.Trim(), CancellationToken.None)
                .ConfigureAwait(true);

            if (result.Sent)
            {
                var name = result.GameName ?? sel.DisplayName;
                ShowResult(InfoBarSeverity.Success,
                    "Request sent!",
                    $"\"{name}\" has been submitted to the OpenSteam fulfillment pipeline.");
                ReasonBox.Text = "";
            }
            else
            {
                ShowResult(InfoBarSeverity.Error,
                    "Request not sent",
                    result.ErrorMessage ?? "Unknown error — check your API key and daily request limit.");
            }
        }
        catch (Exception ex)
        {
            ShowResult(InfoBarSeverity.Error, "Error", ex.Message);
        }
        finally
        {
            SendRequestButton.IsEnabled = true;
        }
    }

    private void ShowResult(InfoBarSeverity severity, string title, string message)
    {
        RequestResultBar.Severity = severity;
        RequestResultBar.Title    = title;
        RequestResultBar.Message  = message;
        RequestResultBar.IsOpen   = true;
    }

    // ── Artwork helpers ───────────────────────────────────────────────────────

    private void ApplyCapsule(GameRowVm vm, ManifestApp.Core.SteamStoreAppHit hit)
    {
        try
        {
            if (!string.IsNullOrEmpty(hit.TinyImageHttpsUrl))
            {
                vm.AttachRemoteHttpsThumbnail(hit.TinyImageHttpsUrl);
                return;
            }
        }
        catch { /* fall through */ }

        _ = LoadThumbnailAsync(vm);
    }

    private async Task LoadThumbnailAsync(GameRowVm vm)
    {
        var path = await TypedApp.Svcs.ArtworkCache
            .GetCapsule184PathAsync(vm.AppId, CancellationToken.None)
            .ConfigureAwait(false);

        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

        _dispatcher.TryEnqueue(() => vm.AttachLocalThumbnail(path));
    }
}
