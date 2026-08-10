using System.ComponentModel;
using System.Runtime.CompilerServices;
using ManifestApp.Core.Models;

namespace ManifestApp;

public sealed class OnlineFixRowVm : INotifyPropertyChanged
{
    internal static Uri PlaceholderUri { get; } = new("ms-appx:///Assets/OpenSteamAppLogo.png");

    public OnlineFixRowVm(OnlineFixItem source, string displayTitle)
    {
        Source = source;
        DisplayTitle = displayTitle;
        _thumbUri = PlaceholderUri;
    }

    public OnlineFixItem Source { get; }

    public string DisplayTitle { get; }

    public string? Size => Source.Size;

    public string? Version => Source.Version;

    private Uri _thumbUri;

    public Uri ThumbUri
    {
        get => _thumbUri;
        private set => SetField(ref _thumbUri, value);
    }

    public void AttachRemoteThumbnail(string httpsUrl)
    {
        var trimmed = httpsUrl.Trim();
        if (trimmed.StartsWith("//", StringComparison.Ordinal))
            trimmed = "https:" + trimmed;

        ThumbUri = new Uri(trimmed, UriKind.Absolute);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
