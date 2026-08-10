namespace ManifestApp.Core.Models;

public sealed class OnlineFixItem
{
    public string Name { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Size { get; set; }
    public string? Version { get; set; }
    public string? FileName { get; set; }
    public string? ImageUrl { get; set; }

    public string? HeaderImageUrl { get; set; }

    public string? DownloadName { get; set; }

    public uint? SteamAppId { get; set; }

    public string ResolveDownloadName() =>
        !string.IsNullOrWhiteSpace(DownloadName) ? DownloadName! :
        !string.IsNullOrWhiteSpace(Name) ? Name : Title;
}
