[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot ".")).Path
$quickstart = Join-Path $repoRoot "quickstart.ps1"

if (-not (Test-Path -LiteralPath $quickstart)) {
  throw "quickstart.ps1 was not found in $repoRoot."
}

& $quickstart -QuickClose
if ($LASTEXITCODE -ne 0) {
  throw "The CSHighlighter host could not be stopped."
}

Write-Output "CSHighlighter host is stopped."
