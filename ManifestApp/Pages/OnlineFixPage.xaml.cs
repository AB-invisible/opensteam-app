using System.Collections.ObjectModel;
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
    private List<OnlineFixItem>? _allFixes;
    private CancellationTokenSource? _cts;

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
        base.OnNavigatedFrom(e);
    }

    private async Task LoadFixesAsync()
    {
        _cts?.Cancel();
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

            _allFixes = list;
            ApplyFilter();
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
        var filtered = _allFixes;

        if (!string.IsNullOrEmpty(query))
        {
            filtered = _allFixes.Where(f =>
                f.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                f.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            ).ToList();
        }

        FixesGrid.ItemsSource = filtered;

        if (filtered.Count == 0)
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
        var sel = FixesGrid.SelectedItem as OnlineFixItem;
        if (sel == null)
        {
            DetailPanel.Visibility = Visibility.Collapsed;
            return;
        }

        DetailTitle.Text = sel.Title;
        DetailName.Text = $"Name: {sel.Name}";
        DetailVersionText.Text = !string.IsNullOrEmpty(sel.Version) ? sel.Version : "N/A";
        DetailSizeText.Text = !string.IsNullOrEmpty(sel.Size) ? sel.Size : "N/A";

        DownloadProgressPanel.Visibility = Visibility.Collapsed;
        StatusInfoBar.IsOpen = false;
        DownloadButton.IsEnabled = true;
        DetailPanel.Visibility = Visibility.Visible;
    }

    private async void DownloadButton_Click(object sender, RoutedEventArgs e)
    {
        var sel = FixesGrid.SelectedItem as OnlineFixItem;
        if (sel == null) return;

        if (!OpenSteamApiKeyStore.TryRetrieve(out var apiKey) || string.IsNullOrWhiteSpace(apiKey))
        {
            ShowStatus(InfoBarSeverity.Error, "Authentication Error", "No API key configured. Check Settings.");
            return;
        }

        // Configure save file picker dynamically based on the server's extension (often .rar instead of .zip)
        var ext = ".zip";
        var filterLabel = "ZIP Archive";
        if (!string.IsNullOrWhiteSpace(sel.FileName))
        {
            var detectedExt = System.IO.Path.GetExtension(sel.FileName);
            if (!string.IsNullOrWhiteSpace(detectedExt))
            {
                ext = detectedExt.ToLowerInvariant();
                filterLabel = ext == ".rar" ? "RAR Archive" : $"{ext.TrimStart('.').ToUpperInvariant()} Archive";
            }
        }

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.Downloads,
            SuggestedFileName = !string.IsNullOrWhiteSpace(sel.FileName) ? sel.FileName : $"{sel.Name}{ext}"
        };
        picker.FileTypeChoices.Add(filterLabel, new List<string> { ext });

        // Bind Window Handle to picker (required in WinUI 3)
        var hwnd = WindowInterop.GetWindowHandle(TypedApp.MainShell);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSaveFileAsync();
        if (file == null) return;

        // Start Download
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
            fileStream.SetLength(0); // Truncate existing file if any

            await TypedApp.Svcs.OpenSteamApi.DownloadOnlineFixAsync(
                apiKey.Trim(),
                sel.Name,
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
