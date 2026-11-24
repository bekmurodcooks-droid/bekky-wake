param(
    [switch]$Purge
)

$ErrorActionPreference = "Stop"

$homeDir = if ($env:BEKKY_WAKE_HOME) { $env:BEKKY_WAKE_HOME } else { Join-Path $env:LOCALAPPDATA "bekky-wake" }
$stateFile = Join-Path $homeDir "state.json"
$sessionFile = Join-Path $homeDir "session.json"

# Try running backstop from unpacked release folder or local build output before inspecting journal.
$candidates = @(
    (Join-Path $PSScriptRoot "bekky-wake.exe"),
    (Join-Path $PSScriptRoot "..\publish-standalone\bekky-wake.exe"),
    (Join-Path $PSScriptRoot "..\publish\bekky-wake.exe"),
    (Join-Path $PSScriptRoot "..\src\BekkyWake\bin\Release\net10.0-windows\win-x64\publish\bekky-wake.exe"),
    (Join-Path $PSScriptRoot "..\src\BekkyWake\bin\Release\net10.0-windows\bekky-wake.exe"),
    (Join-Path $PSScriptRoot "..\src\BekkyWake\bin\Debug\net10.0-windows\bekky-wake.exe")
)
$exe = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($exe) {
    Write-Host "Running recovery check via $exe --backstop ..."
    & $exe --backstop
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Recovery backstop reported unresolved changes (exit $LASTEXITCODE). Resolve warnings in bekky-wake before uninstalling."
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
            Write-Error "state.json still contains unresolved changes. Reopen bekky-wake to complete recovery before uninstalling."
            exit 1
        }
        if ($state.savedAudioOutputs -and $state.savedAudioOutputs.Count -gt 0) {
            Write-Error "Saved audio outputs are still waiting to be restored. Reconnect your audio output or choose 'Stop waiting' in bekky-wake first."
            exit 1
        }
    }
    catch {
        Write-Error "state.json could not be parsed; refusing to remove recovery backstop while journal state is unknown."
        exit 1
    }
}

if (Test-Path $sessionFile) {
    Write-Error "An active or uncleaned session.json still exists at $sessionFile. End the session in bekky-wake first."
    exit 1
}

# Remove scheduled backstop task if registered.
$prevPref = $ErrorActionPreference
$ErrorActionPreference = "Continue"
schtasks /Query /TN "BekkyWakeBackstop" 2>&1 | Out-Null
$hasTask = ($LASTEXITCODE -eq 0)
$ErrorActionPreference = $prevPref

if ($hasTask) {
    schtasks /Delete /F /TN "BekkyWakeBackstop" | Out-Null
    Write-Host "Removed scheduled task BekkyWakeBackstop."
}

# Remove startup registry entry if present.
$runPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
if (Test-Path $runPath) {
    $prop = Get-ItemProperty -Path $runPath -Name "bekky-wake" -ErrorAction SilentlyContinue
    if ($prop) {
        Remove-ItemProperty -Path $runPath -Name "bekky-wake"
        Write-Host "Removed Windows startup entry."
    }
}

if ($Purge -and (Test-Path $homeDir)) {
    Remove-Item -Path $homeDir -Recurse -Force
    Write-Host "Purged data directory: $homeDir"
}

Write-Host "bekky-wake machinery cleanly removed."
