$REPO_URL = "https://github.com/AB-INVISIBLE/opensteam-app.git"

Set-Location $PSScriptRoot

if (Test-Path ".git") {
    Remove-Item -Recurse -Force ".git"
    Write-Host "Removed old .git folder." -ForegroundColor Yellow
}

git init -b main
git config user.name "AB-INVISIBLE"
git config user.email "pokemgo300@gmail.com"
Write-Host "Git repo initialised." -ForegroundColor Green

git add -A
git status

git commit -m "Initial commit - WinUI 3 app with versioning and update checker"

$existingRemote = git remote | Where-Object { $_ -eq "origin" }
if ($existingRemote) {
    git remote set-url origin $REPO_URL
} else {
    git remote add origin $REPO_URL
}

git push -u origin main

Write-Host ""
Write-Host "Done! Code is on GitHub." -ForegroundColor Green
