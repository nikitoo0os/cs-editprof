[CmdletBinding()]
param(
  [ValidateSet("Development", "RenderMachine")]
  [string]$Mode = "Development",
  [switch]$Stop,
  [switch]$QuickClose,
  [switch]$Status,
  [switch]$Doctor,
  [switch]$ResetDevelopmentData,
  [switch]$RunAcceptance
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot ".")).Path
$stateRoot = Join-Path $repoRoot "storage"
$statePath = Join-Path $stateRoot "quickstart-state.json"
$logRoot = Join-Path $repoRoot "logs"

function Test-Command([string]$Name) {
  return $null -ne (Get-Command $Name -ErrorAction SilentlyContinue)
}
function Get-State {
  if (-not (Test-Path -LiteralPath $statePath)) { return $null }
  try { return Get-Content -Raw -LiteralPath $statePath | ConvertFrom-Json } catch { return $null }
}
function Write-State([int]$ProcessId, [string]$ProcessMode) {
  New-Item -ItemType Directory -Force -Path $stateRoot, $logRoot | Out-Null
  @{ Pid = $ProcessId; Mode = $ProcessMode; StartedAtUtc = [DateTime]::UtcNow.ToString("O"); Root = $repoRoot } |
    ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding utf8
}
function Show-Status {
  $state = Get-State
  if ($null -eq $state) { Write-Output "CSHighlighter: stopped"; return }
  $process = Get-Process -Id ([int]$state.Pid) -ErrorAction SilentlyContinue
  if ($null -eq $process) { Write-Output "CSHighlighter: stale state"; return }
  Write-Output "CSHighlighter: running"
  Write-Output "PID: $($state.Pid)"
  Write-Output "Mode: $($state.Mode)"
  Write-Output "Started UTC: $($state.StartedAtUtc)"
}
function Get-DescendantProcessIds([int]$ParentProcessId) {
  $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $ParentProcessId")
  foreach ($child in $children) {
    $childId = [int]$child.ProcessId
    Write-Output $childId
    Get-DescendantProcessIds $childId
  }
}
function Stop-ProcessTree([int]$RootProcessId) {
  $descendants = @(Get-DescendantProcessIds $RootProcessId)
  for ($index = $descendants.Count - 1; $index -ge 0; $index--) {
    Stop-Process -Id ([int]$descendants[$index]) -Force -ErrorAction SilentlyContinue
  }
  Stop-Process -Id $RootProcessId -Force -ErrorAction SilentlyContinue
}
function Stop-Project {
  $state = Get-State
  $stopped = $false
  if ($null -ne $state -and $state.Root -eq $repoRoot) {
    $process = Get-Process -Id ([int]$state.Pid) -ErrorAction SilentlyContinue
    if ($null -ne $process) {
      Stop-ProcessTree $process.Id
      Write-Output "Stopped project process $($process.Id)."
      $stopped = $true
    }
  }
  Remove-Item -LiteralPath $statePath -Force -ErrorAction SilentlyContinue
  if (-not $stopped) { Write-Output "No project process is registered." }
}
function Close-ProjectQuickly {
  Stop-Project
  $projectPath = [IO.Path]::GetFullPath((Join-Path $repoRoot "src\Cs2Highlight.Web\Cs2Highlight.Web.csproj"))
  $projectPattern = [Regex]::Escape($projectPath.Replace('/', '\'))
  $relativeProjectPattern = [Regex]::Escape("src\Cs2Highlight.Web\Cs2Highlight.Web.csproj")
  $matches = @(Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe'" | Where-Object {
    $commandLine = [string]$_.CommandLine
    $commandLine -match '(?i)dotnet\.exe"?\s+run' -and
      (($commandLine.Replace('/', '\') -match $projectPattern) -or
       ($commandLine.Replace('/', '\') -match $relativeProjectPattern))
  })
  foreach ($match in $matches) {
    Stop-ProcessTree ([int]$match.ProcessId)
    Write-Output "Quick-closed project process $($match.ProcessId)."
  }
  if ($matches.Count -eq 0) { Write-Output "No manually started project host found." }
}
function Invoke-Doctor {
  $checks = @("git", "dotnet", "node", "npm", "python")
  if ($Mode -eq "RenderMachine") { $checks += @("ffmpeg", "ffprobe") }
  Write-Output "Component | Version/Path | Status"
  foreach ($name in $checks) {
    $command = Get-Command $name -ErrorAction SilentlyContinue
    if ($null -eq $command) { Write-Output "$name | - | Missing"; continue }
    $version = (& $name --version 2>$null | Select-Object -First 1)
    Write-Output "$name | $version | Ready"
  }
  $drive = Get-PSDrive -Name ([IO.Path]::GetPathRoot($repoRoot).TrimEnd('\').TrimEnd(':')) -ErrorAction SilentlyContinue
  if ($null -ne $drive) { Write-Output "storage | $($drive.Free) bytes free | $(if ($drive.Free -gt 1GB) { 'Ready' } else { 'Low space' })" }
  if ($Mode -eq "RenderMachine") { Write-Output "Steam/CS2/HLAE | Configured paths are environment-specific | Inspect manually" }
}

if ($Status) { Show-Status; exit 0 }
if ($Stop) { Stop-Project; exit 0 }
if ($QuickClose) { Close-ProjectQuickly; exit 0 }
if ($Doctor) { Invoke-Doctor; exit 0 }
if ($ResetDevelopmentData) {
  if ($Mode -ne "Development") { throw "-ResetDevelopmentData is available only in Development mode." }
  $databasePath = Join-Path $repoRoot "storage\generations.db"
  $generationPath = Join-Path $repoRoot "storage\generations"
  Write-Output "The following development paths will be removed:"
  Write-Output $databasePath
  Write-Output $generationPath
  $confirmation = Read-Host "Type RESET to continue"
  if ($confirmation -ne "RESET") { throw "Reset cancelled." }
  Remove-Item -LiteralPath $databasePath -Force -ErrorAction SilentlyContinue
  if (Test-Path -LiteralPath $generationPath) { Remove-Item -LiteralPath $generationPath -Recurse -Force }
  Write-Output "Development data reset."
  exit 0
}

if (-not (Test-Command "dotnet")) { throw ".NET SDK 8 or newer is required." }
if (-not (Test-Command "npm")) { throw "Node.js/npm is required for the frontend build." }
$sdkLines = @(dotnet --list-sdks)
$sdkMajors = @($sdkLines | ForEach-Object {
  if ($_ -match '^(?<major>\d+)\.') { [int]$Matches["major"] }
})
if (-not ($sdkMajors | Where-Object { $_ -ge 8 })) {
  $installedSdks = if ($sdkLines.Count -gt 0) { [string]::Join(", ", $sdkLines) } else { "none" }
  throw ".NET SDK 8 or newer is required. Installed SDKs: $installedSdks"
}
New-Item -ItemType Directory -Force -Path $stateRoot, $logRoot | Out-Null
$localConfig = Join-Path $repoRoot "src\Cs2Highlight.Web\appsettings.local.json"
$localExample = Join-Path $repoRoot "appsettings.local.example.json"
if (-not (Test-Path -LiteralPath $localConfig) -and (Test-Path -LiteralPath $localExample)) {
  Copy-Item -LiteralPath $localExample -Destination $localConfig
  Write-Output "Created $localConfig from the local example."
}
dotnet restore "$repoRoot\Cs2Highlight.RenderPoC.sln"
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed." }
if (Test-Path -LiteralPath (Join-Path $repoRoot "package-lock.json")) {
  npm ci --ignore-scripts
  if ($LASTEXITCODE -ne 0) { throw "npm ci failed." }
  npm run css:build
  if ($LASTEXITCODE -ne 0) { throw "Frontend build failed." }
}
dotnet build "$repoRoot\Cs2Highlight.RenderPoC.sln" --no-restore
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed." }
if ($Mode -eq "RenderMachine") {
  & "$repoRoot\scripts\verify-environment.ps1"
  if ($LASTEXITCODE -ne 0) { throw "RenderMachine dependencies are not ready." }
}
if ($RunAcceptance) {
  if ($Mode -ne "RenderMachine") { throw "-RunAcceptance requires -Mode RenderMachine." }
  & "$repoRoot\scripts\run-acceptance.ps1"
  if ($LASTEXITCODE -ne 0) { throw "Acceptance run failed." }
  exit 0
}
$existing = Get-State
if ($null -ne $existing -and $null -ne (Get-Process -Id ([int]$existing.Pid) -ErrorAction SilentlyContinue)) {
  Show-Status
  exit 0
}
$webLog = Join-Path $logRoot "web.log"
$webErrorLog = Join-Path $logRoot "web-error.log"
$webArgs = @("run", "--project", "$repoRoot\src\Cs2Highlight.Web\Cs2Highlight.Web.csproj", "--no-build", "--no-restore")
$web = Start-Process dotnet -ArgumentList $webArgs -WorkingDirectory $repoRoot -RedirectStandardOutput $webLog -RedirectStandardError $webErrorLog -PassThru -WindowStyle Hidden
Write-State $web.Id $Mode
Start-Sleep -Seconds 2
try {
  $health = Invoke-WebRequest "http://localhost:5000/health/live" -TimeoutSec 10 -UseBasicParsing
  if ($health.StatusCode -ne 200) { throw "Health check returned $($health.StatusCode)." }
} catch {
  Stop-Project
  throw
}
Write-Output "CSHighlighter is ready: http://localhost:5000"
Write-Output "Use .\quickstart.ps1 -Status or -Stop to manage it."
