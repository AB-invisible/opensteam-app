using System;
using System.Net.Http;
using System.Threading;
using Microsoft.UI.Xaml;
using ManifestApp.Core;

namespace ManifestApp;

public partial class App : Application
{
    private static Mutex? _appMutex;

    internal AppServices Svcs { get; }

    /// <summary>Primary shell window — required for pickers and dialogs.</summary>
    internal Window MainShell { get; private set; } = null!;

    public App()
    {
        // Force the app language to English to prevent mixed localizations of system-provided strings.
        // Wrap in try-catch since unpackaged apps lack package identity and may throw on this API.
        try
        {
            Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = "en-US";
        }
        catch { /* ignore */ }

        // Single-file WASDK bootstrap: PRI/runtime layout resolves under the extraction/root dir.
        Environment.SetEnvironmentVariable(
            "MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY",
            AppContext.BaseDirectory);

        // Diagnostic logging for XAML/runtime crashes. Critically, e.Handled must be set to true
        // on the App.UnhandledException event — otherwise WinUI logs the exception and then still
        // terminates the process, which was causing "involuntary" crashes (e.g. clicking Install)
        // for exceptions that should have just surfaced as an error message instead.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash("AppDomain", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogCrash("TaskScheduler", e.Exception);
            e.SetObserved();
        };

        InitializeComponent();

        UnhandledException += (_, e) =>
        {
            LogCrash("App", e.Exception);
            e.Handled = true;
        };

        // Enforce single instance via named system Mutex
        AppLogger.Log($"Application starting up. CommandLine: {Environment.CommandLine}");

        _appMutex = new Mutex(true, "OpenSteamApp_SingleInstance_Mutex", out var isNewInstance);
        if (!isNewInstance)
        {
            AppLogger.Log("Another instance of OpenSteam App is already running. Exiting current instance to prevent conflicts.", "WARNING");
            Environment.Exit(0);
        }

        Svcs = new AppServices(CreateHttpClient());
    }

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "opensteam-crash.log");
            System.IO.File.AppendAllText(path,
                $"[{DateTime.Now:O}] [{source}] {ex?.GetType().FullName}: {ex?.Message}{Environment.NewLine}" +
                $"{ex?.StackTrace}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { /* best-effort */ }

        AppLogger.LogException(source, ex);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10),
        };

        // Literal "OpenSteam App <version>" — the space means UserAgent.ParseAdd would treat it
        // as multiple product tokens, so bypass validation to keep the exact wording.
        var version = Services.UpdateService.CurrentVersionString;
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            $"OpenSteam App {version}");
        return client;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var window = new MainWindow();
        MainShell = window;
        // The splash overlay inside MainWindow drives the update check and calls NavigateShell()
        // once the loading sequence finishes — see MainWindow.RunStartupSequenceAsync.
        window.Activate();
    }
}
