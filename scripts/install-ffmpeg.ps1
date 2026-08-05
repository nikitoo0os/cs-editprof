[CmdletBinding()]
param(
    [string]$OutputDirectory = ".\artifacts\ffmpeg"
)

$ErrorActionPreference = "Stop"
$version = "8.1.2"
$downloadUri = "https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-$version-essentials_build.zip"
$expectedSha256 = "db580001caa24ac104c8cb856cd113a87b0a443f7bdf47d8c12b1d740584a2ec"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$output = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
$bin = Join-Path $output "bin"
$ffmpeg = Join-Path $bin "ffmpeg.exe"
$ffprobe = Join-Path $bin "ffprobe.exe"

if ((Test-Path -LiteralPath $ffmpeg -PathType Leaf) -and
    (Test-Path -LiteralPath $ffprobe -PathType Leaf)) {
    Write-Output $bin
    exit 0
}

$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot "artifacts"))
if (-not $output.StartsWith("$artifactsRoot\", [StringComparison]::OrdinalIgnoreCase)) {
    throw "FFmpeg output must be inside $artifactsRoot."
}

New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null
$temporaryRoot = Join-Path $artifactsRoot (".ffmpeg-install-" + [Guid]::NewGuid().ToString("N"))
$archive = Join-Path $temporaryRoot "ffmpeg.zip"
$extractRoot = Join-Path $temporaryRoot "extract"

try {
    New-Item -ItemType Directory -Path $temporaryRoot, $extractRoot -Force | Out-Null
    Write-Output "Downloading FFmpeg $version..."
    Invoke-WebRequest -Uri $downloadUri -OutFile $archive -UseBasicParsing
    $actualSha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualSha256 -ne $expectedSha256) {
        throw "FFmpeg archive checksum mismatch. Expected $expectedSha256, got $actualSha256."
    }

    Expand-Archive -LiteralPath $archive -DestinationPath $extractRoot
    $packageRoot = Join-Path $extractRoot "ffmpeg-$version-essentials_build"
    $packageFfmpeg = Join-Path $packageRoot "bin\ffmpeg.exe"
    $packageFfprobe = Join-Path $packageRoot "bin\ffprobe.exe"
    if (-not (Test-Path -LiteralPath $packageFfmpeg -PathType Leaf) -or
        -not (Test-Path -LiteralPath $packageFfprobe -PathType Leaf)) {
        throw "Downloaded FFmpeg package has an unexpected layout."
    }

    if (Test-Path -LiteralPath $output) {
        Remove-Item -LiteralPath $output -Recurse -Force
    }
    Move-Item -LiteralPath $packageRoot -Destination $output
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}

Write-Output $bin
