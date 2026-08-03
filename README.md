# OpenSteam App

A Windows desktop app for searching the Steam Store, managing Steam game manifests, and installing them via the [OpenSteam](https://opensteam.lol) API — built with WinUI 3 (Windows App SDK, unpackaged).

## Install (recommended)

Run in **Windows PowerShell**:

```powershell
irm https://raw.githubusercontent.com/AB-invisible/opensteam-app/main/download.ps1 | iex
```

This downloads the latest `OpenSteamApp.exe` from GitHub Releases into `%LOCALAPPDATA%\OpenSteamApp\` and creates Start Menu / Desktop shortcuts.

### Installer menu (optional)

```powershell
powershell -ExecutionPolicy Bypass -File .\download.ps1
```

## Requirements

- Windows 10 version 1809 or later
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (included in self-contained publish)
- Steam installed
- An [OpenSteam](https://opensteam.lol) account and API key

## Build from source

```powershell
cd opensteam-app
dotnet restore ManifestApp.slnx
dotnet test ManifestApp.Core.Tests/ManifestApp.Core.Tests.csproj -c Release
dotnet publish ManifestApp/ManifestApp.csproj -c Release -p:Platform=x64
```

Output: `ManifestApp\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\OpenSteamApp.exe`

## Settings

| Setting | Description |
|---------|-------------|
| API key | From your OpenSteam dashboard — required for manifest downloads |
| OpenSteam API base URL | Leave blank to use `https://opensteam.lol` |

Settings are stored in `%LOCALAPPDATA%\OpenSteamApp\settings.json`.

## License

See [LICENSE](LICENSE).
