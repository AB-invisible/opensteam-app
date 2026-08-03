using System.Diagnostics.CodeAnalysis;
using ManifestApp.Core.Models;
using ManifestApp.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;

namespace ManifestApp.Pages;

public sealed partial class OnboardingPage : Page
{
    private Action _onCompleted = () => { };
    private int _step;
    private bool _welcomeAnimated;

    public OnboardingPage()
    {
        InitializeComponent();
        PairingPanel.PairingCompleted += (_, _) => TryFinishFromStoredKey();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.Parameter is OnboardingNavigationArgs args)
            _onCompleted = args.OnCompleted;

        _step = 0;
        PrefillSteamIfPossible();
        ApplyStepUi();
    }

    private App TypedApp => (App)Application.Current;

    private void PrefillSteamIfPossible()
    {
        if (!string.IsNullOrWhiteSpace(SteamPathBox.Text))
            return;

        var resolved = TypedApp.Svcs.PathsResolver.ResolveSteamInstall();
        if (!string.IsNullOrEmpty(resolved))
            SteamPathBox.Text = resolved;

        SteamPathHintFromSettings();
        UpdateSteamDerivedPathsUi();
    }

    private void SteamPathHintFromSettings()
    {
        var s = TypedApp.Svcs.SettingsStore.Load();
        var o = s.SteamInstallPathOverride?.Trim().Trim('"');
        if (string.IsNullOrEmpty(o))
            return;
        if (!string.IsNullOrWhiteSpace(SteamPathBox.Text))
            return;
        SteamPathBox.Text = o;
    }

    private void WelcomePanel_Loaded(object sender, RoutedEventArgs e)
    {
        if (_welcomeAnimated || _step != 0)
            return;
        _welcomeAnimated = true;

        WelcomePanel.UpdateLayout();

        var sb = new Storyboard();

        var opacity = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(680),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(opacity, WelcomePanel);
        Storyboard.SetTargetProperty(opacity, "Opacity");
        sb.Children.Add(opacity);

        var drift = new DoubleAnimation
        {
            From = 18,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(720),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(drift, WelcomeTransform);
        Storyboard.SetTargetProperty(drift, "TranslateY");
        sb.Children.Add(drift);

        sb.Begin();
    }

    private void ApplyStepUi()
    {
        WelcomePanel.Visibility = _step == 0 ? Visibility.Visible : Visibility.Collapsed;
        SteamPanel.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        ApiPanel.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;

        StepIndicator.Text = _step switch
        {
            0 => "Step 1 of 3 — Welcome",
            1 => "Step 2 of 3 — Steam",
            _ => "Step 3 of 3 — OpenSteam",
        };

        StepProgress.Value = _step;
        BackButton.IsEnabled = _step > 0;
        NextButton.Content = _step == 2 ? "Get started" : "Next";

        if (_step == 1)
            UpdateSteamDerivedPathsUi();
    }

    private void SteamPathBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateSteamDerivedPathsUi();

    private void UpdateSteamDerivedPathsUi()
    {
        var steamRoot = NormalizeFolder(SteamPathBox.Text);
        if (!IsValidSteamRoot(steamRoot))
        {
            PluginPreview.Text = "stplug-in: —";
            DepotPreview.Text = "depotcache: —";
            return;
        }

        var plugin = TypedApp.Svcs.PathsResolver.DefaultStPluginFromSteamRoot(steamRoot!);
        var depot = TypedApp.Svcs.PathsResolver.DefaultDepotCacheFromSteamRoot(steamRoot!);
        PluginPreview.Text = "stplug-in: " + (plugin ?? "—");
        DepotPreview.Text = "depotcache: " + (depot ?? "—");
    }

    private bool IsValidSteamRoot([NotNullWhen(true)] string? path)
    {
        path = NormalizeFolder(path);
        if (string.IsNullOrEmpty(path))
            return false;
        return Directory.Exists(path) && File.Exists(Path.Combine(path, "steam.exe"));
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_step <= 0)
            return;
        _step--;
        ApplyStepUi();
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_step == 0)
        {
            _step = 1;
            ApplyStepUi();
            PrefillSteamIfPossible();
            return;
        }

        if (_step == 1)
        {
            HideValidation();

            var steamRoot = NormalizeFolder(SteamPathBox.Text);
            if (!IsValidSteamRoot(steamRoot))
            {
                ShowSteamValidation("Pick the folder that contains steam.exe — it wasn’t recognized as a Steam install.");
                return;
            }

            _step = 2;
            ApplyStepUi();
            return;
        }

        if (_step == 2)
        {
            HideValidation();
            if (OpenSteamApiKeyStore.TryRetrieve(out var key) && !string.IsNullOrWhiteSpace(key))
            {
                PersistAndFinish(key);
                return;
            }

            ApiValidation.Text = "Generate a pairing code and run /key pair in Discord, or paste your key and tap Save API key.";
            ApiValidation.Visibility = Visibility.Visible;
        }
    }

    private void TryFinishFromStoredKey()
    {
        if (_step != 2)
            return;

        if (OpenSteamApiKeyStore.TryRetrieve(out var key) && !string.IsNullOrWhiteSpace(key))
            PersistAndFinish(key);
    }

    private void PersistAndFinish(string apiKey)
    {
        var store = TypedApp.Svcs.SettingsStore;
        var cur = store.Load();
        cur.SteamInstallPathOverride = NormalizeOrNull(SteamPathBox.Text);
        cur.OnboardingCompleted = true;
        store.Save(cur);

        OpenSteamApiKeyStore.Replace(apiKey);

        _onCompleted();
    }

    private void HideValidation()
    {
        SteamValidation.Visibility = Visibility.Collapsed;
        ApiValidation.Visibility = Visibility.Collapsed;
    }

    private void ShowSteamValidation(string message)
    {
        SteamValidation.Text = message;
        SteamValidation.Visibility = Visibility.Visible;
    }

    private async void BrowseSteam_Click(object sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync(PickerLocationId.ComputerFolder);
        if (!string.IsNullOrEmpty(path))
            SteamPathBox.Text = path;
    }

    private void AutoDetectSteam_Click(object sender, RoutedEventArgs e)
    {
        var cur = TypedApp.Svcs.SettingsStore.Load();
        cur.SteamInstallPathOverride = null;
        TypedApp.Svcs.SettingsStore.Save(cur);

        var detected = TypedApp.Svcs.PathsResolver.ResolveSteamInstall();
        if (string.IsNullOrEmpty(detected))
        {
            ShowSteamValidation("Steam wasn’t found in the registry. Browse to your Steam folder instead.");
            return;
        }

        SteamValidation.Visibility = Visibility.Collapsed;
        SteamPathBox.Text = detected;
    }

    private async Task<string?> PickFolderAsync(PickerLocationId start)
    {
        var picker = new FolderPicker { SuggestedStartLocation = start };
        picker.FileTypeFilter.Add("*");

        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowInterop.GetWindowHandle(TypedApp.MainShell));

        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    private static string? NormalizeFolder(string? s)
    {
        var t = s?.Trim().Trim('"');
        return string.IsNullOrWhiteSpace(t) ? null : t;
    }

    private static string? NormalizeOrNull(string s)
    {
        var t = s.Trim().Trim('"');
        return string.IsNullOrWhiteSpace(t) ? null : t;
    }
}
