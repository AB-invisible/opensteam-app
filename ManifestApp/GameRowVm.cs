using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using ManifestApp.Core.Models;

namespace ManifestApp;

public sealed class GameRowVm : INotifyPropertyChanged
{
    internal static Uri PlaceholderCapsuleUri { get; } = new Uri("ms-appx:///Assets/OpenSteamAppLogo.png");

    public GameRowVm(uint appId, string displayName)
    {
        AppId = appId;
        DisplayName = displayName.Trim();
        _thumbnailUriOverride = PlaceholderCapsuleUri;
    }

    public uint AppId { get; }

    public string DisplayName { get; }

    private bool _isConfigured;

    public bool IsConfigured
    {
        get => _isConfigured;
        set => SetField(ref _isConfigured, value);
    }

    private Uri _thumbnailUriOverride;

    public Uri ThumbUri
    {
        get => _thumbnailUriOverride;
        private set => SetField(ref _thumbnailUriOverride, value);
    }

    public string AppIdFormatted =>
        string.Create(CultureInfo.InvariantCulture, $"App ID: {AppId}");

    /// <summary>Second line under the title when this App ID has a local tracking record (install time, file count).</summary>
    public string TrackingSubtitleLine
    {
        get => _trackingSubtitleLine;
        private set => SetField(ref _trackingSubtitleLine, value);
    }

    private string _trackingSubtitleLine = "";

    public IReadOnlyList<string>? TrackedDeployedPaths { get; private set; }

    public string ZipFingerprintPrefix
    {
        get => _zipFingerprintPrefix;
        private set => SetField(ref _zipFingerprintPrefix, value);
    }

    private string _zipFingerprintPrefix = "";

    public void AttachTrackingMetadata(InstalledManifestRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var count = record.DeployedAbsolutePaths?.Count ?? 0;
        var local = record.InstalledUtc.ToLocalTime();
        TrackingSubtitleLine = string.Create(CultureInfo.CurrentCulture, $"{count} file(s) · Added {local:g}");
        TrackedDeployedPaths =
            record.DeployedAbsolutePaths is { Count: > 0 } list
                ? [.. list]
                : null;

        var hex = record.ZipSha256Hex.Trim();
        ZipFingerprintPrefix = hex.Length == 0
            ? ""
            : hex.Length <= 16
                ? hex
                : string.Concat(hex.AsSpan(0, 16), "…");
    }

    public void ClearTrackingMetadata()
    {
        TrackingSubtitleLine = "";
        TrackedDeployedPaths = null;
        ZipFingerprintPrefix = "";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void AttachLocalThumbnail(string absolutePath)
    {
        var trimmed = absolutePath.Trim().Trim('"');
        var full = Path.GetFullPath(trimmed);
        ThumbUri = new Uri(full);
    }

    /// <summary>Uses storefront capsule artwork (tiny_image HTTPS URL).</summary>
    public void AttachRemoteHttpsThumbnail(string httpsUrl)
    {
        var trimmed = httpsUrl.Trim();
        if (trimmed.StartsWith("//", StringComparison.Ordinal))
            trimmed = "https:" + trimmed;

        ThumbUri = new Uri(trimmed, UriKind.Absolute);
    }

    public void DropThumbnailBackToPlaceholder() =>
        ThumbUri = PlaceholderCapsuleUri;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        if (string.IsNullOrEmpty(name))
            return;

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
