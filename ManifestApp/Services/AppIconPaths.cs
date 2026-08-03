namespace ManifestApp.Services;

internal static class AppIconPaths
{
    internal static string ResolveIconPath()
    {
        var assetsIco = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(assetsIco))
            return assetsIco;

        var markPng = Path.Combine(AppContext.BaseDirectory, "Assets", "OpenSteamMark.png");
        if (File.Exists(markPng))
            return markPng;

        var logoPng = Path.Combine(AppContext.BaseDirectory, "Assets", "OpenSteamAppLogo.png");
        if (File.Exists(logoPng))
            return logoPng;

        return assetsIco;
    }
}
