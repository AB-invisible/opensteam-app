using System.Diagnostics;
using System.Globalization;
using ManifestApp.Core;
using ManifestApp.Core.Models;
using ManifestApp.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Windows.Media.Core;
using Windows.UI;
using System.IO.Compression;

namespace ManifestApp.Pages;

public sealed partial class GameDetailsPage : Page
{
    private App TypedApp => (App)Application.Current;

    private uint _appId;
    private string _displayName = "";
    private bool _isConfigured;
    private SteamAppFullDetails? _details;
    private CancellationTokenSource? _loadCts;

    /// <summary>Unified list of media (movies first, then screenshots). Each item is one of:
    /// <see cref="SteamMovie"/> or <see cref="SteamScreenshot"/>.</summary>
    private readonly List<object> _mediaItems = new();
    private int _currentMediaIndex = -1;
    private Windows.Media.Playback.MediaPlayer? _activePlayer;

    /// <summary>Cancelled when the user navigates to different media so any in-flight
    /// async media load (especially AdaptiveMediaSource.CreateFromUriAsync) aborts before
    /// it can touch a now-orphaned MediaPlayerElement.</summary>
    private CancellationTokenSource? _mediaLoadCts;

    /// <summary>Border element for each thumbnail (parallel to <see cref="_mediaItems"/>) —
    /// used to highlight the active thumb when navigation moves.</summary>
    private readonly List<Border> _thumbBorders = new();

    private OnlineFixItem? _availableFix;
    private bool _fixOperationInProgress;
    private bool _installInProgress;

    private const double ThumbTileWidth   = 160;
    private const double ThumbTileSpacing = 8;
    private const double ThumbTileStride  = ThumbTileWidth + ThumbTileSpacing;

    public GameDetailsPage()
    {
        InitializeComponent();
        Unloaded += (_, _) => _loadCts?.Cancel();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not GameDetailsNavigationArgs args) return;

        _appId        = args.AppId;
        _displayName  = args.DisplayName;
        _isConfigured = args.IsConfigured;

        GameTitleText.Text = _displayName;
        AppIdText.Text     = string.Create(CultureInfo.InvariantCulture, $"App ID {_appId:N0}");

        UpdateTrackedPill();
        UpdateActionButtons();
        LoadTrackedFilesSummary();

        _availableFix = null;

        TypedApp.Svcs.DiscordPresence.NotifyBrowsingGame(TruncateForDiscord(_displayName));

        _loadCts = new CancellationTokenSource();
        _ = LoadDetailsAsync(_loadCts.Token);
        _ = CheckFixAvailabilityAsync(_loadCts.Token);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _loadCts?.Cancel();
        ClearMediaViewer();
    }

    // ── Hero + meta ──────────────────────────────────────────────────────────

    private async Task LoadDetailsAsync(CancellationToken ct)
    {
        try
        {
            _details = await TypedApp.Svcs.SteamStoreDetails
                .GetFullDetailsAsync(_appId, ct)
                .ConfigureAwait(true);

            if (ct.IsCancellationRequested) return;

            if (_details == null)
            {
                ShortDescText.Text = "Steam store page unavailable for this App ID.";
                MediaPlaceholder.Text = "No media available";
                AwaitFallbackCapsuleAsync();
                return;
            }

            if (!string.IsNullOrWhiteSpace(_details.Name))
                GameTitleText.Text = _details.Name;

            if (!string.IsNullOrWhiteSpace(_details.ReleaseDateText))
            {
                ReleaseText.Text = _details.ReleaseDateText!;
                ReleaseText.Visibility = Visibility.Visible;
            }

            ComingSoonPill.Visibility = _details.ComingSoon ? Visibility.Visible : Visibility.Collapsed;

            if (_details.Developers.Count > 0)
            {
                DeveloperLineText.Text = "by " + string.Join(", ", _details.Developers);
                DeveloperLineText.Visibility = Visibility.Visible;
            }

            ShortDescText.Text = string.IsNullOrWhiteSpace(_details.ShortDescription)
                ? "No description available."
                : _details.ShortDescription;

            if (!string.IsNullOrWhiteSpace(_details.AboutText) &&
                _details.AboutText.Length > _details.ShortDescription.Length + 8)
            {
                AboutLongText.Text = _details.AboutText;
                AboutPanel.Visibility = Visibility.Visible;
            }

            if (!string.IsNullOrEmpty(_details.HeaderImageUrl))
                HeroBackgroundImage.Source = new BitmapImage(new Uri(_details.HeaderImageUrl!));

            BuildMetaPanel();
            BuildMediaStrip();
        }
        catch (OperationCanceledException)
        {
            /* navigation cancelled */
        }
        catch (Exception ex)
        {
            ShortDescText.Text = $"Could not load Steam details: {ex.Message}";
            MediaPlaceholder.Text = "Media unavailable";
        }
    }

    private async void AwaitFallbackCapsuleAsync()
    {
        try
        {
            var path = await TypedApp.Svcs.ArtworkCache
                .GetCapsule184PathAsync(_appId, CancellationToken.None)
                .ConfigureAwait(true);

            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                HeroBackgroundImage.Source = new BitmapImage(new Uri(System.IO.Path.GetFullPath(path)));
        }
        catch { /* best effort */ }
    }

    private void BuildMetaPanel()
    {
        MetaPanel.Children.Clear();
        if (_details == null) return;

        if (_details.Developers.Count > 0)
            MetaPanel.Children.Add(BuildMetaRow("Developer", string.Join(", ", _details.Developers)));

        if (_details.Publishers.Count > 0)
            MetaPanel.Children.Add(BuildMetaRow("Publisher", string.Join(", ", _details.Publishers)));

        if (!string.IsNullOrWhiteSpace(_details.ReleaseDateText))
            MetaPanel.Children.Add(BuildMetaRow("Release", _details.ReleaseDateText!));

        if (_details.Genres.Count > 0)
            MetaPanel.Children.Add(BuildGenreChips(_details.Genres));

        var platforms = new List<string>();
        if (_details.WindowsSupported) platforms.Add("Windows");
        if (_details.MacSupported)     platforms.Add("macOS");
        if (_details.LinuxSupported)   platforms.Add("Linux");
        if (platforms.Count > 0)
            MetaPanel.Children.Add(BuildMetaRow("Platforms", string.Join(" · ", platforms)));
    }

    private UIElement BuildMetaRow(string label, string value)
    {
        var sp = new StackPanel { Spacing = 2 };
        sp.Children.Add(new TextBlock
        {
            Text = label.ToUpperInvariant(),
            FontSize = 10,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            CharacterSpacing = 160,
            Opacity = 0.6,
        });
        sp.Children.Add(new TextBlock
        {
            Text = value,
            FontSize = 13,
            TextWrapping = TextWrapping.WrapWholeWords,
        });
        return sp;
    }

    private UIElement BuildGenreChips(IReadOnlyList<string> genres)
    {
        var sp = new StackPanel { Spacing = 6 };
        sp.Children.Add(new TextBlock
        {
            Text = "GENRES",
            FontSize = 10,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            CharacterSpacing = 160,
            Opacity = 0.6,
        });

        // Simple two-row wrap using StackPanels — sufficient for the 1–4 genres Steam typically returns.
        var row1 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var row2 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 6, 0, 0) };
        var border = (Brush)new SolidColorBrush(Color.FromArgb(0x30, 0x6E, 0x45, 0xFF));
        var fill   = (Brush)new SolidColorBrush(Color.FromArgb(0x15, 0x6E, 0x45, 0xFF));
        var fg     = (Brush)new SolidColorBrush(Color.FromArgb(0xFF, 0xB8, 0xC8, 0xFF));

        var split = Math.Min(genres.Count, 3);
        for (int i = 0; i < genres.Count; i++)
        {
            var chip = new Border
            {
                CornerRadius    = new CornerRadius(999),
                Padding         = new Thickness(10, 3, 12, 3),
                BorderThickness = new Thickness(1),
                BorderBrush     = border,
                Background      = fill,
                Child = new TextBlock { Text = genres[i], FontSize = 11, Foreground = fg },
            };
            (i < split ? row1 : row2).Children.Add(chip);
        }

        sp.Children.Add(row1);
        if (row2.Children.Count > 0) sp.Children.Add(row2);
        return sp;
    }

    // ── Media (trailer + screenshots) ────────────────────────────────────────

    private void BuildMediaStrip()
    {
        ThumbStrip.Items.Clear();
        _thumbBorders.Clear();
        _mediaItems.Clear();
        _currentMediaIndex = -1;
        if (_details == null) return;

        // Unified media list — movies first (any playable format: mp4 / HLS / DASH), then screenshots.
        foreach (var m in _details.Movies)
            if (m.HasAnyPlayable) _mediaItems.Add(m);
        foreach (var s in _details.Screenshots)
            _mediaItems.Add(s);

        if (_mediaItems.Count == 0)
        {
            if (!string.IsNullOrEmpty(_details.HeaderImageUrl))
                ShowScreenshot(_details.HeaderImageUrl!);
            else
                MediaPlaceholder.Text = "No screenshots or trailers available";
            PrevMediaButton.Visibility = Visibility.Collapsed;
            NextMediaButton.Visibility = Visibility.Collapsed;
            MediaCounterBadge.Visibility = Visibility.Collapsed;
            return;
        }

        // Build the thumbnail strip in the same order as _mediaItems so indices match.
        for (int i = 0; i < _mediaItems.Count; i++)
        {
            var item = _mediaItems[i];
            var idx = i;
            UIElement tile = item switch
            {
                SteamMovie mv      => BuildMovieThumbTile(mv, idx),
                SteamScreenshot sh => BuildScreenshotThumbTile(sh, idx),
                _                  => new Border(),
            };
            ThumbStrip.Items.Add(tile);
        }

        var multiple = _mediaItems.Count > 1;
        PrevMediaButton.Visibility   = multiple ? Visibility.Visible : Visibility.Collapsed;
        NextMediaButton.Visibility   = multiple ? Visibility.Visible : Visibility.Collapsed;
        MediaCounterBadge.Visibility = multiple ? Visibility.Visible : Visibility.Collapsed;

        ShowMediaAtIndex(0);
    }

    private void ShowMediaAtIndex(int idx)
    {
        if (_mediaItems.Count == 0) return;
        if (idx < 0) idx = _mediaItems.Count - 1;
        if (idx >= _mediaItems.Count) idx = 0;
        _currentMediaIndex = idx;

        switch (_mediaItems[idx])
        {
            case SteamMovie mv:      ShowMovie(mv);              break;
            case SteamScreenshot sh: ShowScreenshot(sh.FullUrl); break;
        }

        MediaCounterText.Text = $"{idx + 1} / {_mediaItems.Count}";

        UpdateThumbHighlight();
        ScrollActiveThumbIntoView();

        // If the lightbox is currently showing, keep it in sync with the new media.
        // (Only screenshots are lightboxable — videos can't be zoomed.)
        if (LightboxHost.Visibility == Visibility.Visible &&
            _mediaItems[idx] is SteamScreenshot lightboxShot)
        {
            LightboxImage.Source = new BitmapImage(new Uri(lightboxShot.FullUrl));
        }
    }

    /// <summary>
    /// Moves to the next/previous media item. When the lightbox is open we skip movies
    /// (you can't zoom a video) so the user keeps swiping between screenshots.
    /// </summary>
    private void NavigateRelative(int direction)
    {
        if (_mediaItems.Count <= 1) return;
        var lightboxOpen = LightboxHost.Visibility == Visibility.Visible;

        int target = _currentMediaIndex;
        for (int i = 0; i < _mediaItems.Count; i++)
        {
            target = (target + direction + _mediaItems.Count) % _mediaItems.Count;
            if (lightboxOpen && _mediaItems[target] is not SteamScreenshot) continue;
            ShowMediaAtIndex(target);
            return;
        }
        // No reachable item in lightbox mode (no screenshots at all) — give up.
    }

    /// <summary>Recolors thumbnail borders so the active one stands out.</summary>
    private void UpdateThumbHighlight()
    {
        var activeBrush   = (Brush)Application.Current.Resources["OpenSteamAccentGradientBrush"];
        var inactiveBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];

        for (int i = 0; i < _thumbBorders.Count; i++)
        {
            var b = _thumbBorders[i];
            if (i == _currentMediaIndex)
            {
                b.BorderBrush     = activeBrush;
                b.BorderThickness = new Thickness(2);
                b.Opacity         = 1.0;
            }
            else
            {
                b.BorderBrush     = inactiveBrush;
                b.BorderThickness = new Thickness(1);
                b.Opacity         = 0.65;
            }
        }
    }

    /// <summary>Horizontally scrolls the thumbnail strip so the active tile is centered (or as close as possible).</summary>
    private void ScrollActiveThumbIntoView()
    {
        if (_currentMediaIndex < 0 || _mediaItems.Count <= 1) return;
        if (ThumbStripScroller is null) return;

        // Defer until the ScrollViewer has measured its viewport, otherwise ViewportWidth is 0.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            var viewport = ThumbStripScroller.ViewportWidth;
            if (viewport <= 0) return;

            // Position of the active tile's left edge inside the strip.
            var activeX  = _currentMediaIndex * ThumbTileStride;
            var centered = activeX - (viewport / 2) + (ThumbTileWidth / 2);

            // Clamp to valid scroll range.
            var maxOffset = Math.Max(0, ThumbStripScroller.ExtentWidth - viewport);
            var target    = Math.Min(Math.Max(0, centered), maxOffset);

            ThumbStripScroller.ChangeView(target, null, null, disableAnimation: false);
        });
    }

    private void NextMedia_Click(object sender, RoutedEventArgs e) => NavigateRelative(+1);
    private void PrevMedia_Click(object sender, RoutedEventArgs e) => NavigateRelative(-1);

    private void LightboxNext_Click(object sender, RoutedEventArgs e) => NavigateRelative(+1);
    private void LightboxPrev_Click(object sender, RoutedEventArgs e) => NavigateRelative(-1);

    private void NextMedia_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_mediaItems.Count <= 1) return;
        NavigateRelative(+1);
        args.Handled = true;
    }

    private void PrevMedia_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (_mediaItems.Count <= 1) return;
        NavigateRelative(-1);
        args.Handled = true;
    }

    // ── Touch / touchpad swipe ────────────────────────────────────────────────

    /// <summary>Fires after a finger/pen pan ends; if the user dragged horizontally past
    /// a threshold, navigate to the prev/next item.</summary>
    private void Media_ManipulationCompleted(object sender, Microsoft.UI.Xaml.Input.ManipulationCompletedRoutedEventArgs e)
    {
        if (_mediaItems.Count <= 1) return;
        const double swipeThreshold = 60.0;
        var dx = e.Cumulative.Translation.X;
        if (Math.Abs(dx) < swipeThreshold) return;

        NavigateRelative(dx > 0 ? -1 : +1);
        e.Handled = true;
    }

    // Touchpad horizontal scroll fires as a stream of wheel events; we accumulate
    // the delta and only trigger nav once the threshold is crossed so a single
    // swipe doesn't blast through 10 photos.
    private double _hWheelAccum;
    private DateTime _hWheelLastTime;
    private DateTime _hWheelLastNav;
    private const double HWheelThreshold = 200.0;
    private const int    HWheelCooldownMs = 220;

    private void Media_PointerWheelChanged(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_mediaItems.Count <= 1) return;
        var props = e.GetCurrentPoint((UIElement)sender).Properties;

        // Two-finger touchpad horizontal scroll → IsHorizontalMouseWheel=true.
        if (!props.IsHorizontalMouseWheel) return;

        var now = DateTime.UtcNow;
        if ((now - _hWheelLastTime).TotalMilliseconds > 250) _hWheelAccum = 0;
        _hWheelLastTime = now;

        _hWheelAccum += props.MouseWheelDelta;

        // Post-navigation cooldown so a single brisk swipe doesn't blow past several photos.
        if ((now - _hWheelLastNav).TotalMilliseconds < HWheelCooldownMs)
        {
            e.Handled = true;
            return;
        }

        if (_hWheelAccum >= HWheelThreshold)
        {
            NavigateRelative(+1);
            _hWheelAccum = 0;
            _hWheelLastNav = now;
        }
        else if (_hWheelAccum <= -HWheelThreshold)
        {
            NavigateRelative(-1);
            _hWheelAccum = 0;
            _hWheelLastNav = now;
        }
        e.Handled = true;
    }

    private void ToggleVideo_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // Don't hijack space if the focused element handles text input (search boxes, etc.).
        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox or PasswordBox or AutoSuggestBox)
            return;
        if (_activePlayer == null) return;

        var state = _activePlayer.PlaybackSession?.PlaybackState;
        if (state == Windows.Media.Playback.MediaPlaybackState.Playing)
            _activePlayer.Pause();
        else
            _activePlayer.Play();
        args.Handled = true;
    }

    private void Escape_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // If the lightbox is open, Esc closes it instead of navigating back.
        if (LightboxHost.Visibility == Visibility.Visible)
        {
            HideLightbox();
            args.Handled = true;
            return;
        }
        if (Frame != null && Frame.CanGoBack)
        {
            Frame.GoBack();
            args.Handled = true;
        }
    }

    private void OpenMediaInBrowser_Click(object sender, RoutedEventArgs e)
    {
        if (_currentMediaIndex < 0 || _currentMediaIndex >= _mediaItems.Count) return;

        string? url = _mediaItems[_currentMediaIndex] switch
        {
            SteamMovie mv      => mv.BrowserUrl,
            SteamScreenshot sh => sh.FullUrl,
            _                  => null,
        };
        if (string.IsNullOrEmpty(url)) return;

        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Status($"Couldn't open media: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private UIElement BuildMovieThumbTile(SteamMovie movie, int index)
    {
        var border = new Border
        {
            Width = ThumbTileWidth,
            Height = 90,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Opacity = 0.65,
        };
        _thumbBorders.Add(border);
        var grid = new Grid();
        grid.Children.Add(new Image
        {
            Stretch = Stretch.UniformToFill,
            Source = new BitmapImage(new Uri(movie.ThumbnailUrl)),
        });
        // Play overlay
        var overlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x55, 0, 0, 0)),
        };
        var playIcon = new FontIcon
        {
            Glyph = "",
            FontSize = 22,
            Foreground = new SolidColorBrush(Colors.White),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        overlay.Child = playIcon;
        grid.Children.Add(overlay);
        border.Child = grid;

        var button = new Button
        {
            Padding = new Thickness(0),
            MinWidth = 0,
            MinHeight = 0,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            AllowFocusOnInteraction = false,
            IsTabStop = false,
            Content = border,
        };
        button.Click += (_, _) => ShowMediaAtIndex(index);
        return button;
    }

    private UIElement BuildScreenshotThumbTile(SteamScreenshot shot, int index)
    {
        var image = new Image
        {
            Stretch = Stretch.UniformToFill,
            Source = new BitmapImage(new Uri(shot.ThumbnailUrl)),
        };
        var border = new Border
        {
            Width = ThumbTileWidth,
            Height = 90,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Opacity = 0.65,
            Child = image,
        };
        _thumbBorders.Add(border);
        var button = new Button
        {
            Padding = new Thickness(0),
            MinWidth = 0,
            MinHeight = 0,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            AllowFocusOnInteraction = false,
            IsTabStop = false,
            Content = border,
        };
        button.Click += (_, _) => ShowMediaAtIndex(index);
        return button;
    }

    private void ClearMediaViewer()
    {
        // Cancel any in-flight async media load so it doesn't write back into stale UI.
        try { _mediaLoadCts?.Cancel(); _mediaLoadCts?.Dispose(); }
        catch { /* ignore */ }
        _mediaLoadCts = null;

        // Stop and dispose any active MediaPlayer so video playback halts.
        foreach (var child in MediaViewerGrid.Children.OfType<MediaPlayerElement>().ToList())
        {
            try
            {
                child.MediaPlayer?.Pause();
                child.SetMediaPlayer(null);
            }
            catch { /* ignore */ }
        }
        try { _activePlayer?.Pause(); _activePlayer?.Dispose(); }
        catch { /* ignore */ }
        _activePlayer = null;

        MediaViewerGrid.Children.Clear();
        OpenInBrowserButton.Visibility = Visibility.Collapsed;
    }

    private void ShowMovie(SteamMovie movie)
    {
        ClearMediaViewer();

        // Build the playback queue: try cheapest/most-compatible first.
        // 1. Progressive mp4 (older games), 2. HLS (.m3u8), 3. DASH (.mpd).
        var queue = new List<string>();
        if (!string.IsNullOrEmpty(movie.Mp4Low))   queue.Add(movie.Mp4Low!);
        if (!string.IsNullOrEmpty(movie.Mp4High))  queue.Add(movie.Mp4High!);
        if (!string.IsNullOrEmpty(movie.HlsH264))  queue.Add(movie.HlsH264!);
        if (!string.IsNullOrEmpty(movie.DashH264)) queue.Add(movie.DashH264!);

        if (queue.Count == 0)
        {
            // No playable URL at all — show the thumbnail with an "Open in browser" hint
            ShowScreenshot(movie.ThumbnailUrl);
            OpenInBrowserButton.Visibility = Visibility.Visible;
            return;
        }

        var element = new MediaPlayerElement
        {
            AreTransportControlsEnabled = true,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            PosterSource = new BitmapImage(new Uri(movie.ThumbnailUrl)),
        };
        MediaViewerGrid.Children.Add(element);

        // "Open in browser" stays hidden unless inline playback fails — the user already has
        // the built-in transport controls (play/pause/seek/volume) on the MediaPlayerElement.
        OpenInBrowserButton.Visibility = Visibility.Collapsed;

        _mediaLoadCts = new CancellationTokenSource();
        _ = TryPlayQueueAsync(element, movie, queue, 0, _mediaLoadCts.Token);
    }

    private async Task TryPlayQueueAsync(MediaPlayerElement element, SteamMovie movie, List<string> queue, int idx, CancellationToken ct)
    {
        // The user moved on before we got here — bail.
        if (ct.IsCancellationRequested) return;

        if (idx >= queue.Count)
        {
            // All formats failed — quietly surface the browser-fallback button. No big toast.
            if (!ct.IsCancellationRequested)
                OpenInBrowserButton.Visibility = Visibility.Visible;
            return;
        }

        var url = queue[idx];
        Windows.Media.Playback.MediaPlayer? mp = null;
        try
        {
            var startupBehavior = TypedApp.Svcs.SettingsStore.Load().GameDetailsVideoStartupBehavior;
            var shouldStartPaused = startupBehavior == "paused";
            mp = new Windows.Media.Playback.MediaPlayer
            {
                AutoPlay = !shouldStartPaused,
                IsLoopingEnabled = false,
                IsMuted = startupBehavior != "sound",
            };

            // HLS / DASH need AdaptiveMediaSource; progressive mp4/webm use a plain URI.
            var isAdaptive = url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
                          || url.Contains(".mpd",  StringComparison.OrdinalIgnoreCase);

            if (isAdaptive)
            {
                var amsResult = await Windows.Media.Streaming.Adaptive.AdaptiveMediaSource
                    .CreateFromUriAsync(new Uri(url)).AsTask(ct);

                if (ct.IsCancellationRequested) { SafeDispose(mp); return; }

                if (amsResult.Status !=
                    Windows.Media.Streaming.Adaptive.AdaptiveMediaSourceCreationStatus.Success)
                {
                    SafeDispose(mp);
                    await TryPlayQueueAsync(element, movie, queue, idx + 1, ct);
                    return;
                }
                mp.Source = MediaSource.CreateFromAdaptiveMediaSource(amsResult.MediaSource);
            }
            else
            {
                mp.Source = MediaSource.CreateFromUri(new Uri(url));
            }

            if (ct.IsCancellationRequested) { SafeDispose(mp); return; }

            var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            var capturedMp = mp; // closure capture
            mp.MediaFailed += (_, _) =>
            {
                dispatcher.TryEnqueue(() =>
                {
                    if (ct.IsCancellationRequested) { SafeDispose(capturedMp); return; }
                    SafeDispose(capturedMp);
                    if (_activePlayer == capturedMp) _activePlayer = null;
                    _ = TryPlayQueueAsync(element, movie, queue, idx + 1, ct);
                });
            };

            // Final check: a swipe may have completed during the awaits above.
            if (ct.IsCancellationRequested) { SafeDispose(mp); return; }

            element.SetMediaPlayer(mp);
            // Dispose any previous player from an earlier attempt before swapping in.
            if (_activePlayer != null && _activePlayer != mp) SafeDispose(_activePlayer);
            _activePlayer = mp;
            if (!shouldStartPaused)
                mp.Play();
        }
        catch (OperationCanceledException)
        {
            SafeDispose(mp);
        }
        catch
        {
            SafeDispose(mp);
            if (!ct.IsCancellationRequested)
                await TryPlayQueueAsync(element, movie, queue, idx + 1, ct);
        }
    }

    private static void SafeDispose(Windows.Media.Playback.MediaPlayer? mp)
    {
        if (mp == null) return;
        try { mp.Pause(); } catch { }
        try { mp.Dispose(); } catch { }
    }

    private void ShowScreenshot(string fullUrl)
    {
        ClearMediaViewer();
        var image = new Image
        {
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Source = new BitmapImage(new Uri(fullUrl)),
        };

        // Click anywhere on the image to open it in a lightbox dialog.
        var clickable = new Button
        {
            Padding = new Thickness(0),
            MinWidth = 0, MinHeight = 0,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            AllowFocusOnInteraction = false,
            IsTabStop = false,
            Content = image,
        };
        clickable.Click += (_, _) => ShowLightbox(fullUrl);

        MediaViewerGrid.Children.Add(clickable);
    }

    /// <summary>Opens the full-page lightbox overlay so the screenshot can be examined at full size.</summary>
    private void ShowLightbox(string fullUrl)
    {
        LightboxImage.Source = new BitmapImage(new Uri(fullUrl));
        LightboxHost.Visibility = Visibility.Visible;
        LightboxHost.Focus(FocusState.Programmatic);
    }

    private void HideLightbox()
    {
        LightboxHost.Visibility = Visibility.Collapsed;
        LightboxImage.Source = null;
    }

    private void LightboxClose_Click(object sender, RoutedEventArgs e) => HideLightbox();

    private void LightboxHost_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) =>
        HideLightbox();

    // ── Tracked state ────────────────────────────────────────────────────────

    private void UpdateTrackedPill()
    {
        var record = TypedApp.Svcs.InstalledRecords.FindByAppId(_appId);
        _isConfigured = record != null;

        if (_isConfigured)
        {
            TrackedPillText.Text = "TRACKED";
            TrackedDot.Fill = (Brush)Application.Current.Resources["OpenSteamSuccessBrush"];
        }
        else
        {
            TrackedPillText.Text = "NOT TRACKED";
            TrackedDot.Fill = new SolidColorBrush(Color.FromArgb(255, 0xAF, 0xAF, 0xC4));
        }
    }

    private void UpdateActionButtons()
    {
        InstallButton.IsEnabled = !_isConfigured && !_fixOperationInProgress;
        RemoveButton.IsEnabled  = _isConfigured && !_fixOperationInProgress;
        FixAvailableButton.Visibility = _availableFix != null ? Visibility.Visible : Visibility.Collapsed;
        FixAvailableButton.IsEnabled  = !_fixOperationInProgress;
        LaunchSteamButton.IsEnabled   = !_fixOperationInProgress;
        RestartSteamButton.IsEnabled  = !_fixOperationInProgress;
    }

    private void LoadTrackedFilesSummary()
    {
        var record = TypedApp.Svcs.InstalledRecords.FindByAppId(_appId);
        if (record == null)
        {
            TrackedFilesBlock.Visibility = Visibility.Collapsed;
            return;
        }

        var count = record.DeployedAbsolutePaths?.Count ?? 0;
        var local = record.InstalledUtc.ToLocalTime();
        TrackedFilesSummary.Text = string.Create(CultureInfo.CurrentCulture,
            $"{count} file(s) deployed · added {local:g}");
        TrackedFilesBlock.Visibility = Visibility.Visible;
    }

    private static string TruncateForDiscord(string s, int max = 96) =>
        s.Length <= max ? s : string.Concat(s.AsSpan(0, max - 1), "…");

    // ── Navigation ───────────────────────────────────────────────────────────

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Frame != null && Frame.CanGoBack)
            Frame.GoBack();
    }

    // ── Status helpers ───────────────────────────────────────────────────────

    private void Status(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        StatusInfoBar.Severity = severity;
        StatusInfoBar.Message  = message;
        StatusInfoBar.IsOpen   = true;
    }

    private async Task ShowInfoAsync(string title, string message, string close = "OK")
    {
        var dlg = new ContentDialog
        {
            Title           = title,
            Content         = message,
            CloseButtonText = close,
            DefaultButton   = ContentDialogButton.Close,
            XamlRoot        = XamlRoot!,
        };
        await DialogService.ShowAsync(dlg);
    }

    private async Task<bool> ChooseContinueAnywayAsync(
        string title, string message, string stopText, string continueText)
    {
        var dlg = new ContentDialog
        {
            Title             = title,
            Content           = message,
            PrimaryButtonText = stopText,
            SecondaryButtonText = continueText,
            DefaultButton     = ContentDialogButton.Primary,
            XamlRoot          = XamlRoot!,
        };
        return await DialogService.ShowAsync(dlg) == ContentDialogResult.Secondary;
    }

    private async Task<bool> AskRestartSteamNowAsync(string explanation)
    {
        var dlg = new ContentDialog
        {
            Title             = "Restart Steam?",
            Content           = explanation,
            PrimaryButtonText = "Restart Steam now",
            CloseButtonText   = "I'll restart later",
            DefaultButton     = ContentDialogButton.Primary,
            XamlRoot          = XamlRoot!,
        };
        return await DialogService.ShowAsync(dlg) == ContentDialogResult.Primary;
    }

    private async Task OfferSteamGracefulRestartAfterMutationAsync(string explanation, string deferReminder)
    {
        if (!await AskRestartSteamNowAsync(explanation).ConfigureAwait(true))
        {
            Status(deferReminder, InfoBarSeverity.Informational);
            return;
        }

        var steamRoot = TypedApp.Svcs.PathsResolver.ResolveSteamInstall();
        if (string.IsNullOrEmpty(steamRoot))
        {
            const string manual = "Steam folder unknown — restart Steam yourself from Desktop or Start.";
            Status(manual, InfoBarSeverity.Warning);
            await ShowInfoAsync("Restart Steam manually", manual);
            return;
        }

        var outcome = await SteamClientRestart
            .TryGracefulRestartAsync(steamRoot, TimeSpan.FromSeconds(45), CancellationToken.None)
            .ConfigureAwait(true);

        Status(outcome.Message,
            outcome.Succeeded ? InfoBarSeverity.Success : InfoBarSeverity.Warning);

        await ShowInfoAsync(
            outcome.Succeeded ? "Steam restarted" : "Steam restart incomplete",
            outcome.Message);
    }

    // ── Online fix ───────────────────────────────────────────────────────────

    private async Task CheckFixAvailabilityAsync(CancellationToken ct)
    {
        try
        {
            if (!OpenSteamApiKeyStore.TryRetrieve(out var apiKey) || string.IsNullOrWhiteSpace(apiKey))
                return;

            var fixes = await TypedApp.Svcs.OpenSteamApi.GetOnlineFixesAsync(apiKey.Trim(), ct)
                .ConfigureAwait(true);
            if (ct.IsCancellationRequested) return;

            _availableFix = OnlineFixMatcher.FindMatch(fixes, _displayName);
            UpdateActionButtons();
        }
        catch (OperationCanceledException)
        {
            // Page navigated away.
        }
        catch
        {
            // Fix availability is optional UI — never block the page.
        }
    }

    private async Task<string?> ResolveGameInstallDirAsync()
    {
        var monitor = new SteamGameMonitor(TypedApp.Svcs.SettingsStore);
        return await Task.FromResult(monitor.TryGetInstalledGamePath(_appId)).ConfigureAwait(true);
    }

    private async Task<bool> DownloadAndApplyFixAsync(string apiKey, OnlineFixItem fix, string gameInstallDir)
    {
        var downloadProgress = new Progress<double>(pct =>
        {
            if (InstallProgressRing.Visibility == Visibility.Visible)
            {
                InstallProgressRing.IsActive   = false;
                InstallProgressRing.Visibility = Visibility.Collapsed;
                InstallProgressBar.Visibility  = Visibility.Visible;
            }
            InstallProgressBar.Value = pct;
            InstallProgressText.Text = $"Downloading fix… {pct:0}%";
        });

        using var ms = new MemoryStream();
        await TypedApp.Svcs.OpenSteamApi
            .DownloadOnlineFixAsync(apiKey, fix.Name, ms, CancellationToken.None, downloadProgress)
            .ConfigureAwait(true);
        ms.Position = 0;

        SetInstallProgressIndeterminate("Extracting online fix to game folder...");
        OnlineFixService.ExtractArchive(ms, gameInstallDir);
        return true;
    }

    private async void FixAvailable_Click(object sender, RoutedEventArgs e)
    {
        if (_availableFix == null || _fixOperationInProgress) return;

        if (!OpenSteamApiKeyStore.TryRetrieve(out var apiKey) || string.IsNullOrWhiteSpace(apiKey))
        {
            Status("API key missing — open Settings to add your OpenSteam key.", InfoBarSeverity.Warning);
            return;
        }

        _fixOperationInProgress = true;
        UpdateActionButtons();
        SetInstallProgressIndeterminate("Checking game install folder…");

        try
        {
            var gameInstallDir = await ResolveGameInstallDirAsync().ConfigureAwait(true);
            if (string.IsNullOrEmpty(gameInstallDir))
            {
                var installViaSteam = await AskInstallViaSteamAsync().ConfigureAwait(true);
                if (!installViaSteam) return;

                Process.Start(new ProcessStartInfo
                {
                    FileName = $"steam://install/{_appId}",
                    UseShellExecute = true,
                });
                Status("Install the game in Steam, then press Fix available again.", InfoBarSeverity.Informational);
                return;
            }

            SetInstallProgressIndeterminate($"Downloading fix: {_availableFix.Title}…");
            await DownloadAndApplyFixAsync(apiKey.Trim(), _availableFix, gameInstallDir).ConfigureAwait(true);
            Status($"Online fix installed for {_displayName}.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            Status($"Online fix failed: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            _fixOperationInProgress = false;
            HideInstallProgress();
            UpdateActionButtons();
        }
    }

    private async Task<bool> AskInstallViaSteamAsync()
    {
        var dlg = new ContentDialog
        {
            Title             = "Game not installed",
            Content           = $"{_displayName} is not installed in Steam yet.\n\nInstall it via Steam first, then return here to apply the online fix.",
            PrimaryButtonText = "Install via Steam",
            CloseButtonText   = "Cancel",
            DefaultButton     = ContentDialogButton.Primary,
            XamlRoot          = XamlRoot!,
        };
        return await DialogService.ShowAsync(dlg) == ContentDialogResult.Primary;
    }

    private async Task RunAutoInstallFlowAsync(string apiKey)
    {
        try
        {
            Status($"Triggering Steam install for {_displayName}...", InfoBarSeverity.Informational);

            Process.Start(new ProcessStartInfo
            {
                FileName = $"steam://install/{_appId}",
                UseShellExecute = true,
            });

            SetInstallProgressIndeterminate("Waiting for Steam download to finish...");

            using var monitorCts = new CancellationTokenSource();
            var monitor = new SteamGameMonitor(TypedApp.Svcs.SettingsStore);
            var gameInstallDir = await monitor.WaitForInstallationAsync(_appId, monitorCts.Token);

            if (string.IsNullOrEmpty(gameInstallDir))
            {
                Status("Steam installation was not detected or failed.", InfoBarSeverity.Error);
                return;
            }

            var targetFix = _availableFix;
            if (targetFix == null)
            {
                SetInstallProgressIndeterminate("Fetching matching online fix...");
                var fixes = await TypedApp.Svcs.OpenSteamApi.GetOnlineFixesAsync(apiKey, CancellationToken.None);
                targetFix = OnlineFixMatcher.FindMatch(fixes, _displayName);
            }

            if (targetFix == null)
            {
                Status($"Game downloaded, but no matching online fix found for {_displayName}.", InfoBarSeverity.Warning);
                return;
            }

            SetInstallProgressIndeterminate($"Downloading fix: {targetFix.Title}...");
            await DownloadAndApplyFixAsync(apiKey, targetFix, gameInstallDir).ConfigureAwait(true);
            Status($"Successfully installed {_displayName} and its online fix!", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            Status($"Auto-install flow failed: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            HideInstallProgress();
        }
    }

    // ── Install ──────────────────────────────────────────────────────────────

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_isConfigured || _installInProgress) return;
        _installInProgress = true;

        // These two await calls each show a ContentDialog. They go through DialogService, which
        // refuses to open a second dialog (or one on a detached XamlRoot) — the WinUI overlap that
        // fail-fasts the whole process (0xc000027b) and can't be caught. The try/catch stays as a
        // secondary net for ordinary exceptions.
        try
        {
            if (!await RunSetupGuideAsync()) { _installInProgress = false; return; }
            if (!await EnsureSteamToolsOrContinueAnywayAsync()) { _installInProgress = false; return; }
        }
        catch (Exception ex)
        {
            Status($"Couldn't start install: {ex.Message}", InfoBarSeverity.Error);
            _installInProgress = false;
            return;
        }

        InstallButton.IsEnabled = false;
        SetInstallProgressIndeterminate("Fetching from OpenSteam…");

        try
        {
            TypedApp.Svcs.DiscordPresence.NotifyInstalling(TruncateForDiscord(_displayName, 72));

            var listing = await TypedApp.Svcs.SteamStoreDetails
                .GetListingAsync(_appId, CancellationToken.None).ConfigureAwait(true);

            if (listing.Parsed && listing.ComingSoon)
            {
                var storeName = listing.NameOnStore ?? _displayName;
                var when = string.IsNullOrWhiteSpace(listing.ReleaseDateCaption)
                    ? "Steam marks this title as Coming soon."
                    : listing.ReleaseDateCaption!.Trim();

                var proceed = await ChooseContinueAnywayAsync(
                    "Game not released yet",
                    $"\"{storeName}\" is not publicly released ({when}). OpenSteam frequently has nothing to generate until launch.\n\nRetry after release, or continue anyway.",
                    "Stop install",
                    "Try OpenSteam anyway");
                if (!proceed) return;
            }

            var downloadProgress = new Progress<double>(pct =>
            {
                if (InstallProgressRing.Visibility == Visibility.Visible)
                {
                    InstallProgressRing.IsActive   = false;
                    InstallProgressRing.Visibility = Visibility.Collapsed;
                    InstallProgressBar.Visibility  = Visibility.Visible;
                }
                InstallProgressBar.Value = pct;
                InstallProgressText.Text = $"Downloading ZIP… {pct:0}%";
            });

            if (!OpenSteamApiKeyStore.TryRetrieve(out var apiKey) || string.IsNullOrWhiteSpace(apiKey))
            {
                Status("API key missing — open Settings to add your OpenSteam key.", InfoBarSeverity.Warning);
                return;
            }

            var zipResult = await TypedApp.Svcs.OpenSteamApi
                .DownloadGenerateZipAsync(apiKey.Trim(), _appId, CancellationToken.None, downloadProgress);

            HideInstallProgress();

            if (!zipResult.Ok || zipResult.ZipBytes is null)
            {
                var err = zipResult.ErrorMessage ?? "OpenSteam response could not be turned into manifest artifacts.";
                Status(err, InfoBarSeverity.Error);
                _ = TypedApp.Svcs.AdminReporter.ReportInstallAsync(_appId, _displayName, false);

                var (isQuota, cachedStats) = await CheckQuotaAsync(err);
                if (isQuota)
                    await ShowOutOfGenerationsDialogAsync(cachedStats);
                else
                    await OfferGameRequestAsync(err);
                return;
            }

            try
            {
                await TypedApp.Svcs.ZipInstaller
                    .InstallForAppAsync(_appId, zipResult.ZipBytes, CancellationToken.None);
            }
            catch (InvalidOperationException ex)
            {
                Status(ex.Message, InfoBarSeverity.Error);
                await ShowInfoAsync("Install blocked", ex.Message + "\n\nCheck Steam paths in Settings or ZIP contents.");
                return;
            }

            UpdateTrackedPill();
            UpdateActionButtons();
            LoadTrackedFilesSummary();

            Status($"Install finished for App ID {_appId}.", InfoBarSeverity.Success);
            _ = TypedApp.Svcs.AdminReporter.ReportInstallAsync(_appId, _displayName, true);

            await OfferSteamGracefulRestartAfterMutationAsync(
                "Manifest files were installed. Restart Steam now so it reloads stplug-in and depotcache changes.",
                "Restart Steam yourself when convenient so depot and plugin layouts reload.");

            if (TypedApp.Svcs.SettingsStore.Load().AutoInstallOnlineFix)
            {
                await RunAutoInstallFlowAsync(apiKey.Trim());
            }
        }
        catch (OperationCanceledException)
        {
            Status("Install cancelled.", InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            Status($"Install failed: {ex.Message}", InfoBarSeverity.Error);
            await ShowInfoAsync("Install failed",
                ex.Message + "\n\nCheck Steam paths, antivirus blocking HTTP, and OpenSteam service status.");
        }
        finally
        {
            _installInProgress = false;
            HideInstallProgress();
            UpdateActionButtons();
            TypedApp.Svcs.DiscordPresence.NotifyBrowsingGame(TruncateForDiscord(_displayName));
        }
    }

    // ── Remove ───────────────────────────────────────────────────────────────

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (!_isConfigured) return;

        try
        {
            TypedApp.Svcs.DiscordPresence.NotifyRemoving(TruncateForDiscord(_displayName, 72));

            TypedApp.Svcs.ZipInstaller.RemoveForApp(_appId);
            _ = TypedApp.Svcs.AdminReporter.ReportRemoveAsync(_appId, _displayName);

            UpdateTrackedPill();
            UpdateActionButtons();
            LoadTrackedFilesSummary();

            Status("Removed tracked files (best effort).", InfoBarSeverity.Success);

            await OfferSteamGracefulRestartAfterMutationAsync(
                "Tracked manifest files were removed. Restart Steam now so it reloads cleared stplug-in and depotcache changes.",
                "Restart Steam yourself when convenient so layouts match what is on disk.");
        }
        finally
        {
            TypedApp.Svcs.DiscordPresence.NotifyBrowsingGame(TruncateForDiscord(_displayName));
        }
    }

    // ── Steam protocol actions ───────────────────────────────────────────────

    private void LaunchSteam_Click(object sender, RoutedEventArgs e) =>
        OpenSteamProtocol("run", "Steam launch");

    private void SteamInstall_Click(object sender, RoutedEventArgs e) =>
        OpenSteamProtocol("install", "Steam install");

    private void SteamUninstall_Click(object sender, RoutedEventArgs e) =>
        OpenSteamProtocol("uninstall", "Steam uninstall");

    private void OpenSteamProtocol(string verb, string label)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName        = $"steam://{verb}/{_appId}",
                UseShellExecute = true,
            });
            Status($"Asked Steam to {verb} App ID {_appId}. Confirm in the Steam window.",
                InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            Status($"Couldn't trigger {label}: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    private async void RestartSteam_Click(object sender, RoutedEventArgs e)
    {
        var steamRoot = TypedApp.Svcs.PathsResolver.ResolveSteamInstall();
        if (string.IsNullOrEmpty(steamRoot))
        {
            Status("Steam folder unknown — set it in Settings first.", InfoBarSeverity.Warning);
            return;
        }

        var outcome = await SteamClientRestart
            .TryGracefulRestartAsync(steamRoot, TimeSpan.FromSeconds(45), CancellationToken.None)
            .ConfigureAwait(true);

        Status(outcome.Message,
            outcome.Succeeded ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
    }

    private void OpenPluginFolder_Click(object sender, RoutedEventArgs e) =>
        TryOpenExplorer(TypedApp.Svcs.PathsResolver.ResolveStPluginFolder(),
            "Plugin path unresolved — configure Settings.");

    private void OpenDepotFolder_Click(object sender, RoutedEventArgs e) =>
        TryOpenExplorer(TypedApp.Svcs.PathsResolver.ResolveDepotCacheFolder(),
            "Depot cache path unresolved — configure Settings.");

    private void TryOpenExplorer(string? path, string fallbackMessage)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            Status(fallbackMessage, InfoBarSeverity.Warning);
            return;
        }

        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo
            {
                FileName        = "explorer.exe",
                Arguments       = "\"" + System.IO.Path.GetFullPath(path) + "\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Status($"Couldn't launch Explorer: {ex.Message}", InfoBarSeverity.Error);
        }
    }

    // ── Progress UI helpers ──────────────────────────────────────────────────

    private void SetInstallProgressIndeterminate(string text)
    {
        InstallProgressPanel.Visibility = Visibility.Visible;
        InstallProgressRing.IsActive    = true;
        InstallProgressRing.Visibility  = Visibility.Visible;
        InstallProgressBar.Visibility   = Visibility.Collapsed;
        InstallProgressText.Text        = text;
    }

    private void HideInstallProgress()
    {
        InstallProgressPanel.Visibility = Visibility.Collapsed;
        InstallProgressRing.IsActive    = false;
        InstallProgressRing.Visibility  = Visibility.Collapsed;
        InstallProgressBar.Visibility   = Visibility.Collapsed;
        InstallProgressBar.Value        = 0;
        InstallProgressText.Text        = string.Empty;
    }

    // ── SteamTools + setup guide (ported from HomePage) ──────────────────────

    private async Task<bool> EnsureSteamToolsOrContinueAnywayAsync()
    {
        if (TypedApp.Svcs.SteamToolsLocator.TryFindSteamTools(out _))
            return true;

        var dlg = new ContentDialog
        {
            Title               = "SteamTools not found",
            Content             = "OpenSteam App couldn't locate SteamTools.exe. Without it, plugin / depot flows often won't work.\n\nContinue anyway?",
            PrimaryButtonText   = "Cancel",
            SecondaryButtonText = "Continue anyway",
            DefaultButton       = ContentDialogButton.Primary,
            XamlRoot            = XamlRoot!,
        };
        return await DialogService.ShowAsync(dlg) == ContentDialogResult.Secondary;
    }

    private async Task<(bool IsExhausted, OpenSteamStatsResult? Stats)> CheckQuotaAsync(string errorMessage)
    {
        var msg = errorMessage.ToLowerInvariant();
        bool keywordHit =
            msg.Contains("limit")    || msg.Contains("credit")  ||
            msg.Contains("quota")    || msg.Contains("daily")   ||
            msg.Contains("exceeded") || msg.Contains("ran out") ||
            msg.Contains("no more");

        OpenSteamStatsResult? stats = null;
        if (OpenSteamApiKeyStore.TryRetrieve(out var apiKey) &&
            !string.IsNullOrWhiteSpace(apiKey))
        {
            try
            {
                stats = await TypedApp.Svcs.OpenSteamApi
                    .GetStatsAsync(apiKey.Trim(), CancellationToken.None)
                    .ConfigureAwait(true);
            }
            catch { /* best-effort */ }
        }

        if (stats?.Ok == true && stats.CreditsRemaining.HasValue)
            return (stats.CreditsRemaining.Value <= 0, stats);
        return (keywordHit, stats);
    }

    private static string ResetAtLabel(string? resetAt)
    {
        if (!string.IsNullOrWhiteSpace(resetAt) &&
            DateTimeOffset.TryParse(resetAt, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var dt))
        {
            var local = dt.ToLocalTime();
            var diff  = local - DateTimeOffset.Now;
            if (diff.TotalMinutes < 1) return "Quota resets in less than a minute";
            if (diff.TotalHours < 1)   return $"Quota resets in {(int)diff.TotalMinutes} min";
            return $"Quota resets at {local:h:mm tt}";
        }
        return "Quota resets every 24 hours";
    }

    private async Task ShowOutOfGenerationsDialogAsync(OpenSteamStatsResult? stats)
    {
        string? plan  = stats?.Ok == true ? stats.Plan : null;
        int?    total = stats?.Ok == true ? stats.CreditsTotal : null;

        var planLabel = string.IsNullOrWhiteSpace(plan)
            ? "Your plan"
            : $"{char.ToUpperInvariant(plan[0])}{plan[1..]} plan";

        var limitLine = total.HasValue
            ? $"{planLabel} includes {total} generation{(total == 1 ? "" : "s")} per day."
            : $"{planLabel} has a daily generation limit.";

        var dlg = new ContentDialog
        {
            Title           = "Daily generation limit reached",
            XamlRoot        = XamlRoot!,
            CloseButtonText = "OK",
            DefaultButton   = ContentDialogButton.Close,
            Content         = new StackPanel
            {
                Spacing  = 12,
                MaxWidth = 420,
                Children =
                {
                    new TextBlock
                    {
                        Text         = $"You've used all your available OpenSteam generations for today. {limitLine}",
                        FontSize     = 13,
                        TextWrapping = TextWrapping.WrapWholeWords,
                    },
                    new InfoBar
                    {
                        IsOpen     = true,
                        IsClosable = false,
                        Severity   = InfoBarSeverity.Warning,
                        Title      = ResetAtLabel(stats?.ResetAt),
                        Message    = "Upgrade your plan at opensteam.lol for a higher daily limit.",
                    },
                },
            },
        };

        await DialogService.ShowAsync(dlg);
        if (TypedApp.MainShell is MainWindow mw)
            await mw.RefreshUserStatsAsync().ConfigureAwait(true);
    }

    private async Task OfferGameRequestAsync(string errorMessage)
    {
        var dlg = new ContentDialog
        {
            Title             = "OpenSteam couldn't generate this title",
            Content           = new StackPanel
            {
                Spacing  = 12,
                MaxWidth = 420,
                Children =
                {
                    new TextBlock
                    {
                        Text         = errorMessage,
                        FontSize     = 13,
                        TextWrapping = TextWrapping.WrapWholeWords,
                        Foreground   = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    },
                    new InfoBar
                    {
                        IsOpen     = true,
                        IsClosable = false,
                        Severity   = InfoBarSeverity.Informational,
                        Title      = "Game not in registry?",
                        Message    = "If this title isn't in the OpenSteam registry yet, you can formally request it via the Requests tab.",
                    },
                },
            },
            PrimaryButtonText = "Go to Requests tab",
            CloseButtonText   = "Close",
            DefaultButton     = ContentDialogButton.Primary,
            XamlRoot          = XamlRoot!,
        };

        var choice = await DialogService.ShowAsync(dlg);
        if (choice == ContentDialogResult.Primary &&
            TypedApp.MainShell is MainWindow mw)
        {
            mw.NavigateToRequests(_appId, _displayName);
        }
    }

    private async Task<bool> RunSetupGuideAsync()
    {
        var hasApiKey = OpenSteamApiKeyStore.TryRetrieve(out _);
        var hasSteam  = TypedApp.Svcs.PathsResolver.ResolveSteamInstall() is { Length: > 0 };
        if (hasApiKey && hasSteam) return true;

        var steps = new List<string> { "welcome" };
        if (!hasApiKey) steps.Add("apikey");
        if (!hasSteam)  steps.Add("steam");
        steps.Add("ready");

        int  cur  = 0;
        bool done = false;

        PasswordBox? apiKeyBox  = null;
        TextBlock?   apiKeyHint = null;
        TextBlock?   steamHint  = null;

        UIElement BuildWelcome()
        {
            var sp = new StackPanel { Spacing = 12, MaxWidth = 400 };
            sp.Children.Add(new TextBlock
            {
                Text         = "Let's get OpenSteam set up. Here's what we'll configure:",
                FontSize     = 14,
                TextWrapping = TextWrapping.WrapWholeWords,
            });

            var bullets = new StackPanel { Spacing = 6, Margin = new Thickness(8, 0, 0, 0) };
            if (!hasApiKey)
                bullets.Children.Add(new TextBlock
                {
                    Text         = "① Your OpenSteam API key — authenticates the manifest generation call",
                    FontSize     = 13,
                    TextWrapping = TextWrapping.WrapWholeWords,
                });
            if (!hasSteam)
                bullets.Children.Add(new TextBlock
                {
                    Text         = "② Your Steam install path — so files deploy to the right folders",
                    FontSize     = 13,
                    TextWrapping = TextWrapping.WrapWholeWords,
                });
            sp.Children.Add(bullets);

            sp.Children.Add(new TextBlock
            {
                Text         = "Click Next to walk through each step.",
                FontSize     = 13,
                Foreground   = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextWrapping = TextWrapping.WrapWholeWords,
            });
            return sp;
        }

        UIElement BuildApiKey()
        {
            var sp = new StackPanel { Spacing = 10, MaxWidth = 400 };
            sp.Children.Add(new TextBlock
            {
                Text         = "Your OpenSteam API key is the {YOUR_KEY} segment in the generate URL:",
                FontSize     = 13,
                TextWrapping = TextWrapping.WrapWholeWords,
            });
            sp.Children.Add(new TextBlock
            {
                Text         = "https://opensteam.lol/api/{YOUR_KEY}/generate/{appId}",
                FontSize     = 11,
                FontFamily   = new FontFamily("Consolas"),
                Foreground   = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
                TextWrapping = TextWrapping.Wrap,
            });

            apiKeyBox = new PasswordBox
            {
                PlaceholderText = "Paste your API key here…",
                Margin          = new Thickness(0, 4, 0, 0),
            };
            sp.Children.Add(apiKeyBox);

            apiKeyHint = new TextBlock
            {
                FontSize     = 12,
                Foreground   = new SolidColorBrush(Colors.OrangeRed),
                Visibility   = Visibility.Collapsed,
                TextWrapping = TextWrapping.WrapWholeWords,
            };
            sp.Children.Add(apiKeyHint);

            sp.Children.Add(new TextBlock
            {
                Text       = "You can find or regenerate your key at opensteam.lol.",
                FontSize   = 12,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
            return sp;
        }

        UIElement BuildSteam()
        {
            var sp = new StackPanel { Spacing = 10, MaxWidth = 400 };
            sp.Children.Add(new TextBlock
            {
                Text         = "OpenSteam deploys manifest files into Steam's stplug-in and depotcache folders. We need your Steam install path to find them.",
                FontSize     = 13,
                TextWrapping = TextWrapping.WrapWholeWords,
            });

            steamHint = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.WrapWholeWords };
            var detected = TypedApp.Svcs.PathsResolver.ResolveSteamInstall();
            if (detected is { Length: > 0 })
            {
                steamHint.Text       = $"✓  Steam detected at: {detected}";
                steamHint.Foreground = new SolidColorBrush(Colors.LightGreen);
            }
            else
            {
                steamHint.Text       = "Steam path not detected yet. Click Auto-detect, or open Settings to set it manually.";
                steamHint.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
            }

            var autoBtn = new Button { Content = "Auto-detect Steam path", Padding = new Thickness(14, 8, 14, 8) };
            autoBtn.Click += (_, _) =>
            {
                var found = TypedApp.Svcs.PathsResolver.ResolveSteamInstall();
                if (found is { Length: > 0 })
                {
                    var s = TypedApp.Svcs.SettingsStore.Load();
                    s.SteamInstallPathOverride = found;
                    TypedApp.Svcs.SettingsStore.Save(s);
                    steamHint!.Text      = $"✓  Saved: {found}";
                    steamHint.Foreground = new SolidColorBrush(Colors.LightGreen);
                }
                else
                {
                    steamHint!.Text      = "⚠  Couldn't auto-detect. Open Settings → Steam paths to set it manually.";
                    steamHint.Foreground = new SolidColorBrush(Colors.OrangeRed);
                }
            };
            sp.Children.Add(autoBtn);
            sp.Children.Add(steamHint);

            var settingsLink = new HyperlinkButton
            {
                Content = "Open Settings to configure paths manually →",
                Padding = new Thickness(0, 2, 0, 0),
            };
            settingsLink.Click += (_, _) =>
            {
                if (TypedApp.MainShell is MainWindow mw)
                    mw.NavigateToSettings();
            };
            sp.Children.Add(settingsLink);
            return sp;
        }

        UIElement BuildReady()
        {
            var sp = new StackPanel { Spacing = 10, MaxWidth = 400 };
            sp.Children.Add(new TextBlock
            {
                Text         = "You're all set! Click Generate to fetch the manifest from OpenSteam and deploy it to your Steam folders.",
                FontSize     = 14,
                TextWrapping = TextWrapping.WrapWholeWords,
            });
            return sp;
        }

        UIElement BuildContent(int idx) => steps[idx] switch
        {
            "welcome" => BuildWelcome(),
            "apikey"  => BuildApiKey(),
            "steam"   => BuildSteam(),
            "ready"   => BuildReady(),
            _         => new StackPanel(),
        };

        string StepTitle(int idx) => steps[idx] switch
        {
            "welcome" => $"Get started  ·  Step 1 of {steps.Count}",
            "apikey"  => $"OpenSteam API Key  ·  Step {idx + 1} of {steps.Count}",
            "steam"   => $"Steam Path  ·  Step {idx + 1} of {steps.Count}",
            "ready"   => "All set!",
            _         => "Setup",
        };

        string PrimaryLabel(int idx) => steps[idx] == "ready" ? "Generate!" : "Next  →";

        bool ValidateCurrent()
        {
            if (steps[cur] != "apikey") return true;
            var key = apiKeyBox?.Password?.Trim() ?? "";
            if (!string.IsNullOrEmpty(key))
            {
                try
                {
                    OpenSteamApiKeyStore.Replace(key);
                    if (apiKeyBox != null) apiKeyBox.Password = "";
                    return true;
                }
                catch (Exception ex)
                {
                    // Save failure — show it inline instead of letting the dialog crash the app.
                    if (apiKeyHint != null)
                    {
                        apiKeyHint.Text       = $"Couldn't store the key: {ex.Message}";
                        apiKeyHint.Visibility = Visibility.Visible;
                    }
                    return false;
                }
            }
            if (apiKeyHint != null)
            {
                apiKeyHint.Text       = "Please paste your API key before continuing.";
                apiKeyHint.Visibility = Visibility.Visible;
            }
            return false;
        }

        var dlg = new ContentDialog
        {
            XamlRoot            = XamlRoot!,
            DefaultButton       = ContentDialogButton.Primary,
            PrimaryButtonText   = PrimaryLabel(cur),
            SecondaryButtonText = "",
            CloseButtonText     = "Cancel",
            Title               = StepTitle(cur),
            Content             = BuildContent(cur),
        };

        void RefreshDialog()
        {
            dlg.Title               = StepTitle(cur);
            dlg.Content             = BuildContent(cur);
            dlg.PrimaryButtonText   = PrimaryLabel(cur);
            dlg.SecondaryButtonText = cur > 0 ? "←  Back" : "";
        }

        dlg.PrimaryButtonClick += (_, ev) =>
        {
            if (!ValidateCurrent()) { ev.Cancel = true; return; }
            cur++;
            if (cur >= steps.Count) { done = true; }
            else { ev.Cancel = true; RefreshDialog(); }
        };

        dlg.SecondaryButtonClick += (_, ev) =>
        {
            if (cur <= 0) return;
            ev.Cancel = true;
            cur--;
            RefreshDialog();
        };

        await DialogService.ShowAsync(dlg);
        return done;
    }
}
