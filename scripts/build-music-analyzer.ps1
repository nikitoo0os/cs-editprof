param(
    [string]$OutputDirectory = ".\artifacts\music-analyzer",
    [switch]$SkipInstall
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$toolRoot = Join-Path $repositoryRoot "tools\music-analyzer"
$output = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))

Push-Location $toolRoot
try {
    if (-not $SkipInstall) {
        python -m pip install -r .\requirements.txt
        if ($LASTEXITCODE -ne 0) { throw "Music analyzer dependency installation failed." }
    }
    python -m unittest -v
    if ($LASTEXITCODE -ne 0) { throw "Music analyzer tests failed." }
    python -m PyInstaller --noconfirm --clean --onefile `
        --name music-analyzer `
        --distpath $output `
        .\music_analyzer.py
    if ($LASTEXITCODE -ne 0) { throw "Music analyzer packaging failed." }
}
finally {
    Pop-Location
}

Write-Output (Join-Path $output "music-analyzer.exe")
