using System.Net.Http.Headers;
using System.Text.Json.Serialization;

namespace ManifestApp.Core;

/// <summary>
/// Queries the public Steam storefront search endpoint to resolve typed game titles to Steam App IDs.
/// </summary>
public sealed class SteamStoreSearchClient(HttpClient http)
{
    private static readonly System.Text.Json.JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public async Task<IReadOnlyList<SteamStoreAppHit>> SearchAppsAsync(string searchText,
        CancellationToken cancellationToken = default)
    {
        var query = searchText.Trim();
        if (query.Length == 0)
            return [];

        var termEncoded = Uri.EscapeDataString(query);

        using var req = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://store.steampowered.com/api/storesearch/?term={termEncoded}&cc=US&l=en");
        req.Headers.Accept.Add(MediaTypeWithQualityHeaderValue.Parse("application/json"));
        req.Headers.TryAddWithoutValidation("Referer", "https://store.steampowered.com/");

        using var resp = await http
            .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var envelope = await System.Text.Json.JsonSerializer
            .DeserializeAsync<SteamStoreSearchEnvelope>(stream, SerializerOptions, cancellationToken)
            .ConfigureAwait(false);

        if (envelope?.Items is null || envelope.Items.Count == 0)
            return [];

        return envelope.Items
            .Where(i => string.Equals(i.Type, "app", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(i.Name))
            .Select(i => new SteamStoreAppHit(i.Id, i.Name.Trim(), NormalizeOptionalUrl(i.TinyImage)))
            .ToList();
    }

    private static string? NormalizeOptionalUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        return url.StartsWith("//", StringComparison.Ordinal) ? "https:" + url : url;
    }

    private sealed class SteamStoreSearchEnvelope
    {
        [JsonPropertyName("total")]
        public int Total { get; init; }

        [JsonPropertyName("items")]
        public List<SteamStoreItemDto>? Items { get; init; }
    }

    private sealed class SteamStoreItemDto
    {
        [JsonPropertyName("type")]
        public string Type { get; init; } = "";

        [JsonPropertyName("name")]
        public string Name { get; init; } = "";

        [JsonPropertyName("id")]
        public uint Id { get; init; }

        [JsonPropertyName("tiny_image")]
        public string? TinyImage { get; init; }
    }
}

public sealed record SteamStoreAppHit(uint AppId, string Name, string? TinyImageHttpsUrl);
