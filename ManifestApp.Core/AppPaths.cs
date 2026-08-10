namespace ManifestApp.Core;

public static class AppPaths
{
    public const string PublisherFolder = "OpenSteamApp";
    private const string LegacyPublisherFolder = "GameGenApp";

    public static string LocalRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            PublisherFolder);

    private static string LegacyLocalRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            LegacyPublisherFolder);

    public static string SettingsPath => Path.Combine(LocalRoot, "settings.json");

    public static string InstalledRecordsPath => Path.Combine(LocalRoot, "installed_manifests.json");

    public static string ImageCacheDir => Path.Combine(LocalRoot, "image_cache");

    public static void EnsureLayout()
    {
        MigrateLegacyLayoutIfNeeded();
        Directory.CreateDirectory(LocalRoot);
        Directory.CreateDirectory(ImageCacheDir);
    }

    private static void MigrateLegacyLayoutIfNeeded()
    {
        if (!Directory.Exists(LegacyLocalRoot))
            return;

        if (!Directory.Exists(LocalRoot))
        {
            Directory.Move(LegacyLocalRoot, LocalRoot);
            return;
        }

        foreach (var file in Directory.EnumerateFiles(LegacyLocalRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(LegacyLocalRoot, file);
            var target = Path.Combine(LocalRoot, relative);
            if (File.Exists(target))
                continue;

            var targetDir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(targetDir))
                Directory.CreateDirectory(targetDir);

            File.Copy(file, target, overwrite: false);
        }
    }
}
