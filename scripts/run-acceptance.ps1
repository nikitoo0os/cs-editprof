[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$JobPath,

    [ValidateRange(1, 20)]
    [int]$Count = 3,

    [string]$RenderAgentPath = (
        Join-Path $PSScriptRoot '..\src\Cs2Highlight.RenderAgent\bin\Release\net8.0\render-agent.exe'
    )
)

$ErrorActionPreference = 'Stop'
$resolvedJob = (Resolve-Path -LiteralPath $JobPath).Path
$resolvedAgent = (Resolve-Path -LiteralPath $RenderAgentPath).Path
$template = Get-Content -Raw -LiteralPath $resolvedJob | ConvertFrom-Json
$outputParent = Split-Path -Parent $template.outputDirectory
$acceptanceRoot = Join-Path $PSScriptRoot '..\artifacts\acceptance-runs'
New-Item -ItemType Directory -Path $acceptanceRoot -Force | Out-Null

$results = for ($index = 1; $index -le $Count; $index++) {
    $suffix = '{0:yyyyMMdd-HHmmss}-{1:D2}' -f (Get-Date), $index
    $runJob = $template.PSObject.Copy()
    $runJob.jobId = "$($template.jobId)-acceptance-$suffix"
    $runJob.outputDirectory = Join-Path $outputParent $runJob.jobId
    $runJobPath = Join-Path $acceptanceRoot "$($runJob.jobId).json"
    $runJob | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $runJobPath -Encoding UTF8

    $json = & $resolvedAgent render --job $runJobPath | Out-String
    $exitCode = $LASTEXITCODE
    $resultPath = Join-Path $acceptanceRoot "$($runJob.jobId).result.json"
    $json | Set-Content -LiteralPath $resultPath -Encoding UTF8
    $result = $json | ConvertFrom-Json

    [pscustomobject]@{
        Run = $index
        JobId = $runJob.jobId
        Success = $result.success -eq $true
        ExitCode = $exitCode
        OutputFile = $result.outputFile
        DurationMilliseconds = $result.durationMilliseconds
        ResultPath = $resultPath
    }

    if ($exitCode -ne 0 -or $result.success -ne $true) {
        throw "Acceptance run $index failed. Inspect $resultPath"
    }
}

$results | Format-Table -AutoSize
