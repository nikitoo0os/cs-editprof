[CmdletBinding()]
param(
  [ValidateSet("Development", "RenderMachine")]
  [string]$Mode = "Development",
  [switch]$Stop,
  [switch]$QuickClose,
  [switch]$Status,
  [switch]$Doctor,
  [switch]$Rebuild,
  [switch]$ResetDevelopmentData,
  [switch]$RunAcceptance
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot ".")).Path
$stateRoot = Join-Path $repoRoot "storage"
$statePath = Join-Path $stateRoot "quickstart-state.json"
$buildStateRoot = Join-Path $stateRoot "build-state"
$logRoot = Join-Path $repoRoot "logs"
$listenUrl = "http://0.0.0.0:5000"
$healthUrl = "http://127.0.0.1:5000/health/live"
$firewallRuleName = "CSHighlighter Local Network"

function Test-Command([string]$Name) {
  return $null -ne (Get-Command $Name -ErrorAction SilentlyContinue)
}
function Test-BuildCurrent {
  param(
    [string]$StampPath,
    [string[]]$OutputPaths,
    [string[]]$InputPaths,
    [string[]]$Extensions = @()
  )
  if ($Rebuild) { return $false }
  foreach ($outputPath in $OutputPaths) {
    if (-not (Test-Path -LiteralPath $outputPath -PathType Leaf)) {
      return $false
    }
  }
  $referenceTime = if (Test-Path -LiteralPath $StampPath -PathType Leaf) {
    (Get-Item -LiteralPath $StampPath).LastWriteTimeUtc
  } else {
    ($OutputPaths | ForEach-Object {
      (Get-Item -LiteralPath $_).LastWriteTimeUtc
    } | Sort-Object | Select-Object -First 1)
  }
  foreach ($inputPath in $InputPaths) {
    if (Test-Path -LiteralPath $inputPath -PathType Leaf) {
      if ((Get-Item -LiteralPath $inputPath).LastWriteTimeUtc -gt $referenceTime) {
        return $false
      }
      continue
    }
    if (-not (Test-Path -LiteralPath $inputPath -PathType Container)) {
      continue
    }
    $newerInput = Get-ChildItem -LiteralPath $inputPath -Recurse -File |
      Where-Object {
        $_.FullName -notmatch '[\\/](bin|obj|node_modules|\.venv[^\\/]*|__pycache__)[\\/]' -and
        ($Extensions.Count -eq 0 -or $_.Extension -in $Extensions) -and
        $_.LastWriteTimeUtc -gt $referenceTime
      } |
      Select-Object -First 1
    if ($null -ne $newerInput) { return $false }
  }
  return $true
}
function Set-BuildStamp([string]$StampPath) {
  New-Item -ItemType Directory -Force -Path (Split-Path -Parent $StampPath) | Out-Null
  [IO.File]::WriteAllText($StampPath, [DateTime]::UtcNow.ToString("O"))
}
function Enable-LocalNetworkAccess {
  if ($null -eq (Get-Command Get-NetFirewallRule -ErrorAction SilentlyContinue)) {
    return
  }
  $privateProfile = Get-NetFirewallProfile -Name Private -ErrorAction SilentlyContinue
  if ($null -ne $privateProfile -and -not $privateProfile.Enabled) {
    return
  }
  $rule = Get-NetFirewallRule -DisplayName $firewallRuleName -ErrorAction SilentlyContinue |
    Where-Object { $_.Enabled -eq "True" -and $_.Action -eq "Allow" } |
    Select-Object -First 1
  if ($null -ne $rule) { return }
  $principal = [Security.Principal.WindowsPrincipal]::new(
    [Security.Principal.WindowsIdentity]::GetCurrent())
  if (-not $principal.IsInRole(
      [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Warning "Windows Firewall may block LAN access. Run quickstart once from an Administrator PowerShell to create the '$firewallRuleName' rule."
    return
  }
  New-NetFirewallRule `
    -DisplayName $firewallRuleName `
    -Direction Inbound `
    -Action Allow `
    -Protocol TCP `
    -LocalPort 5000 `
    -Profile Private `
    -RemoteAddress LocalSubnet | Out-Null
  Write-Output "Created Windows Firewall rule '$firewallRuleName' for TCP 5000 on private networks."
}
function Show-ListeningAddresses {
  Write-Output "Local: http://localhost:5000"
  if ($null -eq (Get-Command Get-NetIPAddress -ErrorAction SilentlyContinue)) {
    return
  }
  $addresses = @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
    Where-Object {
      $_.IPAddress -notlike "127.*" -and
      $_.IPAddress -notlike "169.254.*"
    } |
    Select-Object -ExpandProperty IPAddress -Unique)
  foreach ($address in $addresses) {
    Write-Output "LAN: http://${address}:5000"
  }
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
  $checks = @("git", "dotnet", "node", "npm", "go", "python", "ffmpeg")
  if ($Mode -eq "RenderMachine") { $checks += "ffprobe" }
  Write-Output "Component | Version/Path | Status"
  foreach ($name in $checks) {
    $command = Get-Command $name -ErrorAction SilentlyContinue
    $executable = if ($null -ne $command) { $command.Source } else { $null }
    if ($null -eq $executable -and $name -in @("ffmpeg", "ffprobe")) {
      $bundledExecutable = Join-Path $repoRoot "artifacts\ffmpeg\bin\$name.exe"
      if (Test-Path -LiteralPath $bundledExecutable -PathType Leaf) {
        $executable = $bundledExecutable
      }
    }
    if ($null -eq $executable) { Write-Output "$name | - | Missing"; continue }
    $versionArguments = @(switch ($name) {
      "go" { "version" }
      "ffmpeg" { "-version" }
      "ffprobe" { "-version" }
      default { "--version" }
    })
    $version = (& $executable @versionArguments 2>$null | Select-Object -First 1)
    Write-Output "$name | $version | Ready"
  }
  $drive = Get-PSDrive -Name ([IO.Path]::GetPathRoot($repoRoot).TrimEnd('\').TrimEnd(':')) -ErrorAction SilentlyContinue
  if ($null -ne $drive) { Write-Output "storage | $($drive.Free) bytes free | $(if ($drive.Free -gt 1GB) { 'Ready' } else { 'Low space' })" }
  if ($Mode -eq "RenderMachine") {
    $renderSettings = Join-Path $repoRoot "src\Cs2Highlight.RenderAgent\appsettings.local.json"
    if (Test-Path -LiteralPath $renderSettings -PathType Leaf) {
      & "$repoRoot\scripts\verify-environment.ps1" -SettingsPath $renderSettings
    } else {
      Write-Output "Render Agent settings | $renderSettings | Missing"
    }
  }
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

New-Item -ItemType Directory -Force -Path $stateRoot, $buildStateRoot, $logRoot | Out-Null
$existing = Get-State
if ($null -ne $existing -and $null -ne (Get-Process -Id ([int]$existing.Pid) -ErrorAction SilentlyContinue)) {
  Show-Status
  exit 0
}

if (-not (Test-Command "dotnet")) { throw ".NET SDK 8 or newer is required." }
$sdkLines = @(dotnet --list-sdks)
$sdkMajors = @($sdkLines | ForEach-Object {
  if ($_ -match '^(?<major>\d+)\.') { [int]$Matches["major"] }
})
if (-not ($sdkMajors | Where-Object { $_ -ge 8 })) {
  $installedSdks = if ($sdkLines.Count -gt 0) { [string]::Join(", ", $sdkLines) } else { "none" }
  throw ".NET SDK 8 or newer is required. Installed SDKs: $installedSdks"
}
$ffmpegBin = Join-Path $repoRoot "artifacts\ffmpeg\bin"
& "$repoRoot\scripts\install-ffmpeg.ps1"
if (-not (Test-Path -LiteralPath (Join-Path $ffmpegBin "ffmpeg.exe") -PathType Leaf) -or
    -not (Test-Path -LiteralPath (Join-Path $ffmpegBin "ffprobe.exe") -PathType Leaf)) {
  throw "Local FFmpeg installation failed."
}
$env:PATH = "$ffmpegBin;$env:PATH"
if ($Mode -eq "RenderMachine") {
  & "$repoRoot\scripts\install-hlae.ps1"
  if ($LASTEXITCODE -ne 0) { throw "Local HLAE installation failed." }
}
$localConfig = Join-Path $repoRoot "src\Cs2Highlight.Web\appsettings.local.json"
$localExample = Join-Path $repoRoot "appsettings.local.example.json"
if (-not (Test-Path -LiteralPath $localConfig) -and (Test-Path -LiteralPath $localExample)) {
  Copy-Item -LiteralPath $localExample -Destination $localConfig
  Write-Output "Created $localConfig from the local example."
}
if (Test-Path -LiteralPath (Join-Path $repoRoot "package-lock.json")) {
  $npmStamp = Join-Path $buildStateRoot "npm.stamp"
  $npmCurrent = Test-BuildCurrent `
    -StampPath $npmStamp `
    -OutputPaths @((Join-Path $repoRoot "node_modules\.bin\tailwindcss.cmd")) `
    -InputPaths @(
      (Join-Path $repoRoot "package.json"),
      (Join-Path $repoRoot "package-lock.json"))
  if (-not $npmCurrent) {
    if (-not (Test-Command "npm")) { throw "Node.js/npm is required for the frontend build." }
    npm ci --ignore-scripts
    if ($LASTEXITCODE -ne 0) { throw "npm ci failed." }
  } else {
    Write-Output "Node dependencies are current; skipping npm ci."
  }
  Set-BuildStamp $npmStamp

  $cssStamp = Join-Path $buildStateRoot "css.stamp"
  $cssCurrent = $npmCurrent -and (Test-BuildCurrent `
    -StampPath $cssStamp `
    -OutputPaths @((Join-Path $repoRoot "src\Cs2Highlight.Web\wwwroot\css\app.generated.css")) `
    -InputPaths @(
      (Join-Path $repoRoot "package.json"),
      (Join-Path $repoRoot "package-lock.json"),
      (Join-Path $repoRoot "src\Cs2Highlight.Web\Styles"),
      (Join-Path $repoRoot "src\Cs2Highlight.Web\Pages"),
      (Join-Path $repoRoot "src\Cs2Highlight.Web\wwwroot\js")) `
    -Extensions @(".css", ".cshtml", ".js"))
  if (-not $cssCurrent) {
    if (-not (Test-Command "npm")) { throw "Node.js/npm is required for the frontend build." }
    npm run css:build
    if ($LASTEXITCODE -ne 0) { throw "Frontend build failed." }
  } else {
    Write-Output "Frontend CSS is current; skipping CSS build."
  }
  Set-BuildStamp $cssStamp
}
$demoParserPath = Join-Path $repoRoot "artifacts\demo-parser\demo-parser.exe"
$demoParserStamp = Join-Path $buildStateRoot "demo-parser.stamp"
$demoParserCurrent = Test-BuildCurrent `
  -StampPath $demoParserStamp `
  -OutputPaths @($demoParserPath) `
  -InputPaths @(
    (Join-Path $repoRoot "scripts\build-demo-parser.ps1"),
    (Join-Path $repoRoot "tools\demo-parser")) `
  -Extensions @(".go", ".mod", ".sum", ".ps1")
if (-not $demoParserCurrent) {
  if (-not (Test-Command "go")) { throw "Go 1.24 or newer is required to build demo-parser.exe." }
  Write-Output "Building demo-parser.exe..."
  & "$repoRoot\scripts\build-demo-parser.ps1"
} else {
  Write-Output "demo-parser.exe is current; skipping build."
}
if (-not (Test-Path -LiteralPath $demoParserPath -PathType Leaf)) {
  throw "Demo parser build did not produce $demoParserPath."
}
Set-BuildStamp $demoParserStamp

$musicAnalyzerPath = Join-Path $repoRoot "artifacts\music-analyzer\music-analyzer.exe"
$musicAnalyzerStamp = Join-Path $buildStateRoot "music-analyzer.stamp"
$musicAnalyzerCurrent = Test-BuildCurrent `
  -StampPath $musicAnalyzerStamp `
  -OutputPaths @($musicAnalyzerPath) `
  -InputPaths @(
    (Join-Path $repoRoot "scripts\build-music-analyzer.ps1"),
    (Join-Path $repoRoot "tools\music-analyzer")) `
  -Extensions @(".py", ".txt", ".ps1")
if (-not $musicAnalyzerCurrent) {
  Write-Output "Building music-analyzer.exe..."
  & "$repoRoot\scripts\build-music-analyzer.ps1" -PythonVersion "3.11"
} else {
  Write-Output "music-analyzer.exe is current; skipping build."
}
if (-not (Test-Path -LiteralPath $musicAnalyzerPath -PathType Leaf)) {
  throw "Music analyzer build did not produce $musicAnalyzerPath."
}
Set-BuildStamp $musicAnalyzerStamp

$dotnetStamp = Join-Path $buildStateRoot "dotnet-release.stamp"
$dotnetCurrent = Test-BuildCurrent `
  -StampPath $dotnetStamp `
  -OutputPaths @(
    (Join-Path $repoRoot "src\Cs2Highlight.Web\bin\Release\net8.0\Cs2Highlight.Web.dll"),
    (Join-Path $repoRoot "src\Cs2Highlight.RenderAgent\bin\Release\net8.0\render-agent.dll"),
    (Join-Path $repoRoot "src\Cs2Highlight.Cli\bin\Release\net8.0\cs2-highlight.dll")) `
  -InputPaths @(
    (Join-Path $repoRoot "Cs2Highlight.RenderPoC.sln"),
    (Join-Path $repoRoot "src")) `
  -Extensions @(".cs", ".csproj", ".props", ".targets", ".json", ".cshtml")
if (-not $dotnetCurrent) {
  Write-Output "Restoring and building .NET executables in Release configuration..."
  dotnet restore "$repoRoot\Cs2Highlight.RenderPoC.sln"
  if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed." }
  dotnet build "$repoRoot\Cs2Highlight.RenderPoC.sln" -c Release --no-restore
  if ($LASTEXITCODE -ne 0) { throw "dotnet build failed." }
} else {
  Write-Output ".NET Release outputs are current; skipping restore and build."
}
Set-BuildStamp $dotnetStamp
if ($Mode -eq "RenderMachine") {
  $renderSettings = Join-Path $repoRoot "src\Cs2Highlight.RenderAgent\bin\Release\net8.0\appsettings.local.json"
  & "$repoRoot\scripts\verify-environment.ps1" -SettingsPath $renderSettings
  if ($LASTEXITCODE -ne 0) { throw "RenderMachine dependencies are not ready." }
}
if ($RunAcceptance) {
  if ($Mode -ne "RenderMachine") { throw "-RunAcceptance requires -Mode RenderMachine." }
  & "$repoRoot\scripts\run-acceptance.ps1"
  if ($LASTEXITCODE -ne 0) { throw "Acceptance run failed." }
  exit 0
}
$webLog = Join-Path $logRoot "web.log"
$webErrorLog = Join-Path $logRoot "web-error.log"
$webEnvironment = if ($Mode -eq "Development") { "Development" } else { "Production" }
$webArgs = @(
  "run",
  "--project", "$repoRoot\src\Cs2Highlight.Web\Cs2Highlight.Web.csproj",
  "--configuration", "Release",
  "--no-build",
  "--no-restore",
  "--",
  "--urls", $listenUrl)
$previousAspNetCoreEnvironment = $env:ASPNETCORE_ENVIRONMENT
Enable-LocalNetworkAccess
try {
  $env:ASPNETCORE_ENVIRONMENT = $webEnvironment
  $web = Start-Process dotnet -ArgumentList $webArgs -WorkingDirectory $repoRoot -RedirectStandardOutput $webLog -RedirectStandardError $webErrorLog -PassThru -WindowStyle Hidden
}
finally {
  if ($null -eq $previousAspNetCoreEnvironment) {
    Remove-Item Env:\ASPNETCORE_ENVIRONMENT -ErrorAction SilentlyContinue
  } else {
    $env:ASPNETCORE_ENVIRONMENT = $previousAspNetCoreEnvironment
  }
}
Write-State $web.Id $Mode
Start-Sleep -Seconds 2
try {
  $health = Invoke-WebRequest $healthUrl -TimeoutSec 10 -UseBasicParsing
  if ($health.StatusCode -ne 200) { throw "Health check returned $($health.StatusCode)." }
} catch {
  $startupError = $_
  $web.Refresh()
  if ($web.HasExited) {
    Write-Warning "CSHighlighter exited during startup with code $($web.ExitCode)."
    if (Test-Path -LiteralPath $webErrorLog) {
      Get-Content -LiteralPath $webErrorLog -Tail 80 | ForEach-Object { Write-Warning $_ }
    }
    if (Test-Path -LiteralPath $webLog) {
      Get-Content -LiteralPath $webLog -Tail 80 | ForEach-Object { Write-Warning $_ }
    }
  }
  Stop-Project
  throw $startupError
}
Write-Output "CSHighlighter is ready."
Show-ListeningAddresses
Write-Output "Use .\quickstart.ps1 -Status or -Stop to manage it."
