using System.Text.Json;
using System.Text.RegularExpressions;

namespace ManifestApp.Core;

/// <summary>Steam Store <c>/api/appdetails</c> helper for listing flags + rich page data.</summary>
public sealed class SteamStoreAppDetailsClient(HttpClient http)
{
    public async Task<SteamAppListingInfo> GetListingAsync(uint appId,
        CancellationToken cancellationToken)
    {
        var url =
            $"https://store.steampowered.com/api/appdetails?appids={appId}&filters=basic,languages&cc=US&l=en";

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Referer", "https://store.steampowered.com/");

            using var resp =
                await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                return SteamAppListingInfo.Unavailable($"{(int)resp.StatusCode} {resp.ReasonPhrase}");

            var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            var idKey = appId.ToString();
            if (!doc.RootElement.TryGetProperty(idKey, out var appNode))
                return SteamAppListingInfo.Unavailable("Store listing not found.");

            if (!appNode.TryGetProperty("success", out var succ) || succ.ValueKind != JsonValueKind.True ||
                !appNode.TryGetProperty("data", out var data))
                return SteamAppListingInfo.Unavailable("Steam has no public store page data for this App ID.");

            var name = data.TryGetProperty("name", out var n) ? n.GetString() : null;
            bool comingSoon = false;
            string? dateLine = null;
            if (data.TryGetProperty("release_date", out var rd))
            {
                if (rd.TryGetProperty("coming_soon", out var cs) &&
                    cs.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    comingSoon = cs.GetBoolean();

                if (rd.TryGetProperty("date", out var d) && d.ValueKind == JsonValueKind.String)
                    dateLine = d.GetString();
            }

            return new SteamAppListingInfo(
                Parsed: true,
                NameOnStore: name,
                ComingSoon: comingSoon,
                ReleaseDateCaption: dateLine,
                Diagnostics: null);
        }
        catch (Exception ex)
        {
            return SteamAppListingInfo.Unavailable(ex.Message);
        }
    }

    /// <summary>
    /// Fetches the full Steam Store page payload (description, header image, screenshots, movies,
    /// developers, publishers, genres, release date). Returns null when Steam has no public page
    /// for the App ID, or when the request fails.
    /// </summary>
    public async Task<SteamAppFullDetails?> GetFullDetailsAsync(uint appId,
        CancellationToken cancellationToken)
    {
        var url = $"https://store.steampowered.com/api/appdetails?appids={appId}&cc=US&l=en";

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Referer", "https://store.steampowered.com/");

            using var resp =
                await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                return null;

            var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty(appId.ToString(), out var node))
                return null;
            if (!node.TryGetProperty("success", out var succ) || succ.ValueKind != JsonValueKind.True)
                return null;
            if (!node.TryGetProperty("data", out var data))
                return null;

            return ParseDetails(appId, data);
        }
        catch
        {
            return null;
        }
    }

    private static SteamAppFullDetails ParseDetails(uint appId, JsonElement data)
    {
        var name        = ReadString(data, "name") ?? $"Steam App {appId}";
        var shortDesc   = StripHtml(ReadString(data, "short_description"));
        var about       = StripHtml(ReadString(data, "about_the_game"));
        var header      = ReadString(data, "header_image");
        var capsule     = ReadString(data, "capsule_image");

        var developers  = ReadStringArray(data, "developers");
        var publishers  = ReadStringArray(data, "publishers");

        var genres = new List<string>();
        if (data.TryGetProperty("genres", out var gen) && gen.ValueKind == JsonValueKind.Array)
        {
            foreach (var g in gen.EnumerateArray())
            {
                var desc = ReadString(g, "description");
                if (!string.IsNullOrWhiteSpace(desc))
                    genres.Add(desc!);
            }
        }

        string? releaseText = null;
        bool comingSoon = false;
        if (data.TryGetProperty("release_date", out var rd))
        {
            releaseText = ReadString(rd, "date");
            if (rd.TryGetProperty("coming_soon", out var cs) &&
                cs.ValueKind is JsonValueKind.True or JsonValueKind.False)
                comingSoon = cs.GetBoolean();
        }

        var screenshots = new List<SteamScreenshot>();
        if (data.TryGetProperty("screenshots", out var sh) && sh.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in sh.EnumerateArray())
            {
                var id    = s.TryGetProperty("id", out var i) && i.TryGetInt32(out var iv) ? iv : 0;
                var thumb = ReadString(s, "path_thumbnail");
                var full  = ReadString(s, "path_full");
                if (!string.IsNullOrEmpty(thumb) && !string.IsNullOrEmpty(full))
                    screenshots.Add(new SteamScreenshot(id, thumb!, full!));
            }
        }

        var movies = new List<SteamMovie>();
        if (data.TryGetProperty("movies", out var mv) && mv.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in mv.EnumerateArray())
            {
                var id    = m.TryGetProperty("id", out var i) && i.TryGetInt32(out var iv) ? iv : 0;
                var mname = ReadString(m, "name") ?? "";
                var thumb = ReadString(m, "thumbnail");

                // Legacy progressive mp4/webm fields (older games)
                string? mp4Low  = null;
                string? mp4High = null;
                if (m.TryGetProperty("mp4", out var mp4) && mp4.ValueKind == JsonValueKind.Object)
                {
                    mp4Low  = ReadString(mp4, "480");
                    mp4High = ReadString(mp4, "max");
                }
                string? webmLow  = null;
                string? webmHigh = null;
                if (m.TryGetProperty("webm", out var webm) && webm.ValueKind == JsonValueKind.Object)
                {
                    webmLow  = ReadString(webm, "480");
                    webmHigh = ReadString(webm, "max");
                }

                // Modern adaptive streaming fields (new releases — Steam no longer returns mp4 for these)
                var hlsH264 = ReadString(m, "hls_h264");
                var dashH264 = ReadString(m, "dash_h264");

                // Only add if we have *some* playable URL (or at least a thumbnail with a stream)
                if (!string.IsNullOrEmpty(thumb))
                    movies.Add(new SteamMovie(id, mname, thumb!,
                        mp4Low, mp4High, webmLow, webmHigh, hlsH264, dashH264));
            }
        }

        bool win = false, mac = false, lin = false;
        if (data.TryGetProperty("platforms", out var p) && p.ValueKind == JsonValueKind.Object)
        {
            win = p.TryGetProperty("windows", out var pw) && pw.ValueKind == JsonValueKind.True;
            mac = p.TryGetProperty("mac",     out var pm) && pm.ValueKind == JsonValueKind.True;
            lin = p.TryGetProperty("linux",   out var pl) && pl.ValueKind == JsonValueKind.True;
        }

        return new SteamAppFullDetails(
            AppId:             appId,
            Name:              name,
            ShortDescription:  shortDesc,
            AboutText:         about,
            HeaderImageUrl:    header,
            CapsuleImageUrl:   capsule,
            Developers:        developers,
            Publishers:        publishers,
            Genres:            genres,
            ReleaseDateText:   releaseText,
            ComingSoon:        comingSoon,
            Screenshots:       screenshots,
            Movies:            movies,
            WindowsSupported:  win,
            MacSupported:      mac,
            LinuxSupported:    lin);
    }

    private static string? ReadString(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static IReadOnlyList<string> ReadStringArray(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var list = new List<string>();
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.String) continue;
            var v = el.GetString();
            if (!string.IsNullOrWhiteSpace(v))
                list.Add(v!);
        }
        return list;
    }

    private static string StripHtml(string? html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var noTags  = Regex.Replace(html, "<[^>]+>", " ");
        var decoded = System.Net.WebUtility.HtmlDecode(noTags);
        return Regex.Replace(decoded, @"\s+", " ").Trim();
    }
}

public sealed record SteamAppListingInfo(
    bool Parsed,
    string? NameOnStore,
    bool ComingSoon,
    string? ReleaseDateCaption,
    string? Diagnostics)
{
    internal static SteamAppListingInfo Unavailable(string? why) =>
        new(false, null, false, null, why);
}

public sealed record SteamAppFullDetails(
    uint AppId,
    string Name,
    string ShortDescription,
    string AboutText,
    string? HeaderImageUrl,
    string? CapsuleImageUrl,
    IReadOnlyList<string> Developers,
    IReadOnlyList<string> Publishers,
    IReadOnlyList<string> Genres,
    string? ReleaseDateText,
    bool ComingSoon,
    IReadOnlyList<SteamScreenshot> Screenshots,
    IReadOnlyList<SteamMovie> Movies,
    bool WindowsSupported,
    bool MacSupported,
    bool LinuxSupported);

public sealed record SteamScreenshot(int Id, string ThumbnailUrl, string FullUrl);

public sealed record SteamMovie(
    int Id,
    string Name,
    string ThumbnailUrl,
    string? Mp4Low,
    string? Mp4High,
    string? WebmLow,
    string? WebmHigh,
    string? HlsH264,
    string? DashH264)
{
    /// <summary>Best mp4 URL for inline playback — prefers 480p (more reliable inline) over max.</summary>
    public string? BestMp4 => Mp4Low ?? Mp4High;

    /// <summary>Best progressive video URL (mp4 or webm); null if Steam only exposed streaming.</summary>
    public string? ProgressiveUrl => Mp4Low ?? Mp4High ?? WebmLow ?? WebmHigh;

    /// <summary>Returns true if at least one playable / openable video URL exists.</summary>
    public bool HasAnyPlayable =>
        !string.IsNullOrEmpty(Mp4Low)   || !string.IsNullOrEmpty(Mp4High) ||
        !string.IsNullOrEmpty(WebmLow)  || !string.IsNullOrEmpty(WebmHigh) ||
        !string.IsNullOrEmpty(HlsH264)  || !string.IsNullOrEmpty(DashH264);

    /// <summary>External-friendly URL to hand to the OS browser (HLS plays in modern browsers).</summary>
    public string? BrowserUrl => ProgressiveUrl ?? HlsH264 ?? DashH264;
}
