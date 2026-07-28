[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$SettingsPath
)

$ErrorActionPreference = 'Stop'
$settings = Get-Content -Raw -LiteralPath $SettingsPath | ConvertFrom-Json
$environment = $settings.RenderEnvironment
$hookPath = if ([string]::IsNullOrWhiteSpace($environment.HlaeExecutablePath)) {
    ''
} else {
    Join-Path (Split-Path -Parent $environment.HlaeExecutablePath) 'x64\AfxHookSource2.dll'
}
$repairPath = if ([string]::IsNullOrWhiteSpace($environment.DemoRepairExecutablePath)) {
    Join-Path (Split-Path -Parent $SettingsPath) 'tools\cs2-demo-playback-fix.exe'
} else {
    $environment.DemoRepairExecutablePath
}
$ffprobePath = if (
    [string]::IsNullOrWhiteSpace($environment.FfprobeExecutablePath) -and
    -not [string]::IsNullOrWhiteSpace($environment.FfmpegExecutablePath)
) {
    Join-Path (Split-Path -Parent $environment.FfmpegExecutablePath) 'ffprobe.exe'
} elseif ([string]::IsNullOrWhiteSpace($environment.FfprobeExecutablePath)) {
    ''
} else {
    $environment.FfprobeExecutablePath
}
$netConPortAvailable = $false
$netConListener = $null
$netConPort = if ($null -eq $environment.NetConPort) { 32123 } else { [int]$environment.NetConPort }
try {
    $netConListener = [Net.Sockets.TcpListener]::new(
        [Net.IPAddress]::Loopback,
        $netConPort)
    $netConListener.Start()
    $netConPortAvailable = $true
} catch {
    $netConPortAvailable = $false
} finally {
    if ($null -ne $netConListener) {
        $netConListener.Stop()
    }
}
$checks = [ordered]@{
    Windows = $IsWindows -or $env:OS -eq 'Windows_NT'
    HLAE = Test-Path -LiteralPath $environment.HlaeExecutablePath -PathType Leaf
    AfxHookSource2 = -not [string]::IsNullOrWhiteSpace($hookPath) -and (Test-Path -LiteralPath $hookPath -PathType Leaf)
    CS2 = Test-Path -LiteralPath $environment.Cs2ExecutablePath -PathType Leaf
    Steam = Test-Path -LiteralPath $environment.SteamExecutablePath -PathType Leaf
    FFmpeg = Test-Path -LiteralPath $environment.FfmpegExecutablePath -PathType Leaf
    FFprobe = Test-Path -LiteralPath $ffprobePath -PathType Leaf
    DemoCompatibilityRepair = Test-Path -LiteralPath $repairPath -PathType Leaf
    NetConPortAvailable = $netConPortAvailable
    CS2NotRunning = $null -eq (Get-Process -Name cs2 -ErrorAction SilentlyContinue)
    HLAENotRunning = $null -eq (Get-Process -Name HLAE -ErrorAction SilentlyContinue)
    WorkingRootConfigured = -not [string]::IsNullOrWhiteSpace($environment.WorkingRoot)
    AutomationVerified = $environment.AutomationVerified -eq $true
    InteractiveSession = [Environment]::UserInteractive
}

$checks.GetEnumerator() | ForEach-Object {
    [pscustomobject]@{ Check = $_.Key; Success = $_.Value }
} | Format-Table -AutoSize

if ($checks.Values -contains $false) { exit 20 }
