using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ManifestApp.Core;

namespace ManifestApp.Services;

/// <summary>
/// Checks GitHub Releases for a newer version and can download + apply the update.
/// Falls back to latest.json and the OpenSteam API when GitHub rate-limits the app.
/// </summary>
internal sealed class UpdateService
{
    private const string GitHubRepo   = "AB-invisible/opensteam-app";
    private const string ExeAssetName = "OpenSteamApp.exe";

    private static readonly Uri GitHubLatestApi =
        new($"https://api.github.com/repos/{GitHubRepo}/releases/latest");

    private static readonly Uri LatestJsonFallback =
        new($"https://raw.githubusercontent.com/{GitHubRepo}/main/latest.json");

    private static readonly Uri OpenSteamLatestApi =
        new("https://manifest-web-ylio.onrender.com/api/v2/app/latest");

    private readonly HttpClient _http;

    internal UpdateService(HttpClient http) => _http = http;

    internal static Version CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
            return NormalizeVersion(v);
        }
    }

    internal static string CurrentVersionString
    {
        get
        {
            var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();

            var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;

            if (!string.IsNullOrEmpty(info))
            {
                var plus = info.IndexOf('+');
                return plus > 0 ? info[..plus] : info;
            }

            var v = assembly.GetName().Version ?? new Version(1, 0, 0);
            return NormalizeVersion(v).ToString(3);
        }
    }

    private static Version NormalizeVersion(Version v)
        => new(v.Major, v.Minor, v.Build < 0 ? 0 : v.Build);

    internal async Task<UpdateCheckOutcome> CheckDetailedAsync(CancellationToken ct = default)
    {
        string? lastError = null;

        (string Name, Func<CancellationToken, Task<RemoteReleaseInfo?>> Fetch)[] sources =
        [
            ("GitHub API", TryFetchGitHubLatestAsync),
            ("latest.json", TryFetchLatestJsonAsync),
            ("OpenSteam API", TryFetchOpenSteamLatestAsync),
        ];

        foreach (var source in sources)
        {
            try
            {
                var info = await source.Fetch(ct).ConfigureAwait(false);
                if (info is null)
                {
                    lastError ??= $"{source.Name} returned no release data.";
                    continue;
                }

                var result = await BuildUpdateResultAsync(info, ct).ConfigureAwait(false);
                if (result is not null)
                    return UpdateCheckOutcome.Ok(result);

                lastError = $"{source.Name} did not produce a valid release.";
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                AppLogger.LogException($"UpdateService.CheckDetailedAsync ({source.Name})", ex);
            }
        }

        return UpdateCheckOutcome.Fail(lastError ?? "Could not reach update servers.");
    }

    internal async Task<UpdateResult?> CheckAsync(CancellationToken ct = default)
    {
        var outcome = await CheckDetailedAsync(ct).ConfigureAwait(false);
        return outcome.Result;
    }

    private async Task<RemoteReleaseInfo?> TryFetchGitHubLatestAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, GitHubLatestApi);
        req.Headers.Add("Accept", "application/vnd.github+json");
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"GitHub API HTTP {(int)resp.StatusCode}.");

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var release = JsonSerializer.Deserialize(json, UpdateJsonContext.Default.GitHubRelease);
        if (release is null || string.IsNullOrWhiteSpace(release.TagName))
            return null;

        var exeAsset = release.Assets?.FirstOrDefault(a =>
            string.Equals(a.Name, ExeAssetName, StringComparison.OrdinalIgnoreCase));

        return new RemoteReleaseInfo(
            release.TagName,
            release.HtmlUrl ?? $"https://github.com/{GitHubRepo}/releases/latest",
            exeAsset?.BrowserDownloadUrl,
            release.Body);
    }

    private async Task<RemoteReleaseInfo?> TryFetchLatestJsonAsync(CancellationToken ct)
    {
        using var resp = await _http.GetAsync(LatestJsonFallback, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"latest.json HTTP {(int)resp.StatusCode}.");

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var doc = JsonSerializer.Deserialize(json, UpdateJsonContext.Default.LatestReleaseDocument);
        if (doc is null || string.IsNullOrWhiteSpace(doc.Version))
            return null;

        var tag = string.IsNullOrWhiteSpace(doc.Tag) ? $"v{doc.Version.TrimStart('v', 'V')}" : doc.Tag!;
        var downloadUrl = doc.DownloadUrl
            ?? $"https://github.com/{GitHubRepo}/releases/download/{tag}/{ExeAssetName}";

        return new RemoteReleaseInfo(
            tag,
            doc.ReleaseUrl ?? $"https://github.com/{GitHubRepo}/releases/tag/{tag}",
            downloadUrl,
            doc.Body);
    }

    private async Task<RemoteReleaseInfo?> TryFetchOpenSteamLatestAsync(CancellationToken ct)
    {
        using var resp = await _http.GetAsync(OpenSteamLatestApi, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"OpenSteam API HTTP {(int)resp.StatusCode}.");

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var doc = JsonSerializer.Deserialize(json, UpdateJsonContext.Default.LatestReleaseDocument);
        if (doc is null || string.IsNullOrWhiteSpace(doc.Version))
            return null;

        var tag = string.IsNullOrWhiteSpace(doc.Tag) ? $"v{doc.Version.TrimStart('v', 'V')}" : doc.Tag!;
        var downloadUrl = doc.DownloadUrl
            ?? $"https://github.com/{GitHubRepo}/releases/download/{tag}/{ExeAssetName}";

        return new RemoteReleaseInfo(
            tag,
            doc.ReleaseUrl ?? $"https://github.com/{GitHubRepo}/releases/tag/{tag}",
            downloadUrl,
            doc.Body);
    }

    private async Task<UpdateResult?> BuildUpdateResultAsync(RemoteReleaseInfo release, CancellationToken ct)
    {
        var raw = release.TagName.TrimStart('v', 'V');
        if (!Version.TryParse(raw, out var parsedLatest))
            return null;

        var latest = NormalizeVersion(parsedLatest);

        string? sha256Hex = null;
        if (!string.IsNullOrWhiteSpace(release.ExeDownloadUrl))
        {
            var shaUrl = release.ExeDownloadUrl + ".sha256";
            sha256Hex = await TryFetchSha256HexAsync(shaUrl, ct).ConfigureAwait(false);
        }

        string? localSha256Hex = null;
        var currentExe = Process.GetCurrentProcess().MainModule?.FileName;
        if (!string.IsNullOrWhiteSpace(currentExe) && File.Exists(currentExe))
            localSha256Hex = await ComputeSha256HexAsync(currentExe, ct).ConfigureAwait(false);

        var versionIsNewer = latest > CurrentVersion;
        var buildChanged = sha256Hex is not null
            && localSha256Hex is not null
            && !string.Equals(sha256Hex, localSha256Hex, StringComparison.OrdinalIgnoreCase);

        AppLogger.Log(
            $"Update check completed. Current={CurrentVersion.ToString(3)}, Latest={latest.ToString(3)}, " +
            $"UpdateAvailable={versionIsNewer || buildChanged}, HasExeAsset={release.ExeDownloadUrl is not null}, " +
            $"HasSha256={sha256Hex is not null}.");

        return new UpdateResult(
            CurrentVersion:    CurrentVersion,
            LatestVersion:     latest,
            IsUpdateAvailable: versionIsNewer || buildChanged,
            ReleaseUrl:        release.ReleaseUrl,
            ExeDownloadUrl:    release.ExeDownloadUrl,
            ExeSha256Hex:      sha256Hex,
            ReleaseNotes:      release.Body);
    }

    internal async Task<string?> DownloadUpdateAsync(
        string exeDownloadUrl,
        IProgress<double>? progress = null,
        string? expectedSha256Hex = null,
        CancellationToken ct = default)
    {
        try
        {
            if (!Uri.TryCreate(exeDownloadUrl, UriKind.Absolute, out var uri)
                || !TrustedDownloadUrl.IsAllowedGitHubReleaseAsset(uri))
            {
                AppLogger.Log($"Update download rejected — untrusted URL: {exeDownloadUrl}");
                return null;
            }

            var tempExe = Path.Combine(Path.GetTempPath(), $"ManifestApp_update_{Guid.NewGuid():N}.exe");
            AppLogger.Log($"Starting update download to {tempExe}.");

            using var resp = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            var total = resp.Content.Headers.ContentLength ?? -1L;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(tempExe, FileMode.Create, FileAccess.Write, FileShare.None);

            var buf         = new byte[81920];
            long downloaded = 0;
            int  read;

            while ((read = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, read), ct);
                downloaded += read;
                if (total > 0)
                    progress?.Report((double)downloaded / total * 100.0);
            }

            await dst.FlushAsync(ct);
            dst.Close();

            if (!string.IsNullOrWhiteSpace(expectedSha256Hex))
            {
                var actual = await ComputeSha256HexAsync(tempExe, ct).ConfigureAwait(false);
                if (!string.Equals(actual, expectedSha256Hex.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    AppLogger.Log($"Update SHA-256 mismatch. Expected={expectedSha256Hex}, Actual={actual}");
                    try { File.Delete(tempExe); } catch { /* best effort */ }
                    return null;
                }
                AppLogger.Log("Update SHA-256 verification passed.");
            }

            var currentExe = Process.GetCurrentProcess().MainModule?.FileName
                             ?? throw new InvalidOperationException("Cannot resolve current exe path.");

            var updaterPath = Path.Combine(Path.GetTempPath(), $"ManifestApp_updater_{Guid.NewGuid():N}.bat");
            var script =
                "@echo off\r\n" +
                "set /a tries=0\r\n" +
                ":retry\r\n" +
                "ping -n 2 127.0.0.1 > nul\r\n" +
                $"move /y \"{tempExe}\" \"{currentExe}\" > nul 2>&1\r\n" +
                "if exist \"" + tempExe + "\" (\r\n" +
                "  set /a tries+=1\r\n" +
                "  if %tries% lss 15 goto retry\r\n" +
                "  exit /b 1\r\n" +
                ")\r\n" +
                $"start \"\" \"{currentExe}\"\r\n" +
                "del \"%~f0\"\r\n";

            await File.WriteAllTextAsync(updaterPath, script, ct);
            AppLogger.Log($"Update download completed. Updater script written to {updaterPath}; target exe is {currentExe}.");
            return updaterPath;
        }
        catch (Exception ex)
        {
            AppLogger.LogException("UpdateService.DownloadUpdateAsync", ex);
            return null;
        }
    }

    internal static void ApplyUpdate(string updaterBatPath)
    {
        AppLogger.Log($"Applying update via {updaterBatPath}. App will exit and updater will relaunch it.");

        Process.Start(new ProcessStartInfo
        {
            FileName        = "cmd.exe",
            Arguments       = $"/c \"{updaterBatPath}\"",
            UseShellExecute = false,
            CreateNoWindow  = true,
        });

        Environment.Exit(0);
    }

    private async Task<string?> TryFetchSha256HexAsync(string sha256AssetUrl, CancellationToken ct)
    {
        try
        {
            if (!Uri.TryCreate(sha256AssetUrl, UriKind.Absolute, out var uri)
                || !TrustedDownloadUrl.IsAllowedGitHubReleaseAsset(uri))
                return null;

            var text = (await _http.GetStringAsync(uri, ct).ConfigureAwait(false)).Trim();
            if (string.IsNullOrEmpty(text)) return null;

            var token = text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (string.IsNullOrEmpty(token) || token.Length != 64) return null;
            return token;
        }
        catch (Exception ex)
        {
            AppLogger.LogException("UpdateService.TryFetchSha256HexAsync", ex);
            return null;
        }
    }

    private static async Task<string> ComputeSha256HexAsync(string filePath, CancellationToken ct)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private sealed record RemoteReleaseInfo(
        string TagName,
        string ReleaseUrl,
        string? ExeDownloadUrl,
        string? Body);
}

internal sealed record UpdateCheckOutcome(UpdateResult? Result, string? ErrorMessage)
{
    public static UpdateCheckOutcome Ok(UpdateResult result) => new(result, null);
    public static UpdateCheckOutcome Fail(string message) => new(null, message);
}

internal sealed record UpdateResult(
    Version  CurrentVersion,
    Version  LatestVersion,
    bool     IsUpdateAvailable,
    string   ReleaseUrl,
    string?  ExeDownloadUrl,
    string?  ExeSha256Hex,
    string?  ReleaseNotes);

internal sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")] public string?                   TagName { get; set; }
    [JsonPropertyName("html_url")] public string?                   HtmlUrl { get; set; }
    [JsonPropertyName("body")]     public string?                   Body    { get; set; }
    [JsonPropertyName("assets")]   public List<GitHubReleaseAsset>? Assets  { get; set; }
}

internal sealed class GitHubReleaseAsset
{
    [JsonPropertyName("name")]                  public string? Name                { get; set; }
    [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl  { get; set; }
}

internal sealed class LatestReleaseDocument
{
    [JsonPropertyName("version")]     public string? Version     { get; set; }
    [JsonPropertyName("tag")]         public string? Tag         { get; set; }
    [JsonPropertyName("downloadUrl")] public string? DownloadUrl { get; set; }
    [JsonPropertyName("releaseUrl")]  public string? ReleaseUrl  { get; set; }
    [JsonPropertyName("body")]        public string? Body        { get; set; }
}

[JsonSerializable(typeof(GitHubRelease))]
[JsonSerializable(typeof(GitHubReleaseAsset))]
[JsonSerializable(typeof(LatestReleaseDocument))]
internal sealed partial class UpdateJsonContext : JsonSerializerContext { }
