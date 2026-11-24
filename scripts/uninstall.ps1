param(
    [switch]$Purge
)

$ErrorActionPreference = "Stop"

$homeDir = if ($env:INSOMNIA_HOME) { $env:INSOMNIA_HOME } else { Join-Path $env:LOCALAPPDATA "InsomniaWin" }
$stateFile = Join-Path $homeDir "state.json"
$sessionFile = Join-Path $homeDir "session.json"

# Try running backstop from unpacked release folder or local build output before inspecting journal.
$candidates = @(
    (Join-Path $PSScriptRoot "InsomniaWin.exe"),
    (Join-Path $PSScriptRoot "..\publish-standalone\InsomniaWin.exe"),
    (Join-Path $PSScriptRoot "..\publish\InsomniaWin.exe"),
    (Join-Path $PSScriptRoot "..\src\InsomniaWin\bin\Release\net10.0-windows\win-x64\publish\InsomniaWin.exe"),
    (Join-Path $PSScriptRoot "..\src\InsomniaWin\bin\Release\net10.0-windows\InsomniaWin.exe"),
    (Join-Path $PSScriptRoot "..\src\InsomniaWin\bin\Debug\net10.0-windows\InsomniaWin.exe")
)
$exe = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($exe) {
    Write-Host "Running recovery check via $exe --backstop ..."
    & $exe --backstop
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Recovery backstop reported unresolved changes (exit $LASTEXITCODE). Resolve warnings in InsomniaWin before uninstalling."
        exit 1
    }
}

if (Test-Path $stateFile) {
    try {
        $state = Get-Content -Raw -Path $stateFile | ConvertFrom-Json
        $hasDirty = $state.sleepDisabledByUs -or $state.lowPowerSetByUs -or $state.dockerFrozen -or `
                    ($state.frozenProcesses -and $state.frozenProcesses.Count -gt 0) -or `
                    $state.displayDarkenedByUs -or ($null -ne $state.savedLidAction)
        if ($hasDirty) {
            Write-Error "state.json still contains unresolved changes. Reopen InsomniaWin to complete recovery before uninstalling."
            exit 1
        }
        if ($state.savedAudioOutputs -and $state.savedAudioOutputs.Count -gt 0) {
            Write-Error "Saved audio outputs are still waiting to be restored. Reconnect your audio output or choose 'Stop waiting' in InsomniaWin first."
            exit 1
        }
    }
    catch {
        Write-Error "state.json could not be parsed; refusing to remove recovery backstop while journal state is unknown."
        exit 1
    }
}

if (Test-Path $sessionFile) {
    Write-Error "An active or uncleaned session.json still exists at $sessionFile. End the session in InsomniaWin first."
    exit 1
}

# Remove scheduled backstop task if registered.
$query = schtasks /Query /TN "InsomniaWinBackstop" 2>$null
if ($LASTEXITCODE -eq 0) {
    schtasks /Delete /F /TN "InsomniaWinBackstop" | Out-Null
    Write-Host "Removed scheduled task InsomniaWinBackstop."
}

# Remove startup registry entry if present.
$runPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
if (Test-Path $runPath) {
    $prop = Get-ItemProperty -Path $runPath -Name "InsomniaWin" -ErrorAction SilentlyContinue
    if ($prop) {
        Remove-ItemProperty -Path $runPath -Name "InsomniaWin"
        Write-Host "Removed Windows startup entry."
    }
}

if ($Purge -and (Test-Path $homeDir)) {
    Remove-Item -Path $homeDir -Recurse -Force
    Write-Host "Purged data directory: $homeDir"
}

Write-Host "InsomniaWin machinery cleanly removed."
