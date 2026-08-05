[CmdletBinding()]
param(
    [string]$OutputDirectory = ".\artifacts\hlae"
)

$ErrorActionPreference = "Stop"
$version = "2.191.1"
$downloadUri = "https://github.com/advancedfx/advancedfx/releases/download/v$version/hlae_2_191_1.zip"
$expectedSha256 = "307ba9170b151a7df9b7e5604b335c2d8b8df5bf5cb8d6700ae3fd01069da514"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$output = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot "artifacts"))
$hlae = Join-Path $output "HLAE.exe"
$hook = Join-Path $output "x64\AfxHookSource2.dll"
$sourceFfmpegBin = Join-Path $artifactsRoot "ffmpeg\bin"
$hlaeFfmpegBin = Join-Path $output "ffmpeg\bin"

function Install-HlaeFfmpeg {
    $sourceFfmpeg = Join-Path $sourceFfmpegBin "ffmpeg.exe"
    $sourceFfprobe = Join-Path $sourceFfmpegBin "ffprobe.exe"
    if (-not (Test-Path -LiteralPath $sourceFfmpeg -PathType Leaf) -or
        -not (Test-Path -LiteralPath $sourceFfprobe -PathType Leaf)) {
        throw "Install the project-local FFmpeg package before HLAE."
    }

    New-Item -ItemType Directory -Path $hlaeFfmpegBin -Force | Out-Null
    Copy-Item -LiteralPath $sourceFfmpeg -Destination $hlaeFfmpegBin -Force
    Copy-Item -LiteralPath $sourceFfprobe -Destination $hlaeFfmpegBin -Force
}

if ((Test-Path -LiteralPath $hlae -PathType Leaf) -and
    (Test-Path -LiteralPath $hook -PathType Leaf)) {
    Install-HlaeFfmpeg
    Write-Output $hlae
    exit 0
}

if (-not $output.StartsWith("$artifactsRoot\", [StringComparison]::OrdinalIgnoreCase)) {
    throw "HLAE output must be inside $artifactsRoot."
}

New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null
$temporaryRoot = Join-Path $artifactsRoot (".hlae-install-" + [Guid]::NewGuid().ToString("N"))
$archive = Join-Path $temporaryRoot "hlae.zip"
$extractRoot = Join-Path $temporaryRoot "extract"

try {
    New-Item -ItemType Directory -Path $temporaryRoot, $extractRoot -Force | Out-Null
    Write-Output "Downloading HLAE $version..."
    Invoke-WebRequest -Uri $downloadUri -OutFile $archive -UseBasicParsing
    $actualSha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualSha256 -ne $expectedSha256) {
        throw "HLAE archive checksum mismatch. Expected $expectedSha256, got $actualSha256."
    }

    Expand-Archive -LiteralPath $archive -DestinationPath $extractRoot
    $packageHlae = Join-Path $extractRoot "HLAE.exe"
    $packageHook = Join-Path $extractRoot "x64\AfxHookSource2.dll"
    if (-not (Test-Path -LiteralPath $packageHlae -PathType Leaf) -or
        -not (Test-Path -LiteralPath $packageHook -PathType Leaf)) {
        throw "Downloaded HLAE package has an unexpected layout."
    }

    if (Test-Path -LiteralPath $output) {
        Remove-Item -LiteralPath $output -Recurse -Force
    }
    Move-Item -LiteralPath $extractRoot -Destination $output
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}

Install-HlaeFfmpeg
Write-Output $hlae
