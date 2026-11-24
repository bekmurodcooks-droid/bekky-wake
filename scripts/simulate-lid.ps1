param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("closed", "open")]
    [string]$State
)

$homeDir = if ($env:INSOMNIA_HOME) { $env:INSOMNIA_HOME } else { Join-Path $env:LOCALAPPDATA "InsomniaWin" }
New-Item -ItemType Directory -Path $homeDir -Force | Out-Null
$triggerPath = Join-Path $homeDir "simulate-lid.trigger"
Set-Content -Path $triggerPath -Value $State -NoNewline -Encoding UTF8
Write-Host "Wrote '$State' to $triggerPath"
