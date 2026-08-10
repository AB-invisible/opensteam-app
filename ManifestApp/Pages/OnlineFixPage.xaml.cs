using System.Collections.ObjectModel;
using System.Diagnostics;
using ManifestApp.Core;
using ManifestApp.Core.Models;
using ManifestApp.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;

namespace ManifestApp.Pages;

public sealed partial class OnlineFixPage : Page
{
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private List<OnlineFixRowVm>? _allFixes;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _thumbCts;

    private App TypedApp => (App)Application.Current;

    public OnlineFixPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        TypedApp.Svcs.DiscordPresence.NotifyBrowsingOnlineFixes();
        _ = LoadFixesAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _cts?.Cancel();
        _thumbCts?.Cancel();
        base.OnNavigatedFrom(e);
    }

    private async Task LoadFixesAsync()
    {
        _cts?.Cancel();
        _thumbCts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        DetailPanel.Visibility = Visibility.Collapsed;
        NoFixesText.Visibility = Visibility.Collapsed;
        FixesGrid.Visibility = Visibility.Collapsed;

        if (!OpenSteamApiKeyStore.TryRetrieve(out var apiKey) || string.IsNullOrWhiteSpace(apiKey))
        {
            NoFixesText.Text = "Please configure your OpenSteam API Key in Settings to load multiplayer fixes.";
            NoFixesText.Visibility = Visibility.Visible;
            return;
        }

        LoadingRing.IsActive = true;
        LoadingRing.Visibility = Visibility.Visible;

        try
        {
            var list = await TypedApp.Svcs.OpenSteamApi.GetOnlineFixesAsync(apiKey.Trim(), ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested) return;

            _allFixes = list
                .Select(item =>
                {
                    var title = OnlineFixDisplayHelper.ParseDisplayTitle(item.Name, item.FileName);
                    return new OnlineFixRowVm(item, title);
                })
                .OrderBy(vm => vm.DisplayTitle, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var row in _allFixes)
            {
                var thumb = !string.IsNullOrWhiteSpace(row.Source.HeaderImageUrl)
                    ? row.Source.HeaderImageUrl
                    : row.Source.ImageUrl;
                if (!string.IsNullOrWhiteSpace(thumb))
                    row.AttachRemoteThumbnail(thumb!);
            }

            ApplyFilter();
            _ = EnrichThumbnailsAsync(_allFixes, ct);
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested) return;
            NoFixesText.Text = $"Failed to load online fixes: {ex.Message}";
            NoFixesText.Visibility = Visibility.Visible;
        }
        finally
        {
            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Visibility.Collapsed;
        }
    }

    private async Task EnrichThumbnailsAsync(IReadOnlyList<OnlineFixRowVm> rows, CancellationToken ct)
    {
        _thumbCts?.Cancel();
        _thumbCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var thumbCt = _thumbCts.Token;

        using var gate = new SemaphoreSlim(4);
        var tasks = rows.Where(row => row.ThumbUri == OnlineFixRowVm.PlaceholderUri).Select(async row =>
        {
            await gate.WaitAsync(thumbCt).ConfigureAwait(false);
            try
            {
                var hits = await TypedApp.Svcs.SteamStoreSearch
                    .SearchAppsAsync(row.DisplayTitle, thumbCt)
                    .ConfigureAwait(false);
                if (thumbCt.IsCancellationRequested || hits.Count == 0)
                    return;

                var hit = hits.FirstOrDefault(h =>
                    h.Name.Contains(row.DisplayTitle, StringComparison.OrdinalIgnoreCase) ||
                    row.DisplayTitle.Contains(h.Name, StringComparison.OrdinalIgnoreCase))
                    ?? hits[0];

                var imageUrl = !string.IsNullOrWhiteSpace(hit.TinyImageHttpsUrl)
                    ? hit.TinyImageHttpsUrl!
                    : OnlineFixDisplayHelper.HeaderImageUrl(hit.AppId);

                _dispatcher.TryEnqueue(() =>
                {
                    row.AttachRemoteThumbnail(imageUrl);
                    row.Source.SteamAppId ??= hit.AppId;
                });
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
            catch
            {
                // keep placeholder on lookup failure
            }
            finally
            {
                gate.Release();
            }
        });

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
    }

    private void ApplyFilter()
    {
        if (_allFixes == null || _allFixes.Count == 0)
        {
            NoFixesText.Text = "No fixes available in the database.";
            NoFixesText.Visibility = Visibility.Visible;
            FixesGrid.Visibility = Visibility.Collapsed;
            return;
        }

        var query = SearchBox.Text?.Trim() ?? "";
        IEnumerable<OnlineFixRowVm> filtered = _allFixes;

        if (!string.IsNullOrEmpty(query))
        {
            filtered = _allFixes.Where(f =>
                f.DisplayTitle.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                f.Source.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                f.Source.Title.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        var view = new ObservableCollection<OnlineFixRowVm>(filtered);
        FixesGrid.ItemsSource = view;

        if (!view.Any())
        {
            NoFixesText.Text = $"No matching fixes found for \"{query}\".";
            NoFixesText.Visibility = Visibility.Visible;
            FixesGrid.Visibility = Visibility.Collapsed;
        }
        else
        {
            NoFixesText.Visibility = Visibility.Collapsed;
            FixesGrid.Visibility = Visibility.Visible;
        }
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        ApplyFilter();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await LoadFixesAsync();
    }

    private void FixesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var sel = FixesGrid.SelectedItem as OnlineFixRowVm;
        if (sel == null)
        {
            DetailPanel.Visibility = Visibility.Collapsed;
            return;
        }

        DetailTitle.Text = sel.DisplayTitle;
        DetailName.Text = sel.Source.FileName ?? sel.Source.Name;
        DetailVersionText.Text = !string.IsNullOrEmpty(sel.Version) ? sel.Version : "N/A";
        DetailSizeText.Text = !string.IsNullOrEmpty(sel.Size) ? sel.Size : "N/A";

        DownloadProgressPanel.Visibility = Visibility.Collapsed;
        StatusInfoBar.IsOpen = false;
        DownloadButton.IsEnabled = true;
        InstallButton.IsEnabled = sel.Source.SteamAppId is > 0;
        DetailPanel.Visibility = Visibility.Visible;
        _ = EnsureInstallTargetAsync(sel);
    }

    private async Task EnsureInstallTargetAsync(OnlineFixRowVm row)
    {
        if (row.Source.SteamAppId is > 0)
        {
            _dispatcher.TryEnqueue(() => InstallButton.IsEnabled = true);
            return;
        }

        try
        {
            var hits = await TypedApp.Svcs.SteamStoreSearch
                .SearchAppsAsync(row.DisplayTitle, CancellationToken.None)
                .ConfigureAwait(false);
            var hit = hits.FirstOrDefault(h =>
                h.Name.Contains(row.DisplayTitle, StringComparison.OrdinalIgnoreCase) ||
                row.DisplayTitle.Contains(h.Name, StringComparison.OrdinalIgnoreCase))
                ?? hits.FirstOrDefault();
            if (hit == null) return;

            row.Source.SteamAppId = hit.AppId;
            _dispatcher.TryEnqueue(() => InstallButton.IsEnabled = true);
        }
        catch
        {
            // Install stays disabled when Steam lookup fails.
        }
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        var sel = FixesGrid.SelectedItem as OnlineFixRowVm;
        if (sel == null || sel.Source.SteamAppId is not { } appId || appId == 0) return;

        if (!OpenSteamApiKeyStore.TryRetrieve(out var apiKey) || string.IsNullOrWhiteSpace(apiKey))
        {
            ShowStatus(InfoBarSeverity.Error, "Authentication Error", "No API key configured. Check Settings.");
            return;
        }

        DownloadButton.IsEnabled = false;
        InstallButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        FixesGrid.IsEnabled = false;
        SearchBox.IsEnabled = false;
        DownloadProgressPanel.Visibility = Visibility.Visible;
        DownloadProgressBar.IsIndeterminate = true;
        DownloadProgressText.Text = "Checking game install folder…";
        StatusInfoBar.IsOpen = false;

        try
        {
            var monitor = new SteamGameMonitor(TypedApp.Svcs.SettingsStore);
            var gameInstallDir = monitor.TryGetInstalledGamePath(appId);
            if (string.IsNullOrEmpty(gameInstallDir))
            {
                var dlg = new ContentDialog
                {
                    Title = "Game not installed",
                    Content = $"{sel.DisplayTitle} is not installed in Steam yet.\n\nInstall it via Steam first, then press Install to Game again.",
                    PrimaryButtonText = "Install via Steam",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = XamlRoot!,
                };
                if (await DialogService.ShowAsync(dlg) != ContentDialogResult.Primary)
                    return;

                Process.Start(new ProcessStartInfo
                {
                    FileName = $"steam://install/{appId}",
                    UseShellExecute = true,
                });
                ShowStatus(
                    InfoBarSeverity.Informational,
                    "Install the game in Steam",
                    "After Steam finishes, return here and click Install to Game again.");
                return;
            }

            DownloadProgressBar.IsIndeterminate = false;
            DownloadProgressBar.Value = 0;
            DownloadProgressText.Text = $"Downloading fix for {sel.DisplayTitle}…";

            var progress = new Progress<double>(pct =>
            {
                _dispatcher.TryEnqueue(() =>
                {
                    DownloadProgressBar.Value = pct;
                    DownloadProgressText.Text = $"Downloading… {pct:0}%";
                });
            });

            using var ms = new MemoryStream();
            await TypedApp.Svcs.OpenSteamApi
                .DownloadOnlineFixAsync(apiKey.Trim(), sel.Source.ResolveDownloadName(), ms, CancellationToken.None, progress)
                .ConfigureAwait(true);
            ms.Position = 0;

            DownloadProgressBar.IsIndeterminate = true;
            DownloadProgressText.Text = "Extracting fix to game folder…";
            OnlineFixService.ExtractArchive(ms, gameInstallDir);

            ShowStatus(
                InfoBarSeverity.Success,
                "Install Complete",
                $"Online fix installed for {sel.DisplayTitle}.");
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Install Failed", ex.Message);
        }
        finally
        {
            DownloadButton.IsEnabled = true;
            InstallButton.IsEnabled = sel.Source.SteamAppId is > 0;
            RefreshButton.IsEnabled = true;
            FixesGrid.IsEnabled = true;
            SearchBox.IsEnabled = true;
            DownloadProgressPanel.Visibility = Visibility.Collapsed;
            DownloadProgressBar.IsIndeterminate = false;
        }
    }

    private async void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        var sel = FixesGrid.SelectedItem as OnlineFixRowVm;
        if (sel == null) return;

        if (!OpenSteamApiKeyStore.TryRetrieve(out var apiKey) || string.IsNullOrWhiteSpace(apiKey))
        {
            ShowStatus(InfoBarSeverity.Error, "Authentication Error", "No API key configured. Check Settings.");
            return;
        }

        var ext = ".zip";
        var filterLabel = "ZIP Archive";
        var fileName = sel.Source.FileName;
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            var detectedExt = Path.GetExtension(fileName);
            if (!string.IsNullOrWhiteSpace(detectedExt))
            {
                ext = detectedExt.ToLowerInvariant();
                filterLabel = ext == ".rar" ? "RAR Archive" : $"{ext.TrimStart('.').ToUpperInvariant()} Archive";
            }
        }

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.Downloads,
            SuggestedFileName = !string.IsNullOrWhiteSpace(fileName) ? fileName : $"{sel.Source.Name}{ext}"
        };
        picker.FileTypeChoices.Add(filterLabel, new List<string> { ext });

        var hwnd = WindowInterop.GetWindowHandle(TypedApp.MainShell);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSaveFileAsync();
        if (file == null) return;

        DownloadButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        FixesGrid.IsEnabled = false;
        SearchBox.IsEnabled = false;
        DownloadProgressPanel.Visibility = Visibility.Visible;
        DownloadProgressBar.Value = 0;
        DownloadProgressText.Text = "Starting download...";
        StatusInfoBar.IsOpen = false;

        using var downloadCts = new CancellationTokenSource();
        var progress = new Progress<double>(pct =>
        {
            _dispatcher.TryEnqueue(() =>
            {
                DownloadProgressBar.Value = pct;
                DownloadProgressText.Text = $"Downloading... {pct:0}%";
            });
        });

        try
        {
            using var fileStream = await file.OpenStreamForWriteAsync();
            fileStream.SetLength(0);

            await TypedApp.Svcs.OpenSteamApi.DownloadOnlineFixAsync(
                apiKey.Trim(),
                sel.Source.ResolveDownloadName(),
                fileStream,
                downloadCts.Token,
                progress
            ).ConfigureAwait(true);

            ShowStatus(InfoBarSeverity.Success, "Download Complete", $"Saved to: {file.Name}");
        }
        catch (OperationCanceledException)
        {
            ShowStatus(InfoBarSeverity.Warning, "Cancelled", "The download was cancelled.");
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Download Failed", ex.Message);
        }
        finally
        {
            DownloadButton.IsEnabled = true;
            RefreshButton.IsEnabled = true;
            FixesGrid.IsEnabled = true;
            SearchBox.IsEnabled = true;
            DownloadProgressPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        StatusInfoBar.Severity = severity;
        StatusInfoBar.Title = title;
        StatusInfoBar.Message = message;
        StatusInfoBar.IsOpen = true;
    }
}
