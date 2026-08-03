using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ManifestApp.Core;

namespace ManifestApp.Services;

/// <summary>
/// Checks GitHub Releases for a newer version and can download + apply the update.
/// </summary>
internal sealed class UpdateService
{
    private const string GitHubRepo   = "AB-invisible/opensteam-app";
    // Asset name that must be attached to every GitHub Release
    private const string ExeAssetName = "OpenSteamApp.exe";

    private readonly HttpClient _http;

    internal UpdateService(HttpClient http) => _http = http;

    // ── Version helpers ───────────────────────────────────────────────────────

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

    // ── Check for update ──────────────────────────────────────────────────────

    internal async Task<UpdateResult?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(
                HttpMethod.Get,
                $"https://api.github.com/repos/{GitHubRepo}/releases/latest");

            req.Headers.Add("Accept", "application/vnd.github+json");
            req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            using var resp = await _http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadAsStringAsync(ct);
            var release = JsonSerializer.Deserialize(json, UpdateJsonContext.Default.GitHubRelease);

            if (release is null || string.IsNullOrWhiteSpace(release.TagName))
                return null;

            var raw = release.TagName.TrimStart('v', 'V');
            if (!Version.TryParse(raw, out var parsedLatest))
                return null;

            var latest = NormalizeVersion(parsedLatest);

            var exeAsset = release.Assets?.FirstOrDefault(a =>
                string.Equals(a.Name, ExeAssetName, StringComparison.OrdinalIgnoreCase));

            string? sha256Hex = null;
            var shaAsset = release.Assets?.FirstOrDefault(a =>
                string.Equals(a.Name, ExeAssetName + ".sha256", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(shaAsset?.BrowserDownloadUrl))
                sha256Hex = await TryFetchSha256HexAsync(shaAsset.BrowserDownloadUrl, ct).ConfigureAwait(false);

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
                $"UpdateAvailable={versionIsNewer || buildChanged}, HasExeAsset={exeAsset?.BrowserDownloadUrl is not null}, " +
                $"HasSha256={sha256Hex is not null}.");

            return new UpdateResult(
                CurrentVersion:    CurrentVersion,
                LatestVersion:     latest,
                IsUpdateAvailable: versionIsNewer || buildChanged,
                ReleaseUrl:        release.HtmlUrl ?? $"https://github.com/{GitHubRepo}/releases/latest",
                ExeDownloadUrl:    exeAsset?.BrowserDownloadUrl,
                ExeSha256Hex:      sha256Hex,
                ReleaseNotes:      release.Body);
        }
        catch (Exception ex)
        {
            AppLogger.LogException("UpdateService.CheckAsync", ex);
            return null;
        }
    }

    // ── Download update ───────────────────────────────────────────────────────

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

[JsonSerializable(typeof(GitHubRelease))]
[JsonSerializable(typeof(GitHubReleaseAsset))]
internal sealed partial class UpdateJsonContext : JsonSerializerContext { }
