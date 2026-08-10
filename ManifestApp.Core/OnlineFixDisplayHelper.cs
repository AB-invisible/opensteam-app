namespace ManifestApp.Core;

/// <summary>Turns raw OnlineFix catalog labels into user-facing game titles.</summary>
public static class OnlineFixDisplayHelper
{
    private const string OnlineRuMarker = " по сети";

    public static string ParseDisplayTitle(string? rawName, string? fileName = null)
    {
        var source = (rawName ?? string.Empty).Trim();
        if (source.Length == 0 && !string.IsNullOrWhiteSpace(fileName))
            source = ParseFromFileName(fileName!);

        if (source.Length == 0)
            return "Unknown Game";

        var onlineIdx = source.IndexOf(OnlineRuMarker, StringComparison.OrdinalIgnoreCase);
        if (onlineIdx > 0)
            return source[..onlineIdx].Trim();

        var dashIdx = source.IndexOf(" - ", StringComparison.Ordinal);
        if (dashIdx > 0 &&
            source.Contains("Fix Repair", StringComparison.OrdinalIgnoreCase))
            return source[..dashIdx].Trim();

        if (!string.IsNullOrWhiteSpace(fileName))
        {
            var fromFile = ParseFromFileName(fileName!);
            if (fromFile.Length > 0 && !LooksLikeFixBoilerplate(fromFile))
                return fromFile;
        }

        return StripFixSuffix(source);
    }

    public static string HeaderImageUrl(uint appId) =>
        $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg";

    private static string ParseFromFileName(string fileName)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName.Trim());
        if (baseName.Length == 0)
            return string.Empty;

        const string fixMarker = "_Fix_Repair";
        var fixIdx = baseName.IndexOf(fixMarker, StringComparison.OrdinalIgnoreCase);
        if (fixIdx > 0)
            baseName = baseName[..fixIdx];

        return baseName
            .Replace('_', ' ')
            .Replace('-', ' ')
            .Trim();
    }

    private static bool LooksLikeFixBoilerplate(string value) =>
        value.Contains("Fix Repair", StringComparison.OrdinalIgnoreCase);

    private static string StripFixSuffix(string value)
    {
        var trimmed = value.Trim();
        ReadOnlySpan<string> suffixes =
        [
            " Fix Repair Steam Generic",
            " Fix Repair Steam",
            " Online Fix",
        ];

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var suffix in suffixes)
            {
                if (!trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    continue;

                trimmed = trimmed[..^suffix.Length].Trim();
                changed = true;
            }
        }

        return trimmed.Length > 0 ? trimmed : value.Trim();
    }
}
