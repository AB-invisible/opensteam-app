# Changelog

## 1.5.1 — Fix paired API keys rejected as invalid

- Use v2 activate/stats endpoints with Bearer auth (legacy routes block new keys)
- Verify API key with server before unlocking the pairing gate
- Server: allow localhost HTTP API auth in production dev mode

## 1.5.0 — Pairing gate & icon fixes

- Launch pairing screen blocks the app until Discord pairing completes
- Click pairing code to copy to clipboard
- Fixed taskbar icon showing legacy GameGen artwork
- Auto-update on launch

## 1.0.0 — Initial OpenSteam release

- OpenSteam Desktop App for Windows
- Steam manifest search, install, and online fixes
- Connects to `https://opensteam.lol`
- Install: `irm https://raw.githubusercontent.com/AB-invisible/opensteam-app/main/download.ps1 | iex`
