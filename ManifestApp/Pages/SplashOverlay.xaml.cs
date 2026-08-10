using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using ManifestApp.Services;
using Windows.UI;

namespace ManifestApp.Pages;

public sealed partial class SplashOverlay : UserControl
{
    private const int StarCount = 40;

    /// <summary>Three loading-step rows the splash exposes for the host to advance.</summary>
    public enum SplashStep { CheckUpdates = 0, Workspace = 1, Ready = 2 }

    public enum StepState { Pending, Active, Done }

    private static readonly SolidColorBrush PendingBrush = new(Color.FromArgb(0x66, 0xB8, 0xC8, 0xFF));
    private static readonly SolidColorBrush ActiveBrush  = new(Color.FromArgb(0xFF, 0xB8, 0xC8, 0xFF));
    private static readonly SolidColorBrush DoneBrush    = new(Color.FromArgb(0xFF, 0xED, 0xED, 0xF5));

    public SplashOverlay()
    {
        InitializeComponent();
        VersionLine.Text = $"version {UpdateService.CurrentVersionString}";

        Loaded += OnLoaded;
        StarsCanvas.SizeChanged += (_, _) => LayoutStars();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        HaloLoop.Begin();
        LogoGlowLoop.Begin();
        Ring1Loop.Begin();
        Ring2Loop.Begin();
        Ring3Loop.Begin();
        LogoBreath.Begin();

        BuildStarField();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Plays the content fade-up + per-letter wordmark cascade.</summary>
    public async Task PlayIntroAsync()
    {
        WordmarkSb.Begin(); // runs alongside the content fade
        await PlayAsync(ContentIntroSb);
    }

    /// <summary>Marks a step as active (spinner) or done (green check), with optional text override.</summary>
    public void SetStep(SplashStep step, StepState state, string? overrideText = null)
    {
        var (pending, active, done, text) = step switch
        {
            SplashStep.CheckUpdates => (Step0Pending, Step0Active, Step0Done, Step0Text),
            SplashStep.Workspace    => (Step1Pending, Step1Active, Step1Done, Step1Text),
            SplashStep.Ready        => (Step2Pending, Step2Active, Step2Done, Step2Text),
            _                       => (Step0Pending, Step0Active, Step0Done, Step0Text),
        };

        pending.Visibility = state == StepState.Pending ? Visibility.Visible : Visibility.Collapsed;
        active.Visibility  = state == StepState.Active  ? Visibility.Visible : Visibility.Collapsed;
        done.Visibility    = state == StepState.Done    ? Visibility.Visible : Visibility.Collapsed;
        active.IsActive    = state == StepState.Active;

        if (overrideText is not null)
            text.Text = overrideText;

        text.Foreground = state switch
        {
            StepState.Pending => PendingBrush,
            StepState.Active  => ActiveBrush,
            StepState.Done    => DoneBrush,
            _                 => PendingBrush,
        };
    }

    /// <summary>Plays the halo bloom + content zoom + full-splash fade.</summary>
    public Task PlayOutroAsync() => PlayAsync(OutroSb);

    // ── Star field ────────────────────────────────────────────────────────────

    private readonly List<(Ellipse Star, double NormX, double NormY)> _stars = new();

    private void BuildStarField()
    {
        // Deterministic seed so the layout is stable across launches.
        var rnd = new Random(42);

        for (var i = 0; i < StarCount; i++)
        {
            var size  = 1.0 + rnd.NextDouble() * 2.2;     // 1.0 – 3.2 px
            var alpha = (byte)(70 + rnd.Next(140));        // ~0x46 – 0xD2
            var star  = new Ellipse
            {
                Width  = size,
                Height = size,
                Fill   = new SolidColorBrush(Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF)),
                Opacity = 0.3 + rnd.NextDouble() * 0.6,
                IsHitTestVisible = false,
            };

            StarsCanvas.Children.Add(star);
            _stars.Add((star, rnd.NextDouble(), rnd.NextDouble()));

            // Per-star twinkle: random duration + random phase.
            var twinkle = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
            var anim = new DoubleAnimation
            {
                From       = 0.15 + rnd.NextDouble() * 0.25,
                To         = 0.85 + rnd.NextDouble() * 0.15,
                Duration   = TimeSpan.FromSeconds(2.0 + rnd.NextDouble() * 3.5),
                AutoReverse = true,
                BeginTime  = TimeSpan.FromSeconds(rnd.NextDouble() * 4.0),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            Storyboard.SetTarget(anim, star);
            Storyboard.SetTargetProperty(anim, "Opacity");
            twinkle.Children.Add(anim);
            twinkle.Begin();
        }

        LayoutStars();
    }

    private void LayoutStars()
    {
        var w = StarsCanvas.ActualWidth;
        var h = StarsCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        foreach (var (star, nx, ny) in _stars)
        {
            Canvas.SetLeft(star, nx * w);
            Canvas.SetTop(star,  ny * h);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Task PlayAsync(Storyboard sb)
    {
        var tcs = new TaskCompletionSource<bool>();
        EventHandler<object> handler = null!;
        handler = (_, _) =>
        {
            sb.Completed -= handler;
            tcs.TrySetResult(true);
        };
        sb.Completed += handler;
        sb.Begin();
        return tcs.Task;
    }
}
