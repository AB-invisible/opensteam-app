using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Navigation;
using ManifestApp.Core;
using ManifestApp.Pages;
using ManifestApp.Services;
using Windows.UI;

namespace ManifestApp;

public sealed partial class MainWindow : Window
{
    private TrayIconService? _trayIcon;
    private bool             _splashStarted;
    private bool             _isExiting;
    private DispatcherTimer? _bgUpdateTimer;
    private DispatcherTimer? _heartbeatTimer;
    private DispatcherTimer? _statusPollTimer;
    private bool             _forceUpdateInProgress;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool MessageBeep(uint uType);

    public MainWindow()
    {
        InitializeComponent();

        // Minimize to tray on the X button; only close for real from the tray "Exit" menu
        AppWindow.Closing += OnAppWindowClosing;

        // Create system tray icon
        var uiQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _trayIcon = new TrayIconService(
            uiQueue,
            onOpen:         ShowFromTray,
            onCheckUpdates: OpenSettingsForUpdates,
            onExit: () =>
            {
                _isExiting = true;
                Close();
            });

        NavFrame.Navigated += NavFrame_OnNavigated;
        PairingGate.PairingCompleted += PairingGate_PairingCompleted;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(AppIconPaths.ResolveIconPath());

        RootChrome.ActualThemeChanged += (_, _) => SyncCaptionButtonColors();
        RootChrome.Loaded += (_, _) =>
        {
            SyncCaptionButtonColors();
            if (NavView.SettingsItem is NavigationViewItem settingsItem)
            {
                settingsItem.Content = "Settings";
            }
        };
        Activated += MainWindow_Activated;

        // Start background update checker
        _bgUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        _bgUpdateTimer.Tick += BgUpdateTimer_Tick;
        _bgUpdateTimer.Start();
    }

    private async void BgUpdateTimer_Tick(object? sender, object e)
    {
        var svcs = ((App)Application.Current).Svcs;
        
        // 1. Check for application updates
        var result = await svcs.UpdateChecker.CheckAsync();
        if (result?.IsUpdateAvailable == true && !GlobalUpdateNotification.IsOpen)
        {
            svcs.StartupUpdateResult = result;
            GlobalUpdateNotification.Message = $"Version {result.LatestVersion} is available.";
            GlobalUpdateNotification.IsOpen = true;

            // Trigger Windows Toast Notification for new update
            NotificationService.ShowToast(
                "New Update Available! 🚀",
                $"Version {result.LatestVersion} of OpenSteam App is ready to install."
            );

            try { MessageBeep(0x30); /* MB_ICONEXCLAMATION */ } catch { }
        }

        // 2. Perform inactivity check (1-day threshold)
        try
        {
            var settings = svcs.SettingsStore.Load();
            var lastUsed = settings.LastUsedTime ?? DateTime.UtcNow;
            var timeSinceLastUsed = DateTime.UtcNow - lastUsed;

            if (timeSinceLastUsed >= TimeSpan.FromDays(1))
            {
                var lastNotification = settings.LastInactivityNotificationTime;
                // Only notify if we haven't notified in the last 24 hours to prevent spam
                if (lastNotification == null || (DateTime.UtcNow - lastNotification.Value) >= TimeSpan.FromDays(1))
                {
                    NotificationService.ShowToast(
                        "We miss you! 🎮",
                        "You haven't checked OpenSteam App in a while. Open the app to view new game releases and manifests!"
                    );
                    settings.LastInactivityNotificationTime = DateTime.UtcNow;
                    svcs.SettingsStore.Save(settings);
                }
            }
        }
        catch { /* fail silent */ }
    }

    private async void GlobalUpdateNotification_InstallClick(object sender, RoutedEventArgs e)
    {
        GlobalUpdateNotification.IsOpen = false;
        var svcs = ((App)Application.Current).Svcs;
        var result = svcs.StartupUpdateResult;
        if (result?.ExeDownloadUrl != null)
        {
            var batPath = await svcs.UpdateChecker.DownloadUpdateAsync(
                result.ExeDownloadUrl,
                expectedSha256Hex: result.ExeSha256Hex);
            if (batPath != null)
                UpdateService.ApplyUpdate(batPath);
        }
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs e)
    {
        if (e.WindowActivationState != WindowActivationState.Deactivated)
        {
            UpdateUserActivity();
            SyncCaptionButtonColors();
        }

        if (!_splashStarted && e.WindowActivationState != WindowActivationState.Deactivated)
        {
            _splashStarted = true;
            _ = RunStartupSequenceAsync();
        }
    }

    /// <summary>
    /// Drives the splash overlay through its intro, the background update check, and the outro.
    /// Calls <see cref="NavigateShell"/> behind the splash so the real UI is ready when the splash fades.
    /// </summary>
    private async Task RunStartupSequenceAsync()
    {
        var svcs = ((App)Application.Current).Svcs;

        try
        {
            await Splash.PlayIntroAsync();

            // Step 0 — Check for updates
            Splash.SetStep(SplashOverlay.SplashStep.CheckUpdates, SplashOverlay.StepState.Active);
            var updateTask = svcs.UpdateChecker.CheckAsync();
            // Min display time so a fast network doesn't make the step feel like a flicker.
            await Task.WhenAll(updateTask, Task.Delay(700));
            var result = await updateTask;
            if (result?.IsUpdateAvailable == true)
            {
                svcs.StartupUpdateResult = result;

                if (!string.IsNullOrWhiteSpace(result.ExeDownloadUrl))
                {
                    Splash.SetStep(
                        SplashOverlay.SplashStep.CheckUpdates,
                        SplashOverlay.StepState.Active,
                        $"Downloading v{result.LatestVersion}…");

                    var batPath = await svcs.UpdateChecker.DownloadUpdateAsync(
                        result.ExeDownloadUrl,
                        null,
                        result.ExeSha256Hex);

                    if (batPath is not null)
                    {
                        Splash.SetStep(
                            SplashOverlay.SplashStep.CheckUpdates,
                            SplashOverlay.StepState.Done,
                            $"Installing v{result.LatestVersion}…");
                        await Task.Delay(400);
                        UpdateService.ApplyUpdate(batPath);
                        return;
                    }
                }
            }

            Splash.SetStep(SplashOverlay.SplashStep.CheckUpdates, SplashOverlay.StepState.Done,
                result?.IsUpdateAvailable == true
                    ? $"Update available  ·  v{result.LatestVersion}"
                    : "Up to date");
            await Task.Delay(220);

            // Step 1 — Preparing workspace (set up the real shell behind the splash)
            Splash.SetStep(SplashOverlay.SplashStep.Workspace, SplashOverlay.StepState.Active);
            await Task.Delay(280);
            NavigateShell();
            Splash.SetStep(SplashOverlay.SplashStep.Workspace, SplashOverlay.StepState.Done);
            await Task.Delay(180);

            // Step 2 — Ready
            Splash.SetStep(SplashOverlay.SplashStep.Ready, SplashOverlay.StepState.Active);
            await Task.Delay(220);
            Splash.SetStep(SplashOverlay.SplashStep.Ready, SplashOverlay.StepState.Done);
            await Task.Delay(260);

            await Splash.PlayOutroAsync();
        }
        catch
        {
            // Never let a splash hiccup block the app — best-effort fallback to the main shell.
            try { NavigateShell(); } catch { /* ignore */ }
        }
        finally
        {
            SplashHost.Visibility = Visibility.Collapsed;
        }
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs e)
    {
        if (!_isExiting)
        {
            e.Cancel = true;
            AppWindow.Hide();
            AppLogger.Log("Main window closed. Minimized to system tray.");
            return;
        }

        AppLogger.Log("Main window closing. Starting clean application exit.");

        // Real exit: clean up tray, Discord, and background timers
        _statusPollTimer?.Stop();
        _heartbeatTimer?.Stop();
        _bgUpdateTimer?.Stop();
        _trayIcon?.Dispose();
        _trayIcon = null;
        try { ((App)Application.Current).Svcs.DiscordPresence.Dispose(); } catch { /* ignore */ }
        AppLogger.Log("Cleanup complete. Exit process.");
    }

    private void ShowFromTray()
    {
        UpdateUserActivity();
        AppWindow.Show();
        Activate();
    }

    private void OpenSettingsForUpdates()
    {
        ShowFromTray();
        if (NavFrame.CurrentSourcePageType != typeof(SettingsPage))
            NavFrame.Navigate(typeof(SettingsPage));
        NavView.SelectedItem = NavView.SettingsItem;
    }

    internal void NavigateToSettings()
    {
        if (NavFrame.CurrentSourcePageType != typeof(SettingsPage))
            NavFrame.Navigate(typeof(SettingsPage));
        NavView.SelectedItem = NavView.SettingsItem;
    }

    /// <summary>
    /// Opens the per-game details page (Steam-style hero, screenshots, install actions).
    /// Called from the Home grid when the user clicks a tile.
    /// </summary>
    internal void NavigateToGameDetails(uint appId, string displayName, bool isConfigured)
    {
        NavFrame.Navigate(
            typeof(GameDetailsPage),
            new GameDetailsNavigationArgs(appId, displayName, isConfigured));
    }

    private void NavFrame_OnNavigated(object sender, NavigationEventArgs e)
    {
        if (e.Content is SettingsPage)
            ((App)Application.Current).Svcs.DiscordPresence.NotifySettingsPage();

        // Collapse the side nav to a compact icon rail on the game details page
        // (more room for the hero, screenshots, and trailer), restore on every other page.
        NavView.PaneDisplayMode = e.Content is GameDetailsPage
            ? NavigationViewPaneDisplayMode.LeftCompact
            : NavigationViewPaneDisplayMode.Left;
    }

    /// <summary>Sets root <see cref="FrameworkElement.RequestedTheme"/> from JSON <c>PreferDarkUi</c>.</summary>
    internal void ApplyThemeFromSettings()
    {
        var preferDark =
            ((App)Application.Current).Svcs.SettingsStore.Load().PreferDarkUi;
        RootChrome.RequestedTheme = preferDark ? ElementTheme.Dark : ElementTheme.Default;
        SyncCaptionButtonColors();
    }

    /// <summary>
    /// System minimize / maximize / close glyphs: <see cref="AppWindowTitleBar"/> <c>Button*</c> colors only
    /// (does not set title text / <c>ForegroundColor</c>).
    /// </summary>
    private void SyncCaptionButtonColors()
    {
        var bar = AppWindow.TitleBar;
        if (RootChrome.ActualTheme != ElementTheme.Dark)
        {
            bar.ButtonBackgroundColor = null;
            bar.ButtonForegroundColor = null;
            bar.ButtonHoverBackgroundColor = null;
            bar.ButtonHoverForegroundColor = null;
            bar.ButtonPressedBackgroundColor = null;
            bar.ButtonPressedForegroundColor = null;
            bar.ButtonInactiveBackgroundColor = null;
            bar.ButtonInactiveForegroundColor = null;
            return;
        }

        var white = Color.FromArgb(255, 255, 255, 255);
        var transparent = Color.FromArgb(0, 255, 255, 255);
        var inactiveGlyph = Color.FromArgb(255, 184, 184, 184);
        bar.ButtonBackgroundColor = transparent;
        bar.ButtonForegroundColor = white;
        bar.ButtonInactiveBackgroundColor = transparent;
        bar.ButtonInactiveForegroundColor = inactiveGlyph;
        bar.ButtonHoverForegroundColor = white;
        bar.ButtonPressedForegroundColor = white;
        bar.ButtonHoverBackgroundColor = Color.FromArgb(255, 44, 44, 44);
        bar.ButtonPressedBackgroundColor = Color.FromArgb(255, 62, 62, 62);
    }

    internal void NavigateShell()
    {
        AppLogger.Log("Initializing Shell. Navigating to Main UI.");
        UpdateUserActivity();
        ApplyThemeFromSettings();

        if (!HasStoredApiKey())
        {
            ShowPairingGate();
            return;
        }

        ContinueAfterPairing();
    }

    private static bool HasStoredApiKey()
    {
        return OpenSteamApiKeyStore.TryRetrieve(out var key) && !string.IsNullOrWhiteSpace(key);
    }

    private void ShowPairingGate()
    {
        NavView.Visibility = Visibility.Collapsed;
        OnboardingHost.Visibility = Visibility.Collapsed;
        PairingHost.Visibility = Visibility.Visible;
        AppTitleBar.IsPaneToggleButtonVisible = false;
        PairingGate.Refresh();
    }

    private void PairingGate_PairingCompleted(object? sender, EventArgs e)
    {
        PairingHost.Visibility = Visibility.Collapsed;
        ContinueAfterPairing();
    }

    private void ContinueAfterPairing()
    {
        var svc = ((App)Application.Current).Svcs;

        if (FirstLaunchGate.ShouldShowWelcomeWizard(svc))
        {
            NavView.Visibility = Visibility.Collapsed;
            OnboardingHost.Visibility = Visibility.Visible;
            AppTitleBar.IsPaneToggleButtonVisible = false;
            OnboardingFrame.Navigate(typeof(OnboardingPage), new OnboardingNavigationArgs(FinishWelcomeFlow));
            return;
        }

        OnboardingHost.Visibility = Visibility.Collapsed;
        NavView.Visibility = Visibility.Visible;
        AppTitleBar.IsPaneToggleButtonVisible = true;
        NavFrame.Navigate(typeof(HomePage), new HomeNavigationArgs(HomeLandingMode.StoreSearch));
        SvcsDiscordReload();

        // Kick off background stats load so the pane footer populates without blocking startup
        _ = RefreshUserStatsAsync();

        // Poll key status every 10 seconds so suspensions / deletions appear near-instantly
        _statusPollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _statusPollTimer.Tick += async (_, _) => await PollKeyStatusAsync();
        _statusPollTimer.Start();

        // Heartbeat every 30 seconds so admin commands (disable / forceUpdate) propagate quickly
        _heartbeatTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _heartbeatTimer.Tick += async (_, _) =>
            await ((App)Application.Current).Svcs.AdminReporter.ReportHeartbeatAsync();
        _heartbeatTimer.Start();
    }

    private async Task PollKeyStatusAsync()
    {
        if (!OpenSteamApiKeyStore.TryRetrieve(out var key) || string.IsNullOrWhiteSpace(key))
            return;

        try
        {
            var svcs = ((App)Application.Current).Svcs;
            var stats = await svcs.OpenSteamApi
                .GetStatsAsync(key.Trim(), CancellationToken.None)
                .ConfigureAwait(true);

            ApplyKeyStatus(stats.Ok ? stats : null, stats.Ok, stats.ErrorMessage);

            if (stats.Ok && UserStatsCard.Visibility == Visibility.Visible)
            {
                if (stats.UsageToday.HasValue && stats.CreditsTotal.HasValue)
                    PaneCreditsText.Text = $"{stats.UsageToday}/{stats.CreditsTotal} gens today";
                else if (stats.CreditsRemaining.HasValue && stats.CreditsTotal.HasValue)
                    PaneCreditsText.Text = $"{stats.CreditsRemaining}/{stats.CreditsTotal} remaining";
            }
        }
        catch { /* silent — never block the UI */ }
    }

    private void FinishWelcomeFlow()
    {
        OnboardingHost.Visibility = Visibility.Collapsed;
        NavView.Visibility = Visibility.Visible;
        AppTitleBar.IsPaneToggleButtonVisible = true;
        NavFrame.Navigate(typeof(HomePage), new HomeNavigationArgs(HomeLandingMode.StoreSearch));
        SvcsDiscordReload();
    }

    private void SvcsDiscordReload()
    {
        try
        {
            ((App)Application.Current).Svcs.DiscordPresence.Connect();
        }
        catch
        {
            /* optional */
        }
    }

    private void TitleBar_PaneToggleRequested(TitleBar sender, object args)
    {
        NavView.IsPaneOpen = !NavView.IsPaneOpen;
    }

    private void TitleBar_BackRequested(TitleBar sender, object args)
    {
        if (NavFrame.CanGoBack)
            NavFrame.GoBack();
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            if (NavFrame.CurrentSourcePageType != typeof(SettingsPage))
                NavFrame.Navigate(typeof(SettingsPage));
            return;
        }

        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string tag)
            return;

        if (tag == "manifest-library")
        {
            NavFrame.Navigate(typeof(HomePage), new HomeNavigationArgs(HomeLandingMode.ManifestLibrary));
            return;
        }

        if (tag == "game-requests")
        {
            NavFrame.Navigate(typeof(GameRequestsPage));
            return;
        }

        if (tag == "online-fixes")
        {
            NavFrame.Navigate(typeof(OnlineFixPage));
            return;
        }

        if (tag == "home")
            NavFrame.Navigate(typeof(HomePage), new HomeNavigationArgs(HomeLandingMode.StoreSearch));
    }

    // ── Game Requests navigation ──────────────────────────────────────────────

    /// <summary>
    /// Navigates to the Game Requests page, optionally pre-selecting a game
    /// (called from the Home page install-failure flow).
    /// </summary>
    internal void NavigateToRequests(uint? appId = null, string? gameName = null)
    {
        // Highlight the Requests nav item
        foreach (var item in NavView.MenuItems.OfType<NavigationViewItem>())
        {
            if (item.Tag is string tag && tag == "game-requests")
            {
                NavView.SelectedItem = item;
                break;
            }
        }
        NavFrame.Navigate(typeof(GameRequestsPage),
            new GameRequestsNavigationArgs(appId, gameName));
    }

    // ── User stats (pane footer) ──────────────────────────────────────────────

    /// <summary>
    /// Activates the app with the server (POST /activate) to register the machine and
    /// retrieve user identity + usage in one call.  Falls back to GET /stats if activation
    /// fails.  Populates the pane-footer user card and seeds the AdminReporter cache.
    /// Safe to call at any time; silently no-ops when no key is configured.
    /// </summary>
    internal async Task RefreshUserStatsAsync()
    {
        var svcs = ((App)Application.Current).Svcs;

        if (!ManifestApp.Services.OpenSteamApiKeyStore.TryRetrieve(out var key) ||
            string.IsNullOrWhiteSpace(key))
        {
            UserStatsCard.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            OpenSteamStatsResult? stats = null;
            bool isNewUser = false;

            // Only call /activate on true first launch (MachineId not yet generated).
            // Every other startup uses /stats which is read-only and costs zero credits.
            var settings = svcs.SettingsStore.Load();
            if (string.IsNullOrWhiteSpace(settings.MachineId))
            {
                var activation = await svcs.Activation
                    .ActivateAsync(key.Trim(), CancellationToken.None)
                    .ConfigureAwait(true);

                if (activation.Ok)
                {
                    stats    = activation.ToStatsResult();
                    isNewUser = activation.IsNewUser;
                }
            }

            // Primary path for every launch after the first (and fallback if activation failed)
            if (stats is null || !stats.Ok)
            {
                var fetched = await svcs.OpenSteamApi
                    .GetStatsAsync(key.Trim(), CancellationToken.None)
                    .ConfigureAwait(true);
                if (fetched.Ok)
                    stats = fetched;
            }

            if (stats is null || !stats.Ok)
            {
                var errMsg = stats?.ErrorMessage;
                ApplyKeyStatus(stats, false, errMsg);
                UserStatsCard.Visibility = Visibility.Visible;
                PaneUserName.Text    = "API Key";
                PaneDiscordTag.Text  = "";
                PanePlanText.Text    = "";
                PaneCreditsText.Text = errMsg ?? "Could not load account.";
                return;
            }

            // Prefer display name → raw Discord ID → generic fallback
            string nameText;
            if (!string.IsNullOrWhiteSpace(stats.DisplayName))
                nameText = stats.DisplayName;
            else if (!string.IsNullOrWhiteSpace(stats.DiscordId))
                nameText = $"Discord ID {stats.DiscordId}";
            else
                nameText = "Discord account";

            PaneUserName.Text    = nameText;
            PaneDiscordTag.Text  = !string.IsNullOrWhiteSpace(stats.DisplayName) ? "via Discord" : "";

            PanePlanText.Text = string.IsNullOrWhiteSpace(stats.Plan)
                ? ""
                : $"Plan: {stats.Plan}";

            if (stats.CreditsRemaining.HasValue || stats.UsageToday.HasValue)
            {
                if (stats.UsageToday.HasValue && stats.CreditsTotal.HasValue)
                    PaneCreditsText.Text = $"{stats.UsageToday}/{stats.CreditsTotal} gens today";
                else if (stats.CreditsRemaining.HasValue && stats.CreditsTotal.HasValue)
                    PaneCreditsText.Text = $"{stats.CreditsRemaining}/{stats.CreditsTotal} remaining";
                else if (stats.CreditsRemaining.HasValue)
                    PaneCreditsText.Text = $"{stats.CreditsRemaining} gens remaining";
                else
                    PaneCreditsText.Text = $"{stats.UsageToday} gens used today";
            }
            else
            {
                PaneCreditsText.Text = "";
            }

            UserStatsCard.Visibility = Visibility.Visible;
            ApplyKeyStatus(stats, true, null);

            // Show a one-time welcome notification for brand-new activations.
            if (isNewUser)
            {
                GlobalUpdateNotification.Title   = "Welcome to OpenSteam!";
                GlobalUpdateNotification.Message = $"Your account is activated. Plan: {stats.Plan ?? "Free"}";
                GlobalUpdateNotification.Severity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Success;
                GlobalUpdateNotification.IsOpen  = true;
            }

            // Cache identity in the reporter so subsequent event posts include user info.
            svcs.AdminReporter.CacheStats(key.Trim(), stats!);
            _ = svcs.AdminReporter.ReportStartupAsync();
        }
        catch
        {
            UserStatsCard.Visibility = Visibility.Collapsed;
        }
    }

    // ── Key status indicator ──────────────────────────────────────────────────

    private void SetKeyStatus(string label, byte r, byte g, byte b)
    {
        KeyStatusDot.Fill = new SolidColorBrush(Color.FromArgb(255, r, g, b));
        KeyStatusText.Text = label;
        KeyStatusText.Foreground = new SolidColorBrush(Color.FromArgb(255, r, g, b));
        KeyStatusRow.Visibility = Visibility.Visible;
    }

    private void ApplyKeyStatus(OpenSteamStatsResult? stats, bool ok, string? errorMessage)
    {
        if (stats == null && !ok)
        {
            SetKeyStatus("KEY INVALID", 0xFF, 0x55, 0x55);
            return;
        }

        if (!ok)
        {
            var err = errorMessage?.ToLowerInvariant() ?? "";
            if (err.Contains("401") || err.Contains("invalid"))
                SetKeyStatus("KEY INVALID", 0xFF, 0x55, 0x55);
            else if (err.Contains("429") || err.Contains("quota") || err.Contains("limit"))
                SetKeyStatus("LIMIT REACHED", 0xFF, 0xA0, 0x40);
            else
                SetKeyStatus("UNAVAILABLE", 0xFF, 0xA0, 0x40);
            return;
        }

        if (stats?.CreditsRemaining is 0)
        {
            SetKeyStatus("LIMIT REACHED", 0xFF, 0xA0, 0x40);
            return;
        }

        SetKeyStatus("ACTIVE", 0x7D, 0xD3, 0xA0);
    }

    // ── Administrative Commands ───────────────────────────────────────────────

    internal void ShowLockout()
    {
        LockoutHost.Visibility = Visibility.Visible;
        NavView.IsEnabled = false;
        SetKeyStatus("SUSPENDED", 0xFF, 0x44, 0x44);
    }

    internal async Task ForceUpdateAsync(string? exeDownloadUrl = null)
    {
        if (_forceUpdateInProgress)
            return;

        _forceUpdateInProgress = true;
        var svcs = ((App)Application.Current).Svcs;
        try
        {
            if (string.IsNullOrWhiteSpace(exeDownloadUrl))
            {
                var result = await svcs.UpdateChecker.CheckAsync();
                exeDownloadUrl = result?.ExeDownloadUrl;
            }

            if (!string.IsNullOrWhiteSpace(exeDownloadUrl))
            {
                GlobalUpdateNotification.Message = "Forced update initiated by administrator...";
                GlobalUpdateNotification.IsOpen = true;

                var checkResult = await svcs.UpdateChecker.CheckAsync();
                var batPath = await svcs.UpdateChecker.DownloadUpdateAsync(
                    exeDownloadUrl,
                    expectedSha256Hex: checkResult?.ExeSha256Hex);
                if (batPath != null)
                    UpdateService.ApplyUpdate(batPath);
                else
                {
                    GlobalUpdateNotification.Severity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error;
                    GlobalUpdateNotification.Message = "Forced update failed — download URL rejected or integrity check failed.";
                    GlobalUpdateNotification.IsOpen = true;
                }
            }
        }
        finally
        {
            _forceUpdateInProgress = false;
        }
    }

    private void UpdateUserActivity()
    {
        try
        {
            var svcs = ((App)Application.Current).Svcs;
            var settings = svcs.SettingsStore.Load();
            settings.LastUsedTime = DateTime.UtcNow;
            svcs.SettingsStore.Save(settings);
        }
        catch { /* fail silent */ }
    }
}
