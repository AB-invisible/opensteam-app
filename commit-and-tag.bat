@echo off
powershell.exe -NoExit -ExecutionPolicy Bypass -Command "cd '%~dp0'; git add -A; git commit -m 'v3.2.2: fix ContentDialog crash on Install manifests'; git tag v3.2.2; git push origin main --tags; Write-Host 'Done.' -ForegroundColor Green"
