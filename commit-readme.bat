@echo off
powershell.exe -NoExit -ExecutionPolicy Bypass -Command "cd '%~dp0'; git add README.md; git commit -m 'Add README'; git push origin main; Write-Host 'Done.' -ForegroundColor Green"
