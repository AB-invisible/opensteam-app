namespace ManifestApp.Core;

/// <summary>Validates download URLs against trusted host allowlists.</summary>
public static class TrustedDownloadUrl
{
    private static readonly string[] GitHubReleaseHosts =
    [
        "github.com",
        "objects.githubusercontent.com",
        "githubusercontent.com",
    ];

    /// <summary>GitHub release asset downloads (auto-updater).</summary>
    public static bool IsAllowedGitHubReleaseAsset(Uri url)
    {
        if (!url.IsAbsoluteUri)
            return false;
        if (!string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;

        return GitHubReleaseHosts.Any(h =>
            string.Equals(url.Host, h, StringComparison.OrdinalIgnoreCase)
            || url.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Manifest ZIP CDN links returned by OpenSteam generate API.</summary>
    public static bool IsAllowedOpenSteamCdn(Uri url, string apiRoot)
    {
        if (!url.IsAbsoluteUri)
            return false;
        if (!string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;

        if (IsHostUnderDomain(url.Host, "opensteam.lol"))
            return true;

        if (IsHostUnderDomain(url.Host, "gamegen.lol"))
            return true;

        if (Uri.TryCreate(apiRoot, UriKind.Absolute, out var rootUri)
            && string.Equals(url.Host, rootUri.Host, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static bool IsHostUnderDomain(string host, string domain) =>
        string.Equals(host, domain, StringComparison.OrdinalIgnoreCase)
        || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
}
