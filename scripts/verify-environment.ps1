[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$SettingsPath
)

$ErrorActionPreference = 'Stop'
$settings = Get-Content -Raw -LiteralPath $SettingsPath | ConvertFrom-Json
$environment = $settings.RenderEnvironment
$checks = [ordered]@{
    Windows = $IsWindows -or $env:OS -eq 'Windows_NT'
    HLAE = Test-Path -LiteralPath $environment.HlaeExecutablePath -PathType Leaf
    CS2 = Test-Path -LiteralPath $environment.Cs2ExecutablePath -PathType Leaf
    Steam = Test-Path -LiteralPath $environment.SteamExecutablePath -PathType Leaf
    WorkingRootConfigured = -not [string]::IsNullOrWhiteSpace($environment.WorkingRoot)
    AutomationVerified = $environment.AutomationVerified -eq $true
    InteractiveSession = [Environment]::UserInteractive
}

$checks.GetEnumerator() | ForEach-Object {
    [pscustomobject]@{ Check = $_.Key; Success = $_.Value }
} | Format-Table -AutoSize

if ($checks.Values -contains $false) { exit 20 }
