param(
    [string]$RepoName = "bekky-wake",
    [string]$Tag = "v1.0.1"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    Write-Error "git is not installed or not in PATH."
    exit 1
}

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Write-Error "GitHub CLI (gh) is not installed or not in PATH."
    exit 1
}

gh auth status | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Error "Not signed in to GitHub CLI. Run: gh auth login"
    exit 1
}

if (-not (Test-Path (Join-Path $repoRoot ".git"))) {
    git init -b main
}

if (-not (git config user.name)) {
    $ghUser = (gh api user -q ".login")
    git config user.name $ghUser
}
if (-not (git config user.email)) {
    $ghId = (gh api user -q ".id")
    $ghLogin = (gh api user -q ".login")
    git config user.email "$ghId+$ghLogin@users.noreply.github.com"
}

git add -A
$pending = git status --porcelain
if ($pending) {
    git commit -m "Release bekky-wake $Tag"
}

$hasOrigin = (git remote) -contains "origin"
if (-not $hasOrigin) {
    gh repo create $RepoName --public `
        --description "Native Windows 10/11 system tray app that safely keeps your laptop awake with the lid closed for timed AI agent & build sessions." `
        --source . --remote origin --push
} else {
    git push -u origin main
}

$owner = (gh api user -q ".login")
gh repo edit "$owner/$RepoName" --add-topic windows,wpf,keep-awake,clamshell,ai-agents,system-tray,dotnet

$zipPath = Join-Path $repoRoot "bekky-wake-$Tag-win-x64.zip"
if (Test-Path $zipPath) {
    $hash = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash
    $notes = @"
## bekky-wake $Tag (Windows 10/11 x64)

Self-contained single-file Windows release — no separate .NET installation required.

### Quick Start
1. Download ``bekky-wake-$Tag-win-x64.zip`` below and extract it.
2. Double-click ``bekky-wake.exe`` (on first run, if Windows SmartScreen prompts, choose **More info -> Run anyway**).
3. Look for the eye icon in your Windows System Tray near the bottom-right clock.

### Checksum (SHA-256)
- ``bekky-wake-$Tag-win-x64.zip``: ``$hash``
"@
    $prevPref = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    gh release view $Tag 2>&1 | Out-Null
    $releaseExists = ($LASTEXITCODE -eq 0)
    $ErrorActionPreference = $prevPref

    if ($releaseExists) {
        gh release upload $Tag $zipPath --clobber
        gh release edit $Tag --title "bekky-wake $Tag" --notes $notes
    } else {
        gh release create $Tag $zipPath --title "bekky-wake $Tag" --notes $notes
    }
}

Write-Host "Published publicly at: https://github.com/$owner/$RepoName"
