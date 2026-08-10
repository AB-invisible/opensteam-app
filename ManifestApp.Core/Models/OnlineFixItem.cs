namespace ManifestApp.Core.Models;

public sealed class OnlineFixItem
{
    public string Name { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Size { get; set; }
    public string? Version { get; set; }
    public string? FileName { get; set; }
}
