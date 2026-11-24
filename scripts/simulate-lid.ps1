param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("closed", "open")]
    [string]$State
)

$homeDir = if ($env:BEKKY_WAKE_HOME) { $env:BEKKY_WAKE_HOME } else { Join-Path $env:LOCALAPPDATA "bekky-wake" }
New-Item -ItemType Directory -Path $homeDir -Force | Out-Null
$triggerPath = Join-Path $homeDir "simulate-lid.trigger"
Set-Content -Path $triggerPath -Value $State -NoNewline -Encoding UTF8
Write-Host "Wrote '$State' to $triggerPath"
