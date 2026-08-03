<#
    OpenSteam App — interactive installer & maintenance console.

    Usage
    -----
    Local:   powershell -ExecutionPolicy Bypass -File .\download.ps1
    Online:  irm https://pastebin.com/raw/0iSnYpj3 | iex
    Alt:     irm https://raw.githubusercontent.com/AB-invisible/opensteam-app/main/download.ps1 | iex

    Headless one-shot (no menu):
        .\download.ps1 -Action install
        .\download.ps1 -Action repair
        .\download.ps1 -Action uninstall
#>

[CmdletBinding()]
param(
    [string] $Repo          = 'AB-invisible/opensteam-app',
    [string] $AssetName     = 'OpenSteamApp.exe',
    [string] $InstallDir    = (Join-Path $env:LOCALAPPDATA 'OpenSteamApp'),

    # Optional direct .exe URL for SteamTools — when empty, command 6 opens the website.
    [string] $SteamToolsUrl = '',

    # Headless mode: install | update | repair | uninstall | launch | open | status | steamtools
    [string] $Action        = ''
)

# When invoked via `irm ... | iex`, install immediately instead of showing the menu.
if (-not $Action -and -not $MyInvocation.MyCommand.Path) {
    $Action = 'install'
}

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch { }
try { $host.UI.RawUI.WindowTitle = "app" } catch { }

$script:ExePath = Join-Path $InstallDir $AssetName
$script:Headers = @{
    'Accept'     = 'application/vnd.github+json'
    'User-Agent' = 'OpenSteam-Console'
}
$script:Latest  = $null

# ── Console primitives ────────────────────────────────────────────────────────

function Write-Color {
    param(
        [string] $Text,
        [ConsoleColor] $Color = 'White',
        [switch] $NoNewline
    )
    Write-Host $Text -ForegroundColor $Color -NoNewline:$NoNewline
}

function Write-Step($msg) { Write-Color "   → $msg" Cyan }
function Write-Ok  ($msg) { Write-Color "   ✔ $msg" Green }
function Write-Warn($msg) { Write-Color "   ! $msg" Yellow }
function Write-Fail($msg) { Write-Color "   ✖ $msg" Red }

function Pause-Menu {
    Write-Host ""
    Write-Color "    Press ENTER to return to the menu..." DarkGray
    [void](Read-Host)
}

# ── Version helpers ───────────────────────────────────────────────────────────

function ConvertTo-AppVersion {
    param([string] $raw)
    if (-not $raw) { return $null }
    $clean = $raw -replace '^[vV]', '' -replace '[^\d.].*$', ''
    $parts = $clean.Split('.') | Where-Object { $_ -ne '' }
    while ($parts.Count -lt 4) { $parts += '0' }
    try { return [Version]"$($parts[0..3] -join '.')" } catch { return $null }
}

function Get-InstalledVersion {
    if (-not (Test-Path $script:ExePath)) { return $null }
    try { return (Get-Item $script:ExePath).VersionInfo.ProductVersion } catch { return $null }
}

function Get-RunningProcesses {
    $name = [IO.Path]::GetFileNameWithoutExtension($AssetName)
    return @(Get-Process -Name $name -ErrorAction SilentlyContinue)
}

function Install-ReleaseAssets {
    param($Release)

    $assetsDir = Join-Path $InstallDir 'Assets'
    New-Item -Path $assetsDir -ItemType Directory -Force | Out-Null

    foreach ($name in @('AppIcon.ico', 'OpenSteamAppLogo.png', 'OpenSteamMark.png')) {
        $asset = $Release.assets | Where-Object { $_.name -eq $name } | Select-Object -First 1
        if (-not $asset) { continue }

        $dest = Join-Path $assetsDir $name
        Write-Step "Downloading $name"
        Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $dest -UseBasicParsing
        Write-Ok "Saved $name"
    }
}

function Get-ShortcutIconLocation {
    $iconPath = Join-Path $InstallDir 'Assets\AppIcon.ico'
    if (Test-Path $iconPath) { return "$iconPath,0" }
    return $script:ExePath
}

function Get-LatestRelease {
    if ($script:Latest) { return $script:Latest }
    try {
        Write-Step "Fetching latest release from GitHub API"
        $script:Latest = Invoke-RestMethod "https://api.github.com/repos/$Repo/releases/latest" -Headers $script:Headers

        # Debugging output to inspect the API response
        if (-not $script:Latest) {
            Write-Warn "GitHub API returned no data. Check your connection or repository name."
            return $null
        }
        if (-not $script:Latest.assets) {
            Write-Warn "GitHub API response does not contain 'assets'. Response: $($script:Latest | ConvertTo-Json -Depth 3)"
            return $null
        }

        return $script:Latest
    } catch {
        Write-Fail "Failed to fetch latest release: $($_.Exception.Message)"
        return $null
    }
}

function Stop-RunningInstances {
    $running = Get-RunningProcesses
    if ($running.Count -gt 0) {
        Write-Step "Stopping $($running.Count) running instance(s) so files can be replaced"
        $running | Stop-Process -Force
        Start-Sleep -Milliseconds 600
    }
}

# ── Banner / Menu ─────────────────────────────────────────────────────────────

function Write-Banner {
    Clear-Host
    Write-Host ""
    Write-Color "   ┌──────────────────────────────────────────────────────────────────┐" Magenta
    Write-Color "   │                                                                  │" Magenta
    Write-Color "   │   " Magenta -NoNewline
    Write-Color "O P E N S T E A M   A P P" Magenta -NoNewline
    Write-Color "   ·   " DarkGray  -NoNewline
    Write-Color "O P E N S T E A M" Blue   -NoNewline
    Write-Color "          │" Magenta
    Write-Color "   │                                                                  │" Magenta
    Write-Color "   └──────────────────────────────────────────────────────────────────┘" Magenta
    Write-Host ""

    Write-Color "    Repository  " DarkGray -NoNewline; Write-Color $Repo White
    Write-Color "    Install dir " DarkGray -NoNewline; Write-Color $InstallDir White

    $installed = Get-InstalledVersion
    $rel       = Get-LatestRelease
    $tag       = if ($rel) { $rel.tag_name } else { $null }
    $running   = Get-RunningProcesses

    Write-Color "    Status      " DarkGray -NoNewline
    if (-not $installed) {
        Write-Color "Not installed" Yellow
    } else {
        Write-Color "v$installed" White -NoNewline
        if ($tag) {
            $a = ConvertTo-AppVersion $installed
            $b = ConvertTo-AppVersion $tag
            if ($a -and $b) {
                if     ($b -gt $a) { Write-Color "  ·  update available ($tag)" Yellow }
                elseif ($a -gt $b) { Write-Color "  ·  ahead of release ($tag)" DarkGray }
                else               { Write-Color "  ·  up to date" Green }
            } else {
                Write-Color "  ·  latest $tag" DarkGray
            }
        } else {
            Write-Host ""
        }
    }
    if ($running.Count -gt 0) {
        Write-Color "    Process     " DarkGray -NoNewline
        Write-Color "running (PID $(($running | Select-Object -First 1).Id))" Cyan
    }

    Write-Host ""
    Write-Color "   ────────────────────────────────────────────────────────────────────" DarkMagenta
    Write-Color "    COMMANDS" White
    Write-Color "   ────────────────────────────────────────────────────────────────────" DarkMagenta
    Write-Host ""
}

function Write-Menu {
    Write-Color "    [1]" Magenta -NoNewline; Write-Color "  Install / Update OpenSteam App" White
    Write-Color "    [2]" Magenta -NoNewline; Write-Color "  Repair    " White -NoNewline; Write-Color "(force a clean re-download of the latest)" DarkGray
    Write-Color "    [3]" Magenta -NoNewline; Write-Color "  Uninstall OpenSteam App" White
    Write-Color "    [4]" Magenta -NoNewline; Write-Color "  Launch OpenSteam App" White
    Write-Color "    [5]" Magenta -NoNewline; Write-Color "  Open install folder" White
    Write-Color "    [6]" Magenta -NoNewline; Write-Color "  Download SteamTools" White
    Write-Color "    [7]" Magenta -NoNewline; Write-Color "  Show install status" White
    Write-Color "    [Q]" DarkGray -NoNewline; Write-Color "  Quit" DarkGray
    Write-Host ""
    Write-Color "   ────────────────────────────────────────────────────────────────────" DarkMagenta
    Write-Host ""
}

# ── Action: Install / Update ──────────────────────────────────────────────────

function Invoke-Install {
    param([switch] $Force)

    $rel = Get-LatestRelease
    if (-not $rel) {
        Write-Fail "GitHub API unreachable — check your connection and try again."
        Pause-Menu; return
    }

    $tag   = $rel.tag_name
    $asset = $rel.assets | Where-Object { $_.name -eq $AssetName } | Select-Object -First 1
    if (-not $asset) {
        Write-Fail "Release $tag does not contain '$AssetName'."
        Write-Color "    Assets present: $(@($rel.assets.name) -join ', ')" DarkGray
        Pause-Menu; return
    }

    $installed = Get-InstalledVersion
    if ($installed -and -not $Force) {
        $a = ConvertTo-AppVersion $installed
        $b = ConvertTo-AppVersion $tag
        if ($a -and $b -and $a -ge $b) {
            Write-Ok "Already on the latest version (v$installed)."
            Pause-Menu; return
        }
    }

    if (-not (Test-Path $InstallDir)) {
        New-Item -Path $InstallDir -ItemType Directory -Force | Out-Null
    }

    Stop-RunningInstances

    $sizeMb = [Math]::Round($asset.size / 1MB, 2)
    Write-Step "Downloading $AssetName  ·  $tag  ·  $sizeMb MB"
    Write-Color "    → $script:ExePath" DarkGray
    try {
        Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $script:ExePath -UseBasicParsing
        Install-ReleaseAssets -Release $rel
        Write-Ok "Installed OpenSteam App $tag"

        $wshShell = New-Object -ComObject WScript.Shell
        $iconLocation = Get-ShortcutIconLocation

        # Desktop shortcut
        $desktopPath = [Environment]::GetFolderPath('Desktop')
        $desktopShortcutPath = Join-Path $desktopPath 'OpenSteam App.lnk'
        $desktopShortcut = $wshShell.CreateShortcut($desktopShortcutPath)
        $desktopShortcut.TargetPath = $script:ExePath
        $desktopShortcut.WorkingDirectory = $InstallDir
        $desktopShortcut.IconLocation = $iconLocation
        $desktopShortcut.Save()
        Write-Ok "Created Desktop shortcut"

        # Start Menu shortcut
        $startMenuPath = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
        $startMenuShortcutPath = Join-Path $startMenuPath 'OpenSteam App.lnk'
        $startMenuShortcut = $wshShell.CreateShortcut($startMenuShortcutPath)
        $startMenuShortcut.TargetPath = $script:ExePath
        $startMenuShortcut.WorkingDirectory = $InstallDir
        $startMenuShortcut.IconLocation = $iconLocation
        $startMenuShortcut.Save()
        Write-Ok "Created Start Menu shortcut"
    } catch {
        Write-Fail "Download failed: $($_.Exception.Message)"
    }

    Pause-Menu
}

# ── Action: Repair ────────────────────────────────────────────────────────────

function Invoke-Repair {
    Write-Step "Repairing OpenSteam App"
    Stop-RunningInstances

    if (Test-Path $InstallDir) {
        try {
            Remove-Item $InstallDir -Recurse -Force
            Write-Ok "Cleared $InstallDir"
        } catch {
            Write-Fail "Could not clean install folder: $($_.Exception.Message)"
            Pause-Menu; return
        }
    }
    Invoke-Install -Force
}

# ── Action: Uninstall ─────────────────────────────────────────────────────────

function Invoke-Uninstall {
    if (-not (Test-Path $InstallDir)) {
        Write-Warn "Nothing to uninstall — $InstallDir does not exist."
        Pause-Menu; return
    }

    Write-Color "    Remove OpenSteam App from this PC?" Yellow
    Write-Color "    Folder: $InstallDir" DarkGray
    Write-Color "    Type 'y' to confirm: " White -NoNewline
    $ans = Read-Host
    if ($ans -ne 'y' -and $ans -ne 'Y') {
        Write-Warn "Cancelled."
        Pause-Menu; return
    }

    Stop-RunningInstances

    try {
        Remove-Item $InstallDir -Recurse -Force
        Write-Ok "Removed $InstallDir"
    } catch {
        Write-Fail "Uninstall failed: $($_.Exception.Message)"
    }

    $startMenuPath = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\OpenSteam App.lnk'
    if (Test-Path $startMenuPath) {
        try { Remove-Item $startMenuPath -Force; Write-Ok "Removed Start Menu shortcut" } catch { }
    }

    $desktopPath = Join-Path ([Environment]::GetFolderPath('Desktop')) 'OpenSteam App.lnk'
    if (Test-Path $desktopPath) {
        try { Remove-Item $desktopPath -Force; Write-Ok "Removed Desktop shortcut" } catch { }
    }

    Pause-Menu
}

# ── Action: Launch ────────────────────────────────────────────────────────────

function Invoke-Launch {
    if (-not (Test-Path $script:ExePath)) {
        Write-Warn "OpenSteam App is not installed yet — pick [1] first."
        Pause-Menu; return
    }
    Write-Step "Launching OpenSteam App"
    Start-Process -FilePath $script:ExePath
    Write-Ok "Started."
    Start-Sleep -Milliseconds 800
}

# ── Action: Open install folder ───────────────────────────────────────────────

function Invoke-OpenFolder {
    if (-not (Test-Path $InstallDir)) {
        Write-Warn "Folder does not exist yet."
        Pause-Menu; return
    }
    Start-Process -FilePath explorer.exe -ArgumentList $InstallDir
    Write-Ok "Opened $InstallDir"
    Start-Sleep -Milliseconds 600
}

# ── Action: SteamTools ────────────────────────────────────────────────────────

function Invoke-DownloadSteamTools {
    if ($SteamToolsUrl) {
        if (-not (Test-Path $InstallDir)) {
            New-Item -Path $InstallDir -ItemType Directory -Force | Out-Null
        }
        $st = Join-Path $InstallDir 'SteamTools.exe'
        Write-Step "Downloading SteamTools"
        Write-Color "    → $st" DarkGray
        try {
            Invoke-WebRequest -Uri $SteamToolsUrl -OutFile $st -UseBasicParsing
            Write-Ok "Saved to $st"
            Write-Color "    Tip: point OpenSteam → Settings → SteamTools.exe override at this path." DarkGray
        } catch {
            Write-Fail "Download failed: $($_.Exception.Message)"
        }
        Pause-Menu; return
    }

    Write-Step "Opening SteamTools homepage in your default browser"
    try { Start-Process 'https://steamtools.net/' } catch {
        Write-Fail "Couldn't open browser: $($_.Exception.Message)"
    }
    Write-Color "    Tip: rerun with -SteamToolsUrl <direct .exe URL> to auto-download instead." DarkGray
    Pause-Menu
}

# ── Action: Status ────────────────────────────────────────────────────────────

function Invoke-Status {
    $installed = Get-InstalledVersion
    $rel       = Get-LatestRelease
    $running   = Get-RunningProcesses

    Write-Host ""
    Write-Color "    Install path : " DarkGray -NoNewline; Write-Color $script:ExePath White
    Write-Color "    Installed    : " DarkGray -NoNewline
    if ($installed) { Write-Color "v$installed" Green } else { Write-Color "(not installed)" Yellow }
    Write-Color "    Latest tag   : " DarkGray -NoNewline
    if ($rel) { Write-Color $rel.tag_name White } else { Write-Color "(unreachable)" Yellow }
    Write-Color "    Running      : " DarkGray -NoNewline
    if ($running.Count -gt 0) {
        Write-Color "$($running.Count) instance(s) — PIDs: $($running.Id -join ', ')" Cyan
    } else {
        Write-Color "no" DarkGray
    }
    if ((Test-Path $script:ExePath)) {
        $sz = [Math]::Round((Get-Item $script:ExePath).Length / 1MB, 2)
        Write-Color "    On-disk size : " DarkGray -NoNewline; Write-Color "$sz MB" White
    }
    Pause-Menu
}

# ── Dispatcher ────────────────────────────────────────────────────────────────

function Invoke-Action([string] $key) {
    switch ($key.ToLower()) {
        '1'           { Invoke-Install }
        'install'     { Invoke-Install }
        'update'      { Invoke-Install }
        '2'           { Invoke-Repair }
        'repair'      { Invoke-Repair }
        'fix'         { Invoke-Repair }
        '3'           { Invoke-Uninstall }
        'uninstall'   { Invoke-Uninstall }
        '4'           { Invoke-Launch }
        'launch'      { Invoke-Launch }
        'run'         { Invoke-Launch }
        '5'           { Invoke-OpenFolder }
        'open'        { Invoke-OpenFolder }
        '6'           { Invoke-DownloadSteamTools }
        'steamtools'  { Invoke-DownloadSteamTools }
        '7'           { Invoke-Status }
        'status'      { Invoke-Status }
        default       {
            Write-Warn "Unknown command: '$key'"
            Start-Sleep -Milliseconds 800
        }
    }
}

# ── Entry ─────────────────────────────────────────────────────────────────────

if ($Action) {
    Invoke-Action $Action
    return
}

while ($true) {
    Write-Banner
    Write-Menu
    Write-Color "    ▸ Choose an option: " White -NoNewline
    $choice = Read-Host

    if ([string]::IsNullOrWhiteSpace($choice)) { continue }
    if ($choice -match '^(q|quit|exit)$') { 
        Write-Host ""
        Write-Color "    Goodbye." DarkGray
        Write-Host ""
        Start-Sleep -Milliseconds 400
        exit
    }

    Invoke-Action $choice
}
