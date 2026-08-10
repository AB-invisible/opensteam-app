using System.Text.Json;
using ManifestApp.Core.Models;

namespace ManifestApp.Core;

/// <summary>
/// Canonical OpenSteam route: <c>GET https://opensteam.lol/api/{key}/generate/{appId}</c>.
/// Documentation sometimes shows placeholders percent-encoded (<c>%7Bkey%7D</c>, <c>%7BappId%7D</c>) —
/// substitute your real dashboard key (URL-encoded as a single path segment) and numeric Steam App ID.
/// Response: JSON with <c>manifest.downloadUrl</c> (preferred) then <c>?format=zip</c> fallback.
/// </summary>
public sealed class OpenSteamApiClient(HttpClient http, SettingsStore settingsStore)
{
    public async Task<OpenSteamZipResult> DownloadGenerateZipAsync(string apiKey, uint appId,
        CancellationToken cancellationToken, IProgress<double>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return OpenSteamZipResult.Fail("OpenSteam API key is empty. Add it in Settings.");

        var generateUrl = $"{GetApiRoot().TrimEnd('/')}/api/v2/generate/{appId}?format=zip";

        try
        {
            // Single API call — request ZIP directly so the server charges exactly one credit.
            using var req = new HttpRequestMessage(HttpMethod.Get, generateUrl);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey.Trim());
            req.Headers.Accept.ParseAdd("application/zip, application/octet-stream, application/json");
            using var rsp =
                await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

            var raw = await StreamBodyAsync(rsp.Content, cancellationToken, progress).ConfigureAwait(false);

            if (LooksLikeZip(raw))
                return OpenSteamZipResult.Successful(raw);

            // Server returned JSON — parse for error or a CDN downloadUrl.
            if (TryExtractJsonEnvelope(raw, out var explicitFail, out var err, out var dl, out var mApp))
            {
                if (explicitFail)
                    return OpenSteamZipResult.Fail(string.IsNullOrWhiteSpace(err)
                        ? "OpenSteam returned success=false for this Steam App ID."
                        : err!);

                if (!string.IsNullOrEmpty(mApp) &&
                    uint.TryParse(mApp, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var rspId) &&
                    rspId != appId)
                    return OpenSteamZipResult.Fail(
                        $"OpenSteam returned manifest for app {rspId}, but install targets {appId}.");

                // downloadUrl is a CDN link — downloading it does not consume an additional credit.
                if (!string.IsNullOrWhiteSpace(dl))
                {
                    try
                    {
                        var resolved = AbsoluteUrl(generateUrl, dl!);
                        if (!TrustedDownloadUrl.IsAllowedOpenSteamCdn(resolved, GetApiRoot()))
                        {
                            return OpenSteamZipResult.Fail(
                                "OpenSteam returned a download URL from an untrusted host.");
                        }

                        var zipBytes = await DownloadBytesAsync(resolved, cancellationToken, progress)
                            .ConfigureAwait(false);
                        return LooksLikeZip(zipBytes)
                            ? OpenSteamZipResult.Successful(zipBytes)
                            : OpenSteamZipResult.Fail("Download URL did not return a ZIP archive.");
                    }
                    catch (Exception ex)
                    {
                        return OpenSteamZipResult.Fail($"Downloading manifest ZIP failed: {ex.Message}");
                    }
                }

                return OpenSteamZipResult.Fail(string.IsNullOrWhiteSpace(err)
                    ? $"OpenSteam HTTP {(int)rsp.StatusCode} — no manifest returned."
                    : err!);
            }

            return OpenSteamZipResult.Fail(rsp.IsSuccessStatusCode
                ? "OpenSteam response was not a ZIP archive."
                : $"OpenSteam HTTP {(int)rsp.StatusCode}.");
        }
        catch (Exception ex)
        {
            return OpenSteamZipResult.Fail($"Network error contacting OpenSteam: {ex.Message}");
        }
    }

    /// <returns>false if payload is not JSON object.</returns>
    private static bool TryExtractJsonEnvelope(
        ReadOnlySpan<byte> raw,
        out bool explicitFail,
        out string? error,
        out string? downloadUrl,
        out string? manifestAppId)
    {
        explicitFail = false;
        error = null;
        downloadUrl = null;
        manifestAppId = null;

        try
        {
            var txt = System.Text.Encoding.UTF8.GetString(raw);
            using var doc = JsonDocument.Parse(txt);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return false;

            if (root.TryGetProperty("error", out var errEl))
                error = errEl.GetString();

            if (root.TryGetProperty("success", out var succ))
            {
                if (succ.ValueKind == JsonValueKind.False)
                    explicitFail = true;
                else if (succ.ValueKind == JsonValueKind.String &&
                         bool.TryParse(succ.GetString(), out var sb) &&
                         !sb)
                    explicitFail = true;
            }

            if (root.TryGetProperty("manifest", out var mf))
            {
                if (mf.TryGetProperty("downloadUrl", out var mdu))
                    downloadUrl = mdu.GetString();
                if (mf.TryGetProperty("appId", out var maid))
                {
                    manifestAppId = maid.ValueKind switch
                    {
                        JsonValueKind.Number => maid.GetUInt32().ToString(
                            System.Globalization.CultureInfo.InvariantCulture),
                        JsonValueKind.String => maid.GetString(),
                        _ => null,
                    };
                }
            }

            if (string.IsNullOrEmpty(downloadUrl) && root.TryGetProperty("downloadUrl", out var topDu))
                downloadUrl = topDu.GetString();

            return true;
        }
        catch
        {
            return false;
        }
    }

    private string GetApiRoot() => OpenSteamApiEndpoint.ResolvePrimary(settingsStore);

    private async Task<byte[]> DownloadBytesAsync(Uri url, CancellationToken cancellationToken,
        IProgress<double>? progress = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response =
            await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await StreamBodyAsync(response.Content, cancellationToken, progress).ConfigureAwait(false);
    }

    /// <summary>Reads an HTTP body as a byte array, reporting download progress (0–100) when <paramref name="progress"/> is provided.</summary>
    private static async Task<byte[]> StreamBodyAsync(HttpContent content, CancellationToken ct,
        IProgress<double>? progress = null)
    {
        if (progress is null)
        {
            await using var ms0 = new MemoryStream();
            await content.CopyToAsync(ms0, ct).ConfigureAwait(false);
            return ms0.ToArray();
        }

        var total = content.Headers.ContentLength ?? -1L;
        await using var src = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var ms  = new MemoryStream(total > 0 ? (int)Math.Min(total, 32 * 1024 * 1024) : 65536);

        var buf        = new byte[81920];
        long downloaded = 0;
        int  read;
        while ((read = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            ms.Write(buf, 0, read);
            downloaded += read;
            if (total > 0)
                progress.Report(downloaded * 100.0 / total);
        }

        return ms.ToArray();
    }

    private static Uri AbsoluteUrl(string generateUrl, string relativeOrAbsolute)
    {
        var t = relativeOrAbsolute.Trim();
        if (t.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            t.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return new Uri(t, UriKind.Absolute);
        return new Uri(new Uri(generateUrl, UriKind.Absolute), t.TrimStart('/'));
    }

    private static async Task<byte[]> ReadBodyAsync(HttpContent content, CancellationToken ct)
    {
        await using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct).ConfigureAwait(false);
        return ms.ToArray();
    }

    private static string DescribeHttp(HttpResponseMessage rsp) =>
        string.IsNullOrWhiteSpace(rsp.ReasonPhrase)
            ? $"{(int)rsp.StatusCode}"
            : $"{(int)rsp.StatusCode} {rsp.ReasonPhrase}";

    private static bool LooksLikeZip(IReadOnlyList<byte> data) =>
        data.Count >= 4 &&
        data[0] == 0x50 && data[1] == 0x4B &&   // 'P','K'
        data[2] == 0x03 && data[3] == 0x04;      // local-file header signature

    // ── Game Request ─────────────────────────────────────────────────────────

    /// <summary>
    /// POST /api/{key}/request/{appId} — formally requests a game be added to the OpenSteam registry.
    /// </summary>
    public async Task<OpenSteamRequestResult> RequestGameAsync(
        string apiKey, uint appId, string? reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return OpenSteamRequestResult.Fail("API key is missing.");

        var url    = $"{GetApiRoot().TrimEnd('/')}/api/v2/request/{appId}";

        try
        {
            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                reason = string.IsNullOrWhiteSpace(reason) ? (string?)null : reason.Trim(),
            });

            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey.Trim());
            using var rsp = await http.SendAsync(req, ct).ConfigureAwait(false);
            var raw = await rsp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            var root      = doc.RootElement;
            var status    = root.TryGetProperty("status",   out var st) ? st.GetString() : null;
            var gameName  = root.TryGetProperty("gameName", out var gn) ? gn.GetString() : null;
            var appIdStr  = root.TryGetProperty("appId",    out var ai) ? ai.GetString() : appId.ToString();
            var error     = root.TryGetProperty("error",    out var er) ? er.GetString() : null;

            return status == "sent"
                ? OpenSteamRequestResult.Success(appIdStr, gameName)
                : OpenSteamRequestResult.Fail(error ?? $"Request not sent (status: {status ?? "unknown"}).");
        }
        catch (Exception ex)
        {
            return OpenSteamRequestResult.Fail($"Network error: {ex.Message}");
        }
    }

    // ── Stats ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// GET /api/v2/stats — plan status and remaining daily credits (Bearer auth).
    /// </summary>
    public async Task<OpenSteamStatsResult> GetStatsAsync(string apiKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return new OpenSteamStatsResult { Ok = false, ErrorMessage = "No API key." };

        var url = $"{GetApiRoot().TrimEnd('/')}/api/v2/stats";

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey.Trim());
            using var rsp = await http.SendAsync(req, ct).ConfigureAwait(false);
            var raw = await rsp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!rsp.IsSuccessStatusCode)
                return new OpenSteamStatsResult { Ok = false, ErrorMessage = $"HTTP {(int)rsp.StatusCode}" };

            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            var root = doc.RootElement;

            string? plan = null, displayName = null, discordId = null, userRole = null;
            bool isStaff = false;
            int? creditsRemaining = null, creditsTotal = null;

            if (root.TryGetProperty("plan", out var p)) plan = p.GetString();

            // The API returns Discord identity nested under "user": { discordId, username, discriminator }
            var userEl = root.TryGetProperty("user", out var u) && u.ValueKind == System.Text.Json.JsonValueKind.Object
                ? u
                : root;   // fall back to root-level search for older API shapes

            if (userEl.TryGetProperty("discordId", out var did) &&
                did.ValueKind == System.Text.Json.JsonValueKind.String)
                discordId = did.GetString();

            if (userEl.TryGetProperty("username", out var un) &&
                un.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var name = un.GetString() ?? "";
                // Append discriminator only when it's a legacy 4-digit tag (not "0" or absent)
                if (userEl.TryGetProperty("discriminator", out var disc) &&
                    disc.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var tag = disc.GetString();
                    if (!string.IsNullOrWhiteSpace(tag) && tag != "0")
                        name = $"{name}#{tag}";
                }
                if (!string.IsNullOrWhiteSpace(name))
                    displayName = name;
            }

            if (userEl.TryGetProperty("role", out var roleEl) &&
                roleEl.ValueKind == System.Text.Json.JsonValueKind.String)
                userRole = roleEl.GetString();

            if (userEl.TryGetProperty("isStaff", out var staffEl))
                isStaff = staffEl.ValueKind == System.Text.Json.JsonValueKind.True ||
                          (staffEl.ValueKind == System.Text.Json.JsonValueKind.String &&
                           bool.TryParse(staffEl.GetString(), out var sb) && sb);

            // Credits/usage are nested under "usage" or "quota"
            int?     usageToday  = null;
            string?  resetAt     = null;

            var usageEl = root.TryGetProperty("usage", out var usage) && usage.ValueKind == System.Text.Json.JsonValueKind.Object
                ? usage
                : root.TryGetProperty("quota", out var quota) && quota.ValueKind == System.Text.Json.JsonValueKind.Object
                    ? quota
                    : default;

            if (usageEl.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                if (usageEl.TryGetProperty("remaining", out var rem) && rem.TryGetInt32(out var r))
                    creditsRemaining = r;
                if (usageEl.TryGetProperty("limit", out var lim) && lim.TryGetInt32(out var l))
                    creditsTotal = l;
                if (usageEl.TryGetProperty("today", out var tod) && tod.TryGetInt32(out var t))
                    usageToday = t;
                if (usageEl.TryGetProperty("used", out var usd) && usd.TryGetInt32(out var uToday))
                    usageToday = uToday;
                if (usageEl.TryGetProperty("resetAt", out var rat))
                {
                    if (rat.ValueKind == System.Text.Json.JsonValueKind.String)
                        resetAt = rat.GetString();
                    else if (rat.ValueKind == System.Text.Json.JsonValueKind.Number && rat.TryGetInt64(out var unix))
                        resetAt = DateTimeOffset.FromUnixTimeSeconds(unix).ToString("O", System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            else
            {
                // Fallback: root-level search for older API shapes
                foreach (var field in new[] { "creditsRemaining", "remaining", "credits", "dailyCreditsRemaining", "requestsRemaining" })
                {
                    if (root.TryGetProperty(field, out var v) && v.TryGetInt32(out var i))
                    { creditsRemaining = i; break; }
                }
                foreach (var field in new[] { "creditsTotal", "dailyLimit", "totalCredits", "limit", "dailyCredits" })
                {
                    if (root.TryGetProperty(field, out var v) && v.TryGetInt32(out var i))
                    { creditsTotal = i; break; }
                }
            }

            // Check for flat quota property (e.g. { "quota": 88 })
            if (root.TryGetProperty("quota", out var quotaVal))
            {
                int? flatQuota = null;
                if (quotaVal.ValueKind == JsonValueKind.Number)
                    flatQuota = quotaVal.GetInt32();
                else if (quotaVal.ValueKind == JsonValueKind.String && int.TryParse(quotaVal.GetString(), out var qInt))
                    flatQuota = qInt;

                if (flatQuota.HasValue && !creditsRemaining.HasValue)
                {
                    creditsRemaining = flatQuota.Value;
                }
            }

            return new OpenSteamStatsResult
            {
                Ok               = true,
                Plan             = plan,
                CreditsRemaining = creditsRemaining,
                CreditsTotal     = creditsTotal,
                UsageToday       = usageToday,
                ResetAt          = resetAt,
                DisplayName      = displayName,
                DiscordId        = discordId,
                Role             = userRole,
                IsStaff          = isStaff,
            };
        }
        catch (Exception ex)
        {
            return new OpenSteamStatsResult { Ok = false, ErrorMessage = ex.Message };
        }
    }

    // ── OnlineFixes ──────────────────────────────────────────────────────────

    /// <summary>
    /// GET /api/v2/onlinefix/list — retrieves the list of online multiplayer fixes.
    /// </summary>
    public async Task<List<OnlineFixItem>> GetOnlineFixesAsync(string apiKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("API key is missing.");

        var url = $"{GetApiRoot().TrimEnd('/')}/api/v2/onlinefix/list";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey.Trim());

        using var rsp = await http.SendAsync(req, ct).ConfigureAwait(false);
        rsp.EnsureSuccessStatusCode();

        var raw = await rsp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        var list = new List<OnlineFixItem>();
        JsonElement arrayEl = FindArray(root);

        static JsonElement FindArray(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Array)
                return element;

            if (element.ValueKind == JsonValueKind.Object)
            {
                // 1. Check preferred array properties at this level
                var preferredKeys = new[] { "games", "fixes", "data", "items", "results", "list" };
                foreach (var key in preferredKeys)
                {
                    if (element.TryGetProperty(key, out var prop))
                    {
                        if (prop.ValueKind == JsonValueKind.Array)
                            return prop;
                    }
                }

                // 2. Check if a preferred object property exists and search inside it
                var preferredObjects = new[] { "data", "fixes", "results" };
                foreach (var key in preferredObjects)
                {
                    if (element.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.Object)
                    {
                        var nested = FindArray(prop);
                        if (nested.ValueKind == JsonValueKind.Array)
                            return nested;
                    }
                }

                // 3. Fallback: Search any child property for an array recursively
                foreach (var prop in element.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Array)
                        return prop.Value;
                    if (prop.Value.ValueKind == JsonValueKind.Object)
                    {
                        var nested = FindArray(prop.Value);
                        if (nested.ValueKind == JsonValueKind.Array)
                            return nested;
                    }
                }
            }

            return default;
        }

        if (arrayEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arrayEl.EnumerateArray())
            {
                string? name = null;
                string? title = null;
                string? size = null;
                string? version = null;
                string? fileName = null;
                string? imageUrl = null;
                uint? steamAppId = null;

                if (item.ValueKind == JsonValueKind.Object)
                {
                    if (item.TryGetProperty("endpoint", out var epProp))
                    {
                        var epVal = epProp.GetString();
                        if (!string.IsNullOrWhiteSpace(epVal))
                        {
                            var nameFromEp = epVal;
                            if (nameFromEp.Contains('/'))
                                nameFromEp = nameFromEp.Split('/').Last();
                            name = nameFromEp;
                        }
                    }
                    if (item.TryGetProperty("name", out var nProp)) name = nProp.GetString() ?? name;
                    if (item.TryGetProperty("title", out var tProp)) title = tProp.GetString();
                    if (item.TryGetProperty("displayName", out var dnProp)) title ??= dnProp.GetString();
                    if (item.TryGetProperty("size", out var sProp)) size = sProp.GetString();
                    if (item.TryGetProperty("fileSize", out var fsProp)) size = fsProp.GetString() ?? size;
                    if (item.TryGetProperty("version", out var vProp)) version = vProp.GetString();
                    if (item.TryGetProperty("fileName", out var fnProp)) fileName = fnProp.GetString();
                    if (item.TryGetProperty("imageUrl", out var imgProp)) imageUrl = imgProp.GetString();
                    if (item.TryGetProperty("headerImageUrl", out var hdrProp)) imageUrl ??= hdrProp.GetString();
                    if (item.TryGetProperty("steamAppId", out var sidProp) && sidProp.TryGetUInt32(out var sid))
                        steamAppId = sid;
                }
                else if (item.ValueKind == JsonValueKind.String)
                {
                    name = item.GetString();
                }

                if (!string.IsNullOrWhiteSpace(name))
                {
                    list.Add(new OnlineFixItem
                    {
                        Name = name,
                        Title = string.IsNullOrWhiteSpace(title)
                            ? OnlineFixDisplayHelper.ParseDisplayTitle(name, fileName)
                            : title,
                        Size = size,
                        Version = version,
                        FileName = fileName,
                        ImageUrl = imageUrl,
                        SteamAppId = steamAppId,
                    });
                }
            }
        }

        return list;
    }

    /// <summary>
    /// GET /api/v2/onlinefix/download/{name} — streams the direct zip download for a fix.
    /// </summary>
    public async Task DownloadOnlineFixAsync(string apiKey, string fixName, Stream destinationStream,
        CancellationToken cancellationToken, IProgress<double>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("API key is missing.");

        var url = $"{GetApiRoot().TrimEnd('/')}/api/v2/onlinefix/download/{Uri.EscapeDataString(fixName)}";
        
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey.Trim());
        req.Headers.Accept.ParseAdd("application/zip, application/octet-stream");

        using var rsp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        rsp.EnsureSuccessStatusCode();

        var total = rsp.Content.Headers.ContentLength ?? -1L;
        await using var src = await rsp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        var buf = new byte[81920];
        long downloaded = 0;
        int read;
        while ((read = await src.ReadAsync(buf, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await destinationStream.WriteAsync(buf.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            downloaded += read;
            if (total > 0 && progress != null)
                progress.Report(downloaded * 100.0 / total);
        }
    }
}

public sealed class OpenSteamZipResult(bool ok, byte[]? zipBytes, string? errorMessage)
{
    public bool Ok { get; } = ok;
    public byte[]? ZipBytes { get; } = zipBytes;
    public string? ErrorMessage { get; } = errorMessage;

    public static OpenSteamZipResult Successful(byte[] bytes) => new(true, bytes, null);
    public static OpenSteamZipResult Fail(string msg) => new(false, null, msg);
}

public sealed class OpenSteamRequestResult
{
    public bool    Sent         { get; init; }
    public string? GameName     { get; init; }
    public string? AppId        { get; init; }
    public string? ErrorMessage { get; init; }

    public static OpenSteamRequestResult Success(string? appId, string? gameName) =>
        new() { Sent = true, AppId = appId, GameName = gameName };
    public static OpenSteamRequestResult Fail(string msg) =>
        new() { Sent = false, ErrorMessage = msg };
}

public sealed class OpenSteamStatsResult
{
    public bool    Ok               { get; init; }
    public string? Plan             { get; init; }
    public int?    CreditsRemaining { get; init; }
    public int?    CreditsTotal     { get; init; }
    /// <summary>Generations already used today (usage.today).</summary>
    public int?    UsageToday       { get; init; }
    /// <summary>ISO-8601 timestamp when the daily quota resets (usage.resetAt).</summary>
    public string? ResetAt          { get; init; }
    public string? DisplayName      { get; init; }
    /// <summary>Raw Discord snowflake ID — used as last-resort display when no human-readable name is available.</summary>
    public string? DiscordId        { get; init; }
    /// <summary>Role string from the API: "USER", "ADMIN", "OWNER", etc.</summary>
    public string? Role             { get; init; }
    /// <summary>True when role is anything other than USER (staff/admin/owner).</summary>
    public bool    IsStaff          { get; init; }
    public string? ErrorMessage     { get; init; }
}
