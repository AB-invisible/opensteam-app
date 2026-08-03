@echo off
powershell.exe -NoExit -ExecutionPolicy Bypass -Command "cd '%~dp0'; git pull --rebase origin main; git push origin main; Write-Host 'Done.' -ForegroundColor Green"
