using DiscordRPC;
using ManifestApp.Core;

namespace ManifestApp.Services;

/// <summary>Discord Rich Presence (local IPC). Requires Discord desktop; IDs and copy live in constants below.</summary>
public sealed class DiscordPresenceService : IDisposable
{
    /// <summary>Same Discord app as OpenSteam manifest bot — you control this application.</summary>
    private const string RpcApplicationId = "1532867690031484969";

    private const string PresenceDetails = "OpenSteam";

    private const string IdleState = "Idle";
    private const string SettingsState = "Settings";
    private const string SearchingPrefix = "Searching store:";
    private const string InstallingPrefix = "Installing —";
    private const string RemovingPrefix = "Removing —";
    private const string BrowsingPrefix = "Viewing —";

    /// <summary>Rich Presence artwork key registered under the Discord application.</summary>
    private const string PresenceLargeImageKey = "opensteam";

    private const string PresenceLargeImageHoverText = "OpenSteam";

    private readonly SettingsStore _settingsStore;

    private DiscordRpcClient? _client;
    private DateTime? _sessionUtcStart;

    public DiscordPresenceService(SettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
    }

    private bool PresenceDisabled => _settingsStore.Load().DiscordRichPresenceDisabled;

    /// <summary>Creates the IPC client if enabled and <see cref="RpcApplicationId"/> is set.</summary>
    public void Connect()
    {
        if (PresenceDisabled)
        {
            DisposeClient();
            return;
        }

        if (string.IsNullOrWhiteSpace(RpcApplicationId))
            return;

        try
        {
            StartClient();
            // Discord caches application metadata per client session; a second connect after a
            // developer-portal rename helps pick up the new "OpenSteam" label sooner.
            _ = RefreshConnectionAfterDelayAsync();
        }
        catch
        {
            DisposeClient();
        }
    }

    private async Task RefreshConnectionAfterDelayAsync()
    {
        try
        {
            await Task.Delay(1500).ConfigureAwait(false);
            if (PresenceDisabled)
                return;

            StartClient();
        }
        catch
        {
            /* RPC best effort */
        }
    }

    private void StartClient()
    {
        DisposeClient();

        _client = new DiscordRpcClient(RpcApplicationId.Trim());
        _client.OnReady += (_, _) => SetPresence(PresenceDetails, IdleState);
        _client.Initialize();
        _sessionUtcStart = DateTime.UtcNow;
        SetPresence(PresenceDetails, IdleState);
    }

    public void NotifySettingsPage()
    {
        SetPresence(PresenceDetails, SettingsState);
    }

    public void NotifyHomeSource(int sourceComboIndex)
    {
        var state = sourceComboIndex switch
        {
            1 => "Installed Steam games",
            2 => "Manifest library",
            _ => "Steam Store search",
        };
        SetPresence(PresenceDetails, state);
    }

    public void NotifySearchingStore(string queryTruncated)
    {
        SetPresence(PresenceDetails, $"{SearchingPrefix} “{queryTruncated}”");
    }

    public void NotifyInstalling(string displayNameTruncated)
    {
        SetPresence(PresenceDetails, $"{InstallingPrefix} {displayNameTruncated}");
    }

    public void NotifyRemoving(string displayNameTruncated)
    {
        SetPresence(PresenceDetails, $"{RemovingPrefix} {displayNameTruncated}");
    }

    public void NotifyBrowsingGame(string displayNameTruncated)
    {
        SetPresence(PresenceDetails, $"{BrowsingPrefix} {displayNameTruncated}");
    }
 
    public void NotifyBrowsingOnlineFixes()
    {
        SetPresence(PresenceDetails, "Browsing multiplayer fixes");
    }

    private void SetPresence(string details, string? state = null)
    {
        if (PresenceDisabled || _client is null)
            return;

        try
        {
            _client.SetPresence(new RichPresence
            {
                Details = Truncate(details, 120),
                State = string.IsNullOrEmpty(state) ? null : Truncate(state, 120),
                Timestamps = SessionTimestamps(),
                Assets = new Assets
                {
                    LargeImageKey = PresenceLargeImageKey,
                    LargeImageText = PresenceLargeImageHoverText,
                },
            });
        }
        catch
        {
            /* RPC best effort */
        }
    }

    private Timestamps SessionTimestamps()
    {
        if (_sessionUtcStart is { } start)
            return new Timestamps { Start = start };
        return Timestamps.Now;
    }

    private static string Truncate(string s, int max)
    {
        if (s.Length <= max)
            return s;
        return s[..(max - 1)] + "…";
    }

    private void DisposeClient()
    {
        _sessionUtcStart = null;

        try
        {
            _client?.Deinitialize();
        }
        catch
        {
            /* ignore */
        }

        try
        {
            _client?.ClearPresence();
        }
        catch
        {
            /* ignore */
        }

        try
        {
            _client?.Dispose();
        }
        catch
        {
            /* ignore */
        }

        _client = null;
    }

    public void Dispose() => DisposeClient();
}
