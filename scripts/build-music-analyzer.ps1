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
    $venv = Join-Path $toolRoot ".venv"
    $python = Join-Path $venv "Scripts\python.exe"
    if (-not (Test-Path -LiteralPath $python)) {
        python -m venv $venv
        if ($LASTEXITCODE -ne 0) { throw "Music analyzer virtual environment creation failed." }
    }
    if (-not $SkipInstall) {
        & $python -m pip install --disable-pip-version-check -r .\requirements.txt
        if ($LASTEXITCODE -ne 0) { throw "Music analyzer dependency installation failed." }
    }
    & $python -m unittest -v
    if ($LASTEXITCODE -ne 0) { throw "Music analyzer tests failed." }
    & $python -m PyInstaller --noconfirm --clean --onefile `
        --name music-analyzer `
        --distpath $output `
        .\music_analyzer.py
    if ($LASTEXITCODE -ne 0) { throw "Music analyzer packaging failed." }
}
finally {
    Pop-Location
}

Write-Output (Join-Path $output "music-analyzer.exe")
