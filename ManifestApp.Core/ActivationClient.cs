using System.Text.Json;

namespace ManifestApp.Core;

/// <summary>
/// Calls <c>POST /api/v2/activate</c> with Bearer auth to register the machine,
/// obtain user identity, and receive fresh usage figures in a single round-trip.
/// </summary>
public sealed class ActivationClient(HttpClient http, SettingsStore settingsStore)
{
    // ── Public API ────────────────────────────────────────────────────────────

    public async Task<ActivationResult> ActivateAsync(string apiKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return ActivationResult.Fail("API key is empty.");

        var url = $"{GetApiRoot().TrimEnd('/')}/api/v2/activate";

        try
        {
            var body = JsonSerializer.Serialize(new
            {
                machineId = GetOrCreateMachineId(),
                os        = GetOsDescription(),
                version   = GetAppVersion(),
            });

            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
            req.Headers.Accept.ParseAdd("application/json");
            req.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey.Trim());

            using var rsp = await http.SendAsync(req, ct).ConfigureAwait(false);
            var raw = await rsp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (rsp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                return ActivationResult.Fail("Invalid API key (401).");

            if (rsp.StatusCode == System.Net.HttpStatusCode.Forbidden)
                return ActivationResult.Fail(ParseErrorMessage(raw) ?? "Activation rejected (403).");

            if (rsp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                return ActivationResult.Fail("Daily quota exceeded (429).");

            if (!rsp.IsSuccessStatusCode)
                return ActivationResult.Fail(ParseErrorMessage(raw) ?? $"Server error HTTP {(int)rsp.StatusCode}.");

            return ParseResponse(raw);
        }
        catch (Exception ex)
        {
            return ActivationResult.Fail($"Network error: {ex.Message}");
        }
    }

    private static string? ParseErrorMessage(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("error", out var err))
                return err.GetString();
        }
        catch { /* ignore */ }

        return null;
    }

    // ── Parsing ───────────────────────────────────────────────────────────────

    private static ActivationResult ParseResponse(string raw)
    {
        try
        {
            using var doc  = JsonDocument.Parse(raw);
            var root  = doc.RootElement;

            // success flag
            if (root.TryGetProperty("success", out var succ) &&
                succ.ValueKind == JsonValueKind.False)
                return ActivationResult.Fail("Activation rejected by server.");

            // data.user
            string? username = null, plan = null, role = null, activationId = null;
            bool    isStaff  = false, isNewUser = false;

            if (root.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Object)
            {
                if (data.TryGetProperty("activationId", out var aid))
                    activationId = aid.GetString();

                if (data.TryGetProperty("isNewUser", out var inu))
                    isNewUser = inu.ValueKind == JsonValueKind.True;

                if (data.TryGetProperty("user", out var u) &&
                    u.ValueKind == JsonValueKind.Object)
                {
                    if (u.TryGetProperty("username", out var un)) username = un.GetString();
                    if (u.TryGetProperty("plan",     out var pl)) plan     = pl.GetString();
                    if (u.TryGetProperty("role",     out var ro)) role     = ro.GetString();
                    if (u.TryGetProperty("isStaff",  out var sf))
                        isStaff = sf.ValueKind == JsonValueKind.True;
                }
            }

            // usage — resetAt may be Unix timestamp (integer) or ISO string
            int?    usageToday = null, usageLimit = null, usageRemaining = null;
            string? resetAt    = null;

            if (root.TryGetProperty("usage", out var usage) &&
                usage.ValueKind == JsonValueKind.Object)
            {
                if (usage.TryGetProperty("today",     out var t) && t.TryGetInt32(out var ti)) usageToday     = ti;
                if (usage.TryGetProperty("limit",     out var l) && l.TryGetInt32(out var li)) usageLimit     = li;
                if (usage.TryGetProperty("remaining", out var r) && r.TryGetInt32(out var ri)) usageRemaining = ri;

                if (usage.TryGetProperty("resetAt", out var rat))
                {
                    if (rat.ValueKind == JsonValueKind.Number && rat.TryGetInt64(out var unix))
                    {
                        // Unix timestamp → ISO-8601 string for consistency with stats endpoint
                        resetAt = DateTimeOffset.FromUnixTimeSeconds(unix)
                            .ToString("O", System.Globalization.CultureInfo.InvariantCulture);
                    }
                    else if (rat.ValueKind == JsonValueKind.String)
                    {
                        resetAt = rat.GetString();
                    }
                }
            }

            return new ActivationResult
            {
                Ok             = true,
                ActivationId   = activationId,
                IsNewUser      = isNewUser,
                Username       = username,
                Plan           = plan,
                Role           = role,
                IsStaff        = isStaff,
                UsageToday     = usageToday,
                UsageLimit     = usageLimit,
                UsageRemaining = usageRemaining,
                ResetAt        = resetAt,
            };
        }
        catch (Exception ex)
        {
            return ActivationResult.Fail($"Could not parse activation response: {ex.Message}");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a stable per-machine GUID.  Generated once and persisted in settings.
    /// Does not use WMI or hardware probing — stays simple and reliable.
    /// </summary>
    private string GetOrCreateMachineId()
    {
        var s = settingsStore.Load();
        if (!string.IsNullOrWhiteSpace(s.MachineId))
            return s.MachineId;

        s.MachineId = Guid.NewGuid().ToString("N");
        settingsStore.Save(s);
        return s.MachineId;
    }

    private static string GetOsDescription()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key != null)
            {
                var product = key.GetValue("ProductName") as string;
                var build   = key.GetValue("CurrentBuildNumber") as string;
                var ubr     = key.GetValue("UBR");   // Update Build Revision
                if (!string.IsNullOrEmpty(product))
                    return ubr != null && !string.IsNullOrEmpty(build)
                        ? $"{product} (Build {build}.{ubr})"
                        : !string.IsNullOrEmpty(build)
                            ? $"{product} (Build {build})"
                            : product;
            }
        }
        catch { /* fall through */ }

        return System.Runtime.InteropServices.RuntimeInformation.OSDescription;
    }

    private static string GetAppVersion() =>
        typeof(ActivationClient).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private string GetApiRoot()
    {
        return OpenSteamApiEndpoint.ResolvePrimary(settingsStore);
    }
}

// ── Result type ───────────────────────────────────────────────────────────────

public sealed class ActivationResult
{
    public bool    Ok             { get; init; }
    public string? ActivationId   { get; init; }
    public bool    IsNewUser      { get; init; }
    public string? Username       { get; init; }
    public string? Plan           { get; init; }
    public string? Role           { get; init; }
    public bool    IsStaff        { get; init; }
    public int?    UsageToday     { get; init; }
    public int?    UsageLimit     { get; init; }
    public int?    UsageRemaining { get; init; }
    /// <summary>ISO-8601 reset timestamp (normalised from Unix int or ISO string).</summary>
    public string? ResetAt        { get; init; }
    public string? ErrorMessage   { get; init; }

    public static ActivationResult Fail(string msg) =>
        new() { Ok = false, ErrorMessage = msg };

    /// <summary>Converts to a <see cref="OpenSteamStatsResult"/> so existing UI code can consume it unchanged.</summary>
    public OpenSteamStatsResult ToStatsResult() => new()
    {
        Ok               = Ok,
        Plan             = Plan,
        DisplayName      = Username,
        Role             = Role,
        IsStaff          = IsStaff,
        CreditsRemaining = UsageRemaining,
        CreditsTotal     = UsageLimit,
        UsageToday       = UsageToday,
        ResetAt          = ResetAt,
        ErrorMessage     = ErrorMessage,
    };
}
