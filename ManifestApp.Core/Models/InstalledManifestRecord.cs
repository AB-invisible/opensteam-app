namespace ManifestApp.Core.Models;

public sealed class InstalledManifestRecord
{
    public uint AppId { get; set; }

    public DateTimeOffset InstalledUtc { get; set; }

    public string ZipSha256Hex { get; set; } = "";

    /// <summary>Absolute paths deployed by Add for this AppId.</summary>
    public List<string> DeployedAbsolutePaths { get; set; } = [];
}

public sealed class InstalledManifestRecordsFile
{
    public List<InstalledManifestRecord> Records { get; set; } = [];
}
