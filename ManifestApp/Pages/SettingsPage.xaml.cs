using ManifestApp.Services;
using ManifestApp;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace ManifestApp.Pages;

public sealed partial class SettingsPage : Page
{
    private bool    _suppressDarkThemeToggle;
    private bool    _suppressDiscordPresenceToggle;
    private bool    _suppressVideoStartupCombo;
    private bool    _suppressAutoInstallToggle;
    private string? _latestExeDownloadUrl;
    private string? _latestExeSha256Hex;
    private string? _latestUpdaterBatPath;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += SettingsPage_Loaded;
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        SyncDarkThemeToggleFromStore();
        SyncDiscordPresenceToggleFromStore();
        SyncVideoStartupComboFromStore();
        SyncAutoInstallToggleFromStore();
    }

    private void SyncDarkThemeToggleFromStore()
    {
        _suppressDarkThemeToggle = true;
        DarkThemeToggle.IsOn = TypedApp.Svcs.SettingsStore.Load().PreferDarkUi;
        _suppressDarkThemeToggle = false;
    }

    private void SyncDiscordPresenceToggleFromStore()
    {
        _suppressDiscordPresenceToggle = true;
        DiscordPresenceToggle.IsOn = TypedApp.Svcs.SettingsStore.Load().DiscordRichPresenceDisabled;
        _suppressDiscordPresenceToggle = false;
    }

    private void SyncVideoStartupComboFromStore()
    {
        var behavior = TypedApp.Svcs.SettingsStore.Load().GameDetailsVideoStartupBehavior;

        _suppressVideoStartupCombo = true;
        VideoStartupCombo.SelectedIndex = behavior switch
        {
            "paused" => 1,
            "sound" => 2,
            _ => 0,
        };
        _suppressVideoStartupCombo = false;
    }

    private void SyncAutoInstallToggleFromStore()
    {
        _suppressAutoInstallToggle = true;
        AutoInstallOnlineFixToggle.IsOn = TypedApp.Svcs.SettingsStore.Load().AutoInstallOnlineFix;
        _suppressAutoInstallToggle = false;
    }

    private App TypedApp => (App)Application.Current;

    private void SettingsPage_Loaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var s = TypedApp.Svcs.SettingsStore.Load();

        var paths = TypedApp.Svcs.PathsResolver;
        SteamPathBox.Text = s.SteamInstallPathOverride ?? paths.ResolveSteamInstall() ?? "";
        PluginPathBox.Text = s.StPluginFolderOverride ?? paths.ResolveStPluginFolder() ?? "";
        DepotPathBox.Text = s.DepotCacheFolderOverride ?? paths.ResolveDepotCacheFolder() ?? "";
        SteamToolsPathBox.Text = s.SteamToolsExeOverride ?? "";

        VersionText.Text = UpdateService.CurrentVersionString;

        PairingPanel.RefreshKeyStatus();

        // If the background startup check already found an update, pre-populate the InfoBar
        var startupResult = TypedApp.Svcs.StartupUpdateResult;
        if (startupResult?.IsUpdateAvailable == true && !UpdateInfoBar.IsOpen)
        {
            _latestExeDownloadUrl = startupResult.ExeDownloadUrl;
            _latestExeSha256Hex   = startupResult.ExeSha256Hex;
            UpdateInfoBar.Severity = InfoBarSeverity.Informational;
            UpdateInfoBar.Title    = $"Update available — v{startupResult.LatestVersion.ToString(3)}";
            UpdateInfoBar.Message  = $"You're on v{startupResult.CurrentVersion.ToString(3)}. Click Install to download and apply.";
            UpdateInfoBar.IsOpen   = true;
            if (_latestExeDownloadUrl is not null)
                InstallUpdateButton.Visibility = Visibility.Visible;
        }
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        InstallUpdateButton.Visibility = Visibility.Collapsed;
        UpdateInfoBar.IsOpen = false;
        _latestExeDownloadUrl = null;
        _latestExeSha256Hex   = null;
        _latestUpdaterBatPath = null;

        try
        {
            var result = await TypedApp.Svcs.UpdateChecker.CheckAsync();

            if (result is null)
            {
                UpdateInfoBar.Severity = InfoBarSeverity.Warning;
                UpdateInfoBar.Title   = "Could not check for updates";
                UpdateInfoBar.Message = "Make sure you're connected to the internet.";
            }
            else if (result.IsUpdateAvailable)
            {
                UpdateInfoBar.Severity = InfoBarSeverity.Informational;
                UpdateInfoBar.Title   = $"Update available — v{result.LatestVersion.ToString(3)}";
                UpdateInfoBar.Message = $"You're on v{result.CurrentVersion.ToString(3)}. Click Install to download and apply.";
                _latestExeDownloadUrl = result.ExeDownloadUrl;
                _latestExeSha256Hex   = result.ExeSha256Hex;
                InstallUpdateButton.Visibility = Visibility.Visible;
            }
            else
            {
                UpdateInfoBar.Severity = InfoBarSeverity.Success;
                UpdateInfoBar.Title   = "You're up to date";
                UpdateInfoBar.Message = $"v{result.CurrentVersion.ToString(3)} is the latest release.";
            }

            UpdateInfoBar.IsOpen = true;
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        // If already downloaded, apply immediately
        if (_latestUpdaterBatPath is not null)
        {
            UpdateService.ApplyUpdate(_latestUpdaterBatPath);
            return;
        }

        if (_latestExeDownloadUrl is null)
            return;

        InstallUpdateButton.IsEnabled   = false;
        CheckUpdatesButton.IsEnabled    = false;
        UpdateProgressBar.Value         = 0;
        UpdateProgressBar.Visibility    = Visibility.Visible;
        UpdateProgressText.Text         = "Downloading...";
        UpdateProgressText.Visibility   = Visibility.Visible;

        var progress = new Progress<double>(pct =>
        {
            UpdateProgressBar.Value = pct;
            UpdateProgressText.Text = $"Downloading... {pct:0}%";
        });

        var batPath = await TypedApp.Svcs.UpdateChecker.DownloadUpdateAsync(
            _latestExeDownloadUrl, progress, _latestExeSha256Hex);

        if (batPath is null)
        {
            UpdateProgressBar.Visibility  = Visibility.Collapsed;
            UpdateProgressText.Visibility = Visibility.Collapsed;
            UpdateInfoBar.Severity = InfoBarSeverity.Error;
            UpdateInfoBar.Title   = "Download failed";
            UpdateInfoBar.Message = "Could not download the update. Try again later.";
            UpdateInfoBar.IsOpen  = true;
            InstallUpdateButton.IsEnabled = true;
            CheckUpdatesButton.IsEnabled  = true;
            return;
        }

        _latestUpdaterBatPath           = batPath;
        UpdateProgressBar.Visibility    = Visibility.Collapsed;
        UpdateProgressText.Visibility   = Visibility.Collapsed;
        InstallUpdateButton.Content     = "Restart & apply update";
        InstallUpdateButton.IsEnabled   = true;
        CheckUpdatesButton.IsEnabled    = true;

        UpdateInfoBar.Severity = InfoBarSeverity.Success;
        UpdateInfoBar.Title   = "Download complete";
        UpdateInfoBar.Message = "Click 'Restart & apply update' to install.";
        UpdateInfoBar.IsOpen  = true;
    }

    private void DarkThemeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressDarkThemeToggle)
            return;

        var cur = TypedApp.Svcs.SettingsStore.Load();
        cur.PreferDarkUi = DarkThemeToggle.IsOn;
        TypedApp.Svcs.SettingsStore.Save(cur);

        if (TypedApp.MainShell is MainWindow mw)
            mw.ApplyThemeFromSettings();
    }

    private void DiscordPresenceToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressDiscordPresenceToggle)
            return;

        var cur = TypedApp.Svcs.SettingsStore.Load();
        cur.DiscordRichPresenceDisabled = DiscordPresenceToggle.IsOn;
        TypedApp.Svcs.SettingsStore.Save(cur);
        TypedApp.Svcs.DiscordPresence.Connect();
    }

    private void VideoStartupCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressVideoStartupCombo || VideoStartupCombo.SelectedItem is not ComboBoxItem item)
            return;

        var cur = TypedApp.Svcs.SettingsStore.Load();
        cur.GameDetailsVideoStartupBehavior = item.Tag?.ToString() switch
        {
            "paused" => "paused",
            "sound" => "sound",
            _ => "muted",
        };
        TypedApp.Svcs.SettingsStore.Save(cur);
    }

    private void AutoInstallOnlineFixToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressAutoInstallToggle)
            return;

        var cur = TypedApp.Svcs.SettingsStore.Load();
        cur.AutoInstallOnlineFix = AutoInstallOnlineFixToggle.IsOn;
        TypedApp.Svcs.SettingsStore.Save(cur);
    }

    private async void ResetPathsFromSteam_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var paths = TypedApp.Svcs.PathsResolver;

        var steam = SteamPathBox.Text?.Trim();
        if (string.IsNullOrEmpty(steam) || !System.IO.Directory.Exists(steam) || !System.IO.File.Exists(System.IO.Path.Combine(steam, "steam.exe")))
        {
            steam = paths.ResolveSteamInstall();
        }

        if (string.IsNullOrEmpty(steam))
        {
            SaveSettingsStatus.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
            SaveSettingsStatus.Text =
                "Could not detect Steam. Set Steam install folder above, tap Save Steam settings, then try again.";
            return;
        }

        SteamPathBox.Text = steam;
        PluginPathBox.Text = paths.DefaultStPluginFromSteamRoot(steam) ?? "";
        DepotPathBox.Text = paths.DefaultDepotCacheFromSteamRoot(steam) ?? "";
    }

    private void SaveSettings_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var cur = TypedApp.Svcs.SettingsStore.Load();
        cur.SteamInstallPathOverride = NormalizeOrNull(SteamPathBox.Text);
        cur.StPluginFolderOverride = NormalizeOrNull(PluginPathBox.Text);
        cur.DepotCacheFolderOverride = NormalizeOrNull(DepotPathBox.Text);
        cur.SteamToolsExeOverride = NormalizeOrNull(SteamToolsPathBox.Text);
        TypedApp.Svcs.SettingsStore.Save(cur);

        SaveSettingsStatus.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        SaveSettingsStatus.Text = "Steam paths saved.";
    }

    private async void BrowseSteam_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var path = await PickFolderAsync(PickerLocationId.ComputerFolder);
        if (!string.IsNullOrEmpty(path))
        {
            SteamPathBox.Text = path;
            var paths = TypedApp.Svcs.PathsResolver;
            PluginPathBox.Text = paths.DefaultStPluginFromSteamRoot(path) ?? "";
            DepotPathBox.Text = paths.DefaultDepotCacheFromSteamRoot(path) ?? "";
        }
    }

    private async void BrowsePlugin_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var path = await PickFolderAsync(PickerLocationId.ComputerFolder);
        if (!string.IsNullOrEmpty(path))
            PluginPathBox.Text = path;
    }

    private async void BrowseDepot_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var path = await PickFolderAsync(PickerLocationId.ComputerFolder);
        if (!string.IsNullOrEmpty(path))
            DepotPathBox.Text = path;
    }

    private async void BrowseSteamTools_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
        };

        picker.FileTypeFilter.Add(".exe");

        BindPickerInterop(picker);

        var file = await picker.PickSingleFileAsync();
        if (!string.IsNullOrEmpty(file?.Path))
            SteamToolsPathBox.Text = file.Path;
    }

    private async Task<string?> PickFolderAsync(PickerLocationId start)
    {
        var picker = new FolderPicker { SuggestedStartLocation = start };
        picker.FileTypeFilter.Add("*");
        BindPickerInterop(picker);
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    private void BindPickerInterop(object picker)
    {
        var hwnd = WindowInterop.GetWindowHandle(TypedApp.MainShell);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
    }

    private static string? NormalizeOrNull(string s)
    {
        var t = s.Trim().Trim('"');
        return string.IsNullOrWhiteSpace(t) ? null : t;
    }
}
