namespace ManifestApp.Core;

/// <summary>Turns raw OnlineFix catalog labels into user-facing game titles.</summary>
public static class OnlineFixDisplayHelper
{
    public static string ParseDisplayTitle(string? rawName, string? fileName = null)
    {
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            var fromFile = ParseFromFileName(fileName);
            var strippedFile = StripOnlineFixLabel(fromFile);
            if (!string.IsNullOrWhiteSpace(strippedFile))
                return strippedFile;
        }

        var fromRaw = StripOnlineFixLabel(rawName ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(fromRaw))
            return fromRaw;

        return string.IsNullOrWhiteSpace(rawName) ? "Unknown Game" : rawName.Trim();
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

    private static string StripOnlineFixLabel(string value)
    {
        var text = value.Trim();
        if (text.Length == 0)
            return string.Empty;

        var onlineIdx = text.IndexOf(" по сет", StringComparison.OrdinalIgnoreCase);
        if (onlineIdx > 0)
            text = text[..onlineIdx].Trim();

        var onlineEnIdx = text.IndexOf(" Online", StringComparison.OrdinalIgnoreCase);
        if (onlineEnIdx > 0)
            text = text[..onlineEnIdx].Trim();

        var dashIdx = text.IndexOf(" - ", StringComparison.Ordinal);
        if (dashIdx > 0 && text.Contains("Fix Repair", StringComparison.OrdinalIgnoreCase))
            text = text[..dashIdx].Trim();

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
                if (!text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    continue;

                text = text[..^suffix.Length].Trim();
                changed = true;
            }
        }

        return text;
    }
}
