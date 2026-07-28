[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)]
    [string]$StateDirectory
)

$ErrorActionPreference = 'Stop'
$resolvedState = (Resolve-Path -LiteralPath $StateDirectory).Path
$pidFiles = Get-ChildItem -LiteralPath $resolvedState -Filter '*.pid' -File
foreach ($pidFile in $pidFiles) {
    $ownedPid = 0
    if (-not [int]::TryParse((Get-Content -Raw -LiteralPath $pidFile.FullName).Trim(), [ref]$ownedPid)) {
        Write-Warning "Invalid PID file: $($pidFile.FullName)"
        continue
    }

    $process = Get-Process -Id $ownedPid -ErrorAction SilentlyContinue
    if ($null -ne $process -and $PSCmdlet.ShouldProcess("$($process.ProcessName) PID $ownedPid", 'Stop owned render process')) {
        Stop-Process -Id $ownedPid -Force
    }
}
