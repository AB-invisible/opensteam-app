using System.Text.Json;
using Microsoft.UI.Xaml;
using ManifestApp;
using ManifestApp.Core;

namespace ManifestApp.Services;

/// <summary>
/// Fire-and-forget telemetry reporter.  Posts session heartbeats and user events
/// to the OpenSteam server (POST /api/report, configured via AdminEndpointUrl).
/// If the endpoint is empty or unreachable the call is silently dropped.
/// </summary>
public sealed class AdminReporterService(HttpClient http, SettingsStore settingsStore)
{
    private static readonly string SessionId = Guid.NewGuid().ToString("N");
    private static readonly string AppVersion =
        typeof(AdminReporterService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private OpenSteamStatsResult? _cachedStats;
    private string?             _cachedApiKey;

    /// <summary>Call once after the first successful stats fetch so the reporter can include user identity.</summary>
    public void CacheStats(string apiKey, OpenSteamStatsResult stats)
    {
        _cachedApiKey = apiKey;
        _cachedStats  = stats;
    }

    public Task ReportStartupAsync()  => SendAsync("startup");
    public Task ReportHeartbeatAsync() => SendAsync("heartbeat");

    public Task ReportInstallAsync(uint appId, string gameName, bool success) =>
        SendAsync("install", appId, gameName, success);

    public Task ReportRemoveAsync(uint appId, string gameName) =>
        SendAsync("remove", appId, gameName, null);

    public Task ReportSearchAsync(string query) =>
        SendAsync("search", detail: query);

    // ── Internal ──────────────────────────────────────────────────────────────

    private async Task SendAsync(
        string  eventType,
        uint?   appId    = null,
        string? gameName = null,
        bool?   success  = null,
        string? detail   = null)
    {
        var endpoint = settingsStore.Load().AdminEndpointUrl?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(endpoint))
            return;

        var stats = _cachedStats;
        var key   = _cachedApiKey ?? "";

        var payload = new
        {
            sessionId  = SessionId,
            apiKey     = key,
            appVersion = AppVersion,
            user       = stats is null ? null : new
            {
                discordId     = stats.DiscordId,
                username      = stats.DisplayName,
                discriminator = (string?)null,   // already merged into DisplayName
                role          = stats.Role ?? "USER",
                isStaff       = stats.IsStaff,
            },
            plan  = stats?.Plan,
            usage = stats is null ? null : new
            {
                today     = stats.UsageToday,
                limit     = stats.CreditsTotal,
                remaining = stats.CreditsRemaining,
                resetAt   = stats.ResetAt,
            },
            @event = new
            {
                type     = eventType,
                appId,
                gameName,
                success,
                detail,
            },
            timestamp = DateTimeOffset.UtcNow.ToString("O"),
        };

        try
        {
            var json    = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            
            var rsp = await http
                .PostAsync($"{endpoint}/api/report", content)
                .WaitAsync(TimeSpan.FromSeconds(4))
                .ConfigureAwait(false);

            if (!rsp.IsSuccessStatusCode) return;

            var body = await rsp.Content.ReadAsStringAsync().ConfigureAwait(false);
            var result = JsonSerializer.Deserialize<AdminResponse>(body, 
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (result is null) return;

            var disable = result.Disable || result.Commands?.Disable == true;
            var forceUpdate = result.ForceUpdate || result.Commands?.ForceUpdate == true;

            // Handle commands
            if (disable)
            {
                if (Application.Current is App { MainShell: MainWindow mw })
                {
                    mw.DispatcherQueue.TryEnqueue(mw.ShowLockout);
                }
            }

            if (forceUpdate)
            {
                if (Application.Current is App { MainShell: MainWindow mw })
                {
                    var updateUrl = result.ForceUpdateUrl
                                 ?? result.ExeDownloadUrl
                                 ?? result.Commands?.ForceUpdateUrl
                                 ?? result.Commands?.ExeDownloadUrl;
                    mw.DispatcherQueue.TryEnqueue(() => _ = mw.ForceUpdateAsync(updateUrl));
                }
            }
        }
        catch
        {
            // Telemetry must never crash the host app.
        }
    }

    private sealed record AdminResponse(
        bool Disable,
        bool ForceUpdate,
        string? ForceUpdateUrl,
        string? ExeDownloadUrl,
        AdminCommands? Commands);

    private sealed record AdminCommands(
        bool Disable,
        bool ForceUpdate,
        string? ForceUpdateUrl,
        string? ExeDownloadUrl);
}
