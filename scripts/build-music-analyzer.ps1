param(
    [string]$OutputDirectory = ".\artifacts\music-analyzer",
    [ValidateSet("3.10", "3.11")]
    [string]$PythonVersion = "3.11",
    [switch]$SkipInstall
)

$ErrorActionPreference = "Stop"
$supportedPython = @("3.10", "3.11")
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$toolRoot = Join-Path $repositoryRoot "tools\music-analyzer"
$output = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))

function Resolve-BasePython {
    $launcher = Get-Command "py.exe" -ErrorAction SilentlyContinue
    if ($null -ne $launcher) {
        $detected = & $launcher.Source "-$PythonVersion" -c `
            "import sys; print(f'{sys.version_info.major}.{sys.version_info.minor}')"
        if ($LASTEXITCODE -eq 0 -and $detected.Trim() -eq $PythonVersion) {
            return @{
                Command = $launcher.Source
                Prefix = @("-$PythonVersion")
            }
        }
    }

    $pythonCommand = Get-Command "python.exe" -ErrorAction SilentlyContinue
    if ($null -ne $pythonCommand) {
        $detected = & $pythonCommand.Source -c `
            "import sys; print(f'{sys.version_info.major}.{sys.version_info.minor}')"
        if ($LASTEXITCODE -eq 0 -and $detected.Trim() -eq $PythonVersion) {
            return @{
                Command = $pythonCommand.Source
                Prefix = @()
            }
        }
    }

    throw @"
Python $PythonVersion was not found. The music analyzer intentionally supports
Python $($supportedPython -join " or ") for its pinned librosa/PyInstaller stack.
Install it, then open a new PowerShell window:

  winget install -e --id Python.Python.$PythonVersion
  .\scripts\build-music-analyzer.ps1 -PythonVersion $PythonVersion
"@
}

$basePython = Resolve-BasePython
$basePythonCommand = $basePython.Command
$basePythonPrefix = $basePython.Prefix

Push-Location $toolRoot
try {
    $venvSuffix = $PythonVersion.Replace(".", "")
    $venv = Join-Path $toolRoot ".venv-py$venvSuffix"
    $python = Join-Path $venv "Scripts\python.exe"
    if (-not (Test-Path -LiteralPath $python)) {
        & $basePythonCommand @basePythonPrefix -m venv $venv
        if ($LASTEXITCODE -ne 0) { throw "Music analyzer virtual environment creation failed." }
    }
    $venvVersion = & $python -c `
        "import sys; print(f'{sys.version_info.major}.{sys.version_info.minor}')"
    if ($LASTEXITCODE -ne 0 -or $venvVersion.Trim() -ne $PythonVersion) {
        throw "Music analyzer environment uses Python $venvVersion instead of $PythonVersion."
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
        --collect-all scipy `
        --collect-all librosa `
        --collect-all numba `
        --collect-all llvmlite `
        --collect-all sklearn `
        .\music_analyzer.py
    if ($LASTEXITCODE -ne 0) { throw "Music analyzer packaging failed." }

    $executable = Join-Path $output "music-analyzer.exe"
    $ffmpeg = Get-Command "ffmpeg.exe" -ErrorAction SilentlyContinue
    if ($null -eq $ffmpeg) {
        throw "FFmpeg is required for the packaged analyzer smoke test."
    }
    $smokeInput = Join-Path $output ".music-analyzer-smoke.wav"
    $smokeOutput = Join-Path $output ".music-analyzer-smoke.json"
    $smokeSucceeded = $false
    try {
        & $ffmpeg.Source -y -hide_banner -loglevel error `
            -f lavfi -i "sine=frequency=440:sample_rate=48000" `
            -t 5 $smokeInput
        if ($LASTEXITCODE -ne 0) {
            throw "Music analyzer smoke fixture generation failed."
        }
        & $executable analyze --input $smokeInput --output $smokeOutput
        if ($LASTEXITCODE -ne 0 -or
            -not (Test-Path -LiteralPath $smokeOutput -PathType Leaf)) {
            throw "Packaged music analyzer smoke test failed."
        }
        $smoke = Get-Content -LiteralPath $smokeOutput -Raw | ConvertFrom-Json
        if ($smoke.schemaVersion -ne "2.1" -or
            $smoke.analyzer.version -ne "0.3.0" -or
            $smoke.frameHopSeconds -lt 0.02 -or
            $smoke.frameHopSeconds -gt 0.05 -or
            $smoke.frames.Count -eq 0 -or
            $smoke.waveform.schemaVersion -ne "1.0" -or
            $smoke.waveform.samplesPerSecond -lt 100 -or
            $smoke.waveform.samplesPerSecond -gt 200 -or
            $smoke.waveform.peaks.Count -eq 0) {
            throw "Packaged music analyzer returned an unexpected contract."
        }
        $smokeSucceeded = $true
    }
    finally {
        if (Test-Path -LiteralPath $smokeInput) {
            Remove-Item -LiteralPath $smokeInput -Force
        }
        if (Test-Path -LiteralPath $smokeOutput) {
            Remove-Item -LiteralPath $smokeOutput -Force
        }
        if (-not $smokeSucceeded -and
            (Test-Path -LiteralPath $executable -PathType Leaf)) {
            Remove-Item -LiteralPath $executable -Force
        }
    }
}
finally {
    Pop-Location
}

Write-Output (Join-Path $output "music-analyzer.exe")
