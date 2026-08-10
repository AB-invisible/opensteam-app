using System.Text.Json;
using ManifestApp.Core.Models;

namespace ManifestApp.Core;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public AppSettings Load()
    {
        AppPaths.EnsureLayout();
        if (!File.Exists(AppPaths.SettingsPath))
            return new AppSettings();

        try
        {
            var json = File.ReadAllText(AppPaths.SettingsPath);
            using var doc = JsonDocument.Parse(json);
            var s = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();

            if (string.IsNullOrWhiteSpace(s.OpenSteamApiBaseUrl)
                && doc.RootElement.TryGetProperty("gameGenApiBaseUrl", out var legacyBase)
                && legacyBase.ValueKind == JsonValueKind.String)
            {
                s.OpenSteamApiBaseUrl = legacyBase.GetString();
            }

            s.GameDetailsVideoStartupBehavior = NormalizeVideoStartupBehavior(s.GameDetailsVideoStartupBehavior);
            return s;
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        AppPaths.EnsureLayout();
        File.WriteAllText(
            AppPaths.SettingsPath,
            JsonSerializer.Serialize(settings, JsonOptions));
    }

    private static string NormalizeVideoStartupBehavior(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "paused" => "paused",
            "sound" => "sound",
            _ => "muted",
        };
}
