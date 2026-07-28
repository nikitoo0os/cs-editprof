param(
    [string]$OutputPath = ".\artifacts\demo-parser\demo-parser.exe"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$parserRoot = Join-Path $repositoryRoot "tools\demo-parser"
$resolvedOutput = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputPath))
$outputDirectory = Split-Path -Parent $resolvedOutput

if (-not (Get-Command go -ErrorAction SilentlyContinue)) {
    throw "Go 1.24 or newer is required. Install it from https://go.dev/dl/."
}

New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
Push-Location $parserRoot
try {
    go test ./...
    if ($LASTEXITCODE -ne 0) {
        throw "Go tests failed with exit code $LASTEXITCODE."
    }
    go build -trimpath -o $resolvedOutput .\cmd\demo-parser
    if ($LASTEXITCODE -ne 0) {
        throw "Go build failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}

Write-Output $resolvedOutput
