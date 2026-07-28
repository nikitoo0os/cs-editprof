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
        .\music_analyzer.py
    if ($LASTEXITCODE -ne 0) { throw "Music analyzer packaging failed." }
}
finally {
    Pop-Location
}

Write-Output (Join-Path $output "music-analyzer.exe")
