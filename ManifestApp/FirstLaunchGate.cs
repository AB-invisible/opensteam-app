using System.Text.Json;
using ManifestApp.Core;
using ManifestApp.Services;

namespace ManifestApp;

/// <summary>Decides whether to show first-run setup; migrates legacy settings.json without an onboarding flag.</summary>
internal static class FirstLaunchGate
{
    internal static bool ShouldShowWelcomeWizard(AppServices svcs)
    {
        var s = svcs.SettingsStore.Load();
        if (s.OnboardingCompleted)
            return false;

        if (!File.Exists(AppPaths.SettingsPath))
            return true;

        var hadExplicitOnboardingFlag = false;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(AppPaths.SettingsPath));
            hadExplicitOnboardingFlag = doc.RootElement.TryGetProperty("onboardingCompleted", out var el)
                && el.ValueKind is JsonValueKind.True or JsonValueKind.False;
        }
        catch
        {
            return true;
        }

        if (!hadExplicitOnboardingFlag
            && svcs.PathsResolver.ResolveSteamInstall() is { Length: > 0 }
            && OpenSteamApiKeyStore.TryRetrieve(out _))
        {
            s.OnboardingCompleted = true;
            svcs.SettingsStore.Save(s);
            return false;
        }

        return true;
    }
}
