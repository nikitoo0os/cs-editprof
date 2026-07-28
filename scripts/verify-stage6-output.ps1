[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $GenerationRoot,

    [string] $FfprobePath = "ffprobe.exe",

    [double] $MaximumAlignmentErrorMilliseconds = 50
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$root = [System.IO.Path]::GetFullPath($GenerationRoot)
if (-not (Test-Path -LiteralPath $root -PathType Container)) {
    throw "Generation root does not exist: $root"
}

$required = @{
    FinalVideo = Join-Path $root "output/final-highlights.mp4"
    MusicAnalysis = Join-Path $root "analysis/music/music-analysis.json"
    MusicEditPlan = Join-Path $root "plan/music-edit-plan.json"
    AudioMix = Join-Path $root "output/audio-mix-result.json"
    Alignment = Join-Path $root "output/music-alignment-result.json"
    ColorGrade = Join-Path $root "output/color-grade-result.json"
    Compilation = Join-Path $root "output/compilation-result.json"
}
foreach ($entry in $required.GetEnumerator()) {
    if (-not (Test-Path -LiteralPath $entry.Value -PathType Leaf)) {
        throw "Missing Stage 6 artifact '$($entry.Key)': $($entry.Value)"
    }
}

$probeText = & $FfprobePath -v error `
    -show_entries "format=duration,size:stream=codec_type,width,height,sample_rate" `
    -of json $required.FinalVideo
if ($LASTEXITCODE -ne 0) {
    throw "FFprobe rejected final video with exit code $LASTEXITCODE."
}
$probe = $probeText | ConvertFrom-Json
$video = @($probe.streams | Where-Object codec_type -eq "video")
$audio = @($probe.streams | Where-Object codec_type -eq "audio")
if ($video.Count -ne 1 -or $audio.Count -lt 1) {
    throw "Final MP4 must contain exactly one video stream and at least one audio stream."
}
if ([double]$probe.format.duration -le 0 -or [long]$probe.format.size -le 1024) {
    throw "Final MP4 duration or size is invalid."
}

$qualityReports = @(Get-ChildItem -LiteralPath (Join-Path $root "rendered-clips") `
    -Filter "clip-artifact-quality.json" -File -Recurse -ErrorAction SilentlyContinue)
if ($qualityReports.Count -eq 0) {
    throw "No clip-artifact-quality.json reports were found."
}
foreach ($reportFile in $qualityReports) {
    $quality = Get-Content -LiteralPath $reportFile.FullName -Raw | ConvertFrom-Json
    if (-not $quality.success) {
        throw "Clip quality gate failed: $($reportFile.FullName) - $($quality.error)"
    }
}

$alignment = Get-Content -LiteralPath $required.Alignment -Raw | ConvertFrom-Json
if ([double]$alignment.maximumAlignmentErrorMilliseconds -gt `
    $MaximumAlignmentErrorMilliseconds) {
    throw "Music alignment error $($alignment.maximumAlignmentErrorMilliseconds) ms exceeds $MaximumAlignmentErrorMilliseconds ms."
}

$audioMix = Get-Content -LiteralPath $required.AudioMix -Raw | ConvertFrom-Json
if ($null -eq $audioMix.measuredIntegratedLoudnessLufs) {
    throw "Integrated loudness was not measured."
}
if ($null -ne $audioMix.measuredTruePeakDb -and [double]$audioMix.measuredTruePeakDb -gt -0.5) {
    throw "Measured true peak is too high: $($audioMix.measuredTruePeakDb) dBFS."
}

[pscustomobject]@{
    Success = $true
    GenerationRoot = $root
    DurationSeconds = [double]$probe.format.duration
    SizeBytes = [long]$probe.format.size
    Width = [int]$video[0].width
    Height = [int]$video[0].height
    ClipQualityReports = $qualityReports.Count
    MaximumAlignmentErrorMilliseconds =
        [double]$alignment.maximumAlignmentErrorMilliseconds
    IntegratedLoudnessLufs =
        [double]$audioMix.measuredIntegratedLoudnessLufs
    TruePeakDb = $audioMix.measuredTruePeakDb
}
