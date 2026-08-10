namespace ManifestApp.Core;

/// <summary>
/// Resolves the OpenSteam HTTPS/HTTP API root used by pairing, activation, and manifest calls.
/// </summary>
public static class OpenSteamApiEndpoint
{
    /// <summary>Production API host (Render). Tried before legacy opensteam.lol URLs.</summary>
    public const string ProductionApiBaseUrl = "https://manifest-web-ylio.onrender.com";

    private static readonly string[] DefaultCandidates =
    [
        ProductionApiBaseUrl,
        "http://127.0.0.1:3000",
        "http://opensteam.lol",
    ];

    public static IReadOnlyList<string> GetCandidates(SettingsStore settingsStore)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();

        void Add(string? raw)
        {
            var normalized = NormalizeBaseUrl(raw);
            if (normalized is null || !seen.Add(normalized))
                return;
            list.Add(normalized);
        }

        Add(ProductionApiBaseUrl);
        Add(settingsStore.Load().OpenSteamApiBaseUrl);
        Add(ReadOptionalFile(Path.Combine(AppPaths.LocalRoot, "api-base-url.txt")));
        Add(ReadOptionalFile(Path.Combine(AppPaths.LocalRoot, "public-url.txt")));

        var desktopRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Desktop",
            "opensteam-web-data",
            "public-url.txt");
        Add(ReadOptionalFile(desktopRoot));

        foreach (var candidate in DefaultCandidates)
            Add(candidate);

        return list;
    }

    public static string ResolvePrimary(SettingsStore settingsStore)
    {
        var preferred = ReadOptionalFile(Path.Combine(AppPaths.LocalRoot, "api-base-url.txt"));
        var normalizedPreferred = NormalizeBaseUrl(preferred);
        if (normalizedPreferred is not null)
            return normalizedPreferred;

        var candidates = GetCandidates(settingsStore);
        return candidates.Count > 0 ? candidates[0] : ProductionApiBaseUrl;
    }

    public static async Task<(string BaseUrl, string? Error)> ResolveReachableAsync(
        HttpClient http,
        SettingsStore settingsStore,
        Func<HttpClient, string, CancellationToken, Task<(bool Ok, string? Error)>> probe,
        CancellationToken cancellationToken = default)
    {
        string? lastError = null;

        foreach (var baseUrl in GetCandidates(settingsStore))
        {
            var (ok, error) = await probe(http, baseUrl, cancellationToken).ConfigureAwait(false);
            if (ok)
            {
                PersistPreferredBaseUrl(baseUrl);
                return (baseUrl, null);
            }

            lastError = error;
        }

        return (ResolvePrimary(settingsStore), lastError ?? "Could not reach OpenSteam.");
    }

    internal static void PersistPreferredBaseUrl(string baseUrl)
    {
        try
        {
            AppPaths.EnsureLayout();
            var path = Path.Combine(AppPaths.LocalRoot, "api-base-url.txt");
            var normalized = NormalizeBaseUrl(baseUrl);
            if (normalized is null)
                return;

            if (File.Exists(path) && string.Equals(File.ReadAllText(path).Trim(), normalized, StringComparison.OrdinalIgnoreCase))
                return;

            File.WriteAllText(path, normalized);
        }
        catch
        {
            /* best effort */
        }
    }

    private static string? ReadOptionalFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? NormalizeBaseUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var trimmed = raw.Trim().TrimEnd('/');
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return trimmed;

        return $"https://{trimmed}";
    }
}
