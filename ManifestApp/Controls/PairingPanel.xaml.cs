using ManifestApp.Core;
using ManifestApp.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace ManifestApp.Controls;

public sealed partial class PairingPanel : UserControl
{
    private string? _pairingCode;
    private CancellationTokenSource? _pairingPollCts;
    private DispatcherTimer? _copyFeedbackTimer;

    public event EventHandler? PairingCompleted;

    public PairingPanel()
    {
        InitializeComponent();
        Loaded += PairingPanel_Loaded;
        Unloaded += (_, _) =>
        {
            _pairingPollCts?.Cancel();
            _copyFeedbackTimer?.Stop();
        };
    }

    public void RefreshKeyStatus()
    {
        ApiKeyStatus.Text = OpenSteamApiKeyStore.TryRetrieve(out _)
            ? "API key is saved securely on this device."
            : "No API key saved yet. Generate a pairing code above, or paste a key from Discord.";
    }

    private App TypedApp => (App)Application.Current;

    private void PairingPanel_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshKeyStatus();

        if (OpenSteamApiKeyStore.TryRetrieve(out var existing) && !string.IsNullOrWhiteSpace(existing))
            return;

        _ = EnsurePairingCodeAsync();
    }

    private async void CopyPairingCode_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_pairingCode))
            return;

        var package = new DataPackage();
        package.SetText(_pairingCode);
        Clipboard.SetContent(package);

        PairingCopyFeedback.Text = "Copied to clipboard";
        PairingCopyFeedback.Visibility = Visibility.Visible;
        PairingCopyHintText.Text = "Copied!";

        _copyFeedbackTimer?.Stop();
        _copyFeedbackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _copyFeedbackTimer.Tick += (_, _) =>
        {
            _copyFeedbackTimer.Stop();
            PairingCopyFeedback.Visibility = Visibility.Collapsed;
            PairingCopyHintText.Text = "Click to copy code";
        };
        _copyFeedbackTimer.Start();
    }

    private async Task EnsurePairingCodeAsync(bool force = false)
    {
        if (!force && !string.IsNullOrWhiteSpace(_pairingCode))
            return;

        PairingStatusText.Visibility = Visibility.Visible;
        PairingStatusText.Text = "Generating pairing code…";
        GeneratePairingCodeButton.IsEnabled = false;
        RefreshPairingButton.IsEnabled = false;
        CopyCodeButton.IsEnabled = false;

        var result = await TypedApp.Svcs.Pairing.RequestCodeAsync();

        GeneratePairingCodeButton.IsEnabled = true;
        RefreshPairingButton.IsEnabled = true;
        CopyCodeButton.IsEnabled = true;

        if (!result.Success || string.IsNullOrWhiteSpace(result.Code))
        {
            PairingCodeText.Text = "--------";
            PairingStatusText.Text = result.Error ?? "Could not generate pairing code.";
            return;
        }

        _pairingCode = result.Code;
        PairingCodeText.Text = result.Code;
        PairingHintText.Text = $"Run in Discord: /key pair code:{result.Code}";
        PairingStatusText.Text = "Waiting for Discord… run the command above, then tap Check for key.";
        StartPairingPoll();
    }

    private void StartPairingPoll()
    {
        _pairingPollCts?.Cancel();
        _pairingPollCts = new CancellationTokenSource();
        _ = PollPairingLoopAsync(_pairingPollCts.Token);
    }

    private async Task PollPairingLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(4), token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(_pairingCode))
                continue;

            var status = await TypedApp.Svcs.Pairing.PollStatusAsync(_pairingCode, token);
            if (status.Kind == "ready" && !string.IsNullOrWhiteSpace(status.ApiKey))
            {
                DispatcherQueue.TryEnqueue(() => _ = SavePairingKeyAsync(status.ApiKey!));
                return;
            }

            if (status.Kind == "expired")
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    PairingStatusText.Text = "Code expired — tap Generate code again.";
                    _pairingCode = null;
                });
                return;
            }
        }
    }

    private async Task SavePairingKeyAsync(string apiKey)
    {
        try
        {
            OpenSteamApiKeyStore.Replace(apiKey);
            ApiKeyBox.Password = string.Empty;

            var activation = await TypedApp.Svcs.Activation.ActivateAsync(apiKey, CancellationToken.None);
            if (!activation.Ok)
            {
                PairingStatusText.Visibility = Visibility.Visible;
                PairingStatusText.Text = activation.ErrorMessage ?? "API key could not be verified with the server.";
                ApiKeyStatus.Text = PairingStatusText.Text;
                return;
            }

            var usageSummary = FormatUsageSummary(activation);
            ApiKeyStatus.Text = usageSummary;
            PairingStatusText.Visibility = Visibility.Visible;
            PairingStatusText.Text = "API key received and saved.";
            _pairingPollCts?.Cancel();
            await RefreshShellStatsAsync();
            PairingCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            PairingStatusText.Text = $"Could not save API key: {ex.Message}";
        }
    }

    private static string FormatUsageSummary(ActivationResult activation)
    {
        if (activation.UsageRemaining.HasValue && activation.UsageLimit.HasValue)
        {
            var used = activation.UsageToday ?? Math.Max(0, activation.UsageLimit.Value - activation.UsageRemaining.Value);
            return $"{activation.UsageRemaining}/{activation.UsageLimit} gens left today ({used} used)";
        }

        if (activation.UsageRemaining.HasValue)
            return $"{activation.UsageRemaining} gens left today";

        if (activation.UsageToday.HasValue && activation.UsageLimit.HasValue)
            return $"{Math.Max(0, activation.UsageLimit.Value - activation.UsageToday.Value)}/{activation.UsageLimit} gens left today";

        return "API key saved and verified.";
    }

    private async Task RefreshShellStatsAsync()
    {
        if (TypedApp.MainShell is MainWindow mw)
            await mw.RefreshUserStatsAsync();
    }

    private async void GeneratePairingCode_Click(object sender, RoutedEventArgs e)
    {
        _pairingCode = null;
        PairingCodeText.Text = "--------";
        await EnsurePairingCodeAsync(force: true);
    }

    private async void RefreshPairing_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_pairingCode))
        {
            await EnsurePairingCodeAsync();
            return;
        }

        PairingStatusText.Visibility = Visibility.Visible;
        PairingStatusText.Text = "Checking Discord…";

        var status = await TypedApp.Svcs.Pairing.PollStatusAsync(_pairingCode);
        if (status.Kind == "ready" && !string.IsNullOrWhiteSpace(status.ApiKey))
        {
            await SavePairingKeyAsync(status.ApiKey);
            return;
        }

        PairingStatusText.Text = status.Kind == "pending"
            ? "Still waiting — run /key pair in Discord with the code above."
            : status.Error ?? "No key yet.";
    }

    private async void SaveManualKey_Click(object sender, RoutedEventArgs e)
    {
        var keyProvided = false;
        string? savedKey = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(ApiKeyBox.Password))
            {
                savedKey = ApiKeyBox.Password.Trim();
                OpenSteamApiKeyStore.Replace(savedKey);
                keyProvided = true;
            }
        }
        catch (Exception ex)
        {
            ApiKeyBox.Password = string.Empty;
            ApiKeyStatus.Text = $"Couldn't write the API key to Windows Credential Locker: {ex.Message}";
            return;
        }

        ApiKeyBox.Password = string.Empty;

        if (keyProvided && savedKey is not null)
        {
            var activation = await TypedApp.Svcs.Activation.ActivateAsync(savedKey, CancellationToken.None);
            if (!activation.Ok)
            {
                ApiKeyStatus.Text = activation.ErrorMessage ?? "API key could not be verified with the server.";
                return;
            }

            ApiKeyStatus.Text = FormatUsageSummary(activation);
            PairingCompleted?.Invoke(this, EventArgs.Empty);
            await RefreshShellStatsAsync();
            return;
        }

        bool vaultOk;
        try
        {
            vaultOk = OpenSteamApiKeyStore.TryRetrieve(out _);
        }
        catch
        {
            vaultOk = false;
        }

        ApiKeyStatus.Text = vaultOk switch
        {
            true => "API key unchanged in Credential Locker.",
            _ => "No API key saved yet. Generate a pairing code above or paste your key from Discord.",
        };
    }
}
