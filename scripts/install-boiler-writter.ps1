[CmdletBinding()]
param(
    [string]$OutputDirectory = ".\artifacts\boiler-writter"
)

$ErrorActionPreference = "Stop"
$version = "1.7.0"
$downloadUri = "https://github.com/akiver/boiler-writter/releases/download/v$version/boiler-writter-win-$version.zip"
$expectedSha256 = "eda6ce361215c4c0427332e55811ffd621755b264ae7df0f5385ef5916331141"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$output = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot "artifacts"))
$executable = Join-Path $output "boiler-writter.exe"
$steamApi = Join-Path $output "steam_api64.dll"

if (-not $output.StartsWith("$artifactsRoot\", [StringComparison]::OrdinalIgnoreCase)) {
    throw "boiler-writter output must be inside $artifactsRoot."
}
if ((Test-Path -LiteralPath $executable -PathType Leaf) -and
    (Test-Path -LiteralPath $steamApi -PathType Leaf)) {
    Write-Output $executable
    exit 0
}

$temporaryRoot = Join-Path $artifactsRoot (".boiler-writter-install-" + [Guid]::NewGuid().ToString("N"))
$archive = Join-Path $temporaryRoot "boiler-writter.zip"
$extractRoot = Join-Path $temporaryRoot "extract"
try {
    New-Item -ItemType Directory -Path $temporaryRoot, $extractRoot -Force | Out-Null
    Invoke-WebRequest -Uri $downloadUri -OutFile $archive -UseBasicParsing
    $actualSha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualSha256 -ne $expectedSha256) {
        throw "boiler-writter archive checksum mismatch. Expected $expectedSha256, got $actualSha256."
    }
    Expand-Archive -LiteralPath $archive -DestinationPath $extractRoot
    $packageExecutable = Get-ChildItem -LiteralPath $extractRoot -Filter "boiler-writter.exe" -File -Recurse |
        Select-Object -First 1
    if ($null -eq $packageExecutable) {
        throw "Downloaded boiler-writter package has an unexpected layout."
    }
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    Get-ChildItem -LiteralPath $packageExecutable.DirectoryName -File |
        Copy-Item -Destination $output -Force
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
Write-Output $executable
