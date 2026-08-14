using System.Diagnostics;
using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using Cs2Highlight.Music;
using Cs2Highlight.RenderAgent.Infrastructure;
using Cs2Highlight.Web.Domain;

namespace Cs2Highlight.Web.Services;

public sealed record CompilationRequest(
    IReadOnlyList<string> ClipPaths,
    string OutputDirectory,
    int Width,
    int Height,
    int Fps,
    int MinimumOutputBytes = 1024,
    IReadOnlyList<HighlightEffectPlan?>? EffectPlans = null,
    MusicEditPlan? MusicEditPlan = null,
    string? MusicPath = null,
    GenerationMovieSettings? MovieSettings = null,
    IReadOnlyList<DynamicEffectPlan?>? DynamicEffectPlans = null,
    FfmpegCapabilities? FfmpegCapabilities = null,
    CinematicMoviePlan? CinematicMoviePlan = null,
    bool InputsArePreNormalized = false);
public sealed record CompilationProgress(int Percent, string Stage);

public static class FrameContinuityPolicy
{
    public const double MaximumAutoRepairDurationSeconds = 0.25;

    public static double TransitionBoundaryGuardSeconds(int fps) =>
        2d / Math.Max(1, fps);

    public static double MinimumBlackDefectDurationSeconds(int fps)
    {
        int safeFps = Math.Max(1, fps);
        return Math.Max(0.05, 2.5 / safeFps);
    }

    public static bool IsBriefRepairableBlackDefect(
        double startSeconds,
        double endSeconds,
        double clipDurationSeconds,
        int fps)
    {
        double frameDuration = 1d / Math.Max(1, fps);
        double duration = endSeconds - startSeconds;
        return startSeconds >= frameDuration &&
            endSeconds <= clipDurationSeconds + frameDuration &&
            duration >= MinimumBlackDefectDurationSeconds(fps) &&
            duration <= MaximumAutoRepairDurationSeconds;
    }

    public static (int First, int Last, int Replacement) RepairFrameRange(
        double startSeconds,
        double endSeconds,
        int fps)
    {
        int safeFps = Math.Max(1, fps);
        int first = Math.Max(
            1,
            (int)Math.Round(
                startSeconds * safeFps,
                MidpointRounding.AwayFromZero));
        int last = Math.Max(first, (int)Math.Ceiling(endSeconds * safeFps) - 1);
        return (first, last, first - 1);
    }
}

public sealed record CompilationVideo(int Width, int Height, int Fps, string Codec);
public sealed record CompilationAudio(string? Codec, int? SampleRate);
public sealed record CompilationResult(
    string SchemaVersion,
    bool Success,
    string? OutputFile,
    int IncludedClips,
    int SkippedClips,
    long DurationMilliseconds,
    long FileSizeBytes,
    CompilationVideo? Video,
    CompilationAudio? Audio,
    string? Error);
public sealed record DynamicEffectClipResult(
    string ClipId,
    int PlannedEffects,
    int AppliedEffects,
    int FallbackEffects,
    int SkippedEffects,
    long RenderDurationMilliseconds,
    double OutputDurationSeconds,
    int FfmpegExitCode,
    bool Verified,
    IReadOnlyList<string> Warnings);

public interface IHighlightCompilationService
{
    Task<CompilationResult> ComposeAsync(
        CompilationRequest request,
        IProgress<CompilationProgress>? progress,
        CancellationToken cancellationToken);
}

public sealed record VideoOutputOptions(int Width, int Height, int Fps);
public sealed record FfmpegFilterGraph(string Video, string Audio);

public interface IEffectFilterGraphBuilder
{
    FfmpegFilterGraph Build(
        double durationSeconds,
        HighlightEffectPlan? plan,
        VideoOutputOptions output);
}

public sealed class FfmpegEffectFilterGraphBuilder : IEffectFilterGraphBuilder
{
    public FfmpegFilterGraph Build(
        double durationSeconds,
        HighlightEffectPlan? plan,
        VideoOutputOptions output) =>
        new(
            FfmpegEffectFilterBuilder.Build(
                output.Width,
                output.Height,
                output.Fps,
                durationSeconds,
                plan),
            FfmpegEffectFilterBuilder.BuildAudio(durationSeconds, plan));
}

public sealed partial class FfmpegHighlightCompilationService(
    PipelineOptions options,
    IEffectFilterGraphBuilder filterGraphs,
    IDynamicEffectFilterGraphBuilder dynamicFilterGraphs,
    TrustedLutCatalog trustedLuts,
    ILogger<FfmpegHighlightCompilationService> logger)
    : IHighlightCompilationService
{
    // AAC can add inter-sample overshoot after loudness normalization. Master
    // below the public -0.8 dBTP ceiling so the encoded artifact also passes.
    private const double AudioSafetyTruePeakDb = -2.5;
    private const string NormalizationPipelineVersion = "6";
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(false);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    [LoggerMessage(EventId = 5101, Level = LogLevel.Information, Message = "[Effects] Starting composition: {ClipCount} clips, {Width}x{Height}@{Fps}, dynamic plans: {DynamicPlanCount}")]
    private static partial void LogCompositionStarted(ILogger logger, int clipCount, int width, int height, int fps, int dynamicPlanCount);

    [LoggerMessage(EventId = 5102, Level = LogLevel.Information, Message = "[Effects] Clip {ClipNumber}/{ClipCount}: probe completed, duration {DurationSeconds:F3}s, planned effects {EffectCount}")]
    private static partial void LogClipProbed(ILogger logger, int clipNumber, int clipCount, double durationSeconds, int effectCount);

    [LoggerMessage(EventId = 5103, Level = LogLevel.Information, Message = "[Effects] Clip {ClipNumber}: graph ready, stages {Stages}, effects {Effects}")]
    private static partial void LogGraphReady(ILogger logger, int clipNumber, string stages, string effects);

    [LoggerMessage(EventId = 5104, Level = LogLevel.Information, Message = "[Effects] Clip {ClipNumber}: FFmpeg finished with exit code {ExitCode} in {ElapsedMilliseconds} ms")]
    private static partial void LogFfmpegCompleted(ILogger logger, int clipNumber, int exitCode, long elapsedMilliseconds);

    [LoggerMessage(EventId = 5105, Level = LogLevel.Warning, Message = "[Effects] Clip {ClipNumber}: render failed; see {LogPath}")]
    private static partial void LogRenderFailed(ILogger logger, int clipNumber, string logPath);

    [LoggerMessage(EventId = 5106, Level = LogLevel.Warning, Message = "[Effects] Clip {ClipNumber}: output validation failed ({Width}x{Height}, {DurationSeconds:F3}s)")]
    private static partial void LogValidationFailed(ILogger logger, int clipNumber, int? width, int? height, double durationSeconds);

    [LoggerMessage(EventId = 5107, Level = LogLevel.Information, Message = "[Effects] Clip {ClipNumber}: verified and persisted ({DurationSeconds:F3}s)")]
    private static partial void LogClipVerified(ILogger logger, int clipNumber, double durationSeconds);

    [LoggerMessage(EventId = 5108, Level = LogLevel.Information, Message = "[Effects] Final composition verified: {IncludedClips} clips, {DurationSeconds:F3}s, {FileSizeBytes} bytes")]
    private static partial void LogCompositionVerified(ILogger logger, int includedClips, double durationSeconds, long fileSizeBytes);

    [LoggerMessage(EventId = 5109, Level = LogLevel.Information, Message = "[Effects] Re-encoding {ClipCount} normalized clips into one timestamp-safe gameplay stream")]
    private static partial void LogDecodedConcat(ILogger logger, int clipCount);

    [LoggerMessage(EventId = 5110, Level = LogLevel.Information, Message = "[Effects] Decoding the complete final video to verify stream integrity")]
    private static partial void LogDecodeVerification(ILogger logger);

    public async Task<CompilationResult> ComposeAsync(
        CompilationRequest request,
        IProgress<CompilationProgress>? progress,
        CancellationToken cancellationToken)
    {
        Stopwatch watch = Stopwatch.StartNew();
        string outputDirectory = Path.GetFullPath(request.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);
        string normalizedDirectory = Path.Combine(outputDirectory, ".normalized");
        Directory.CreateDirectory(normalizedDirectory);
        bool enforceCinematicContract =
            request.CinematicMoviePlan is not null &&
            request.MovieSettings?.MovieStyle == MovieStyle.CinematicDirector &&
            !request.CinematicMoviePlan.Warnings.Contains(
                "CAMERA_ONLY_VARIANT",
                StringComparer.Ordinal);
        CinematicContractValidationReport? contractPlanReport = null;
        if (enforceCinematicContract)
        {
            contractPlanReport = CinematicContractPolicy.ValidatePlan(
                request.CinematicMoviePlan!,
                request.Fps);
            await File.WriteAllTextAsync(
                Path.Combine(outputDirectory, "cinematic-contract-plan-report.json"),
                JsonSerializer.Serialize(contractPlanReport, JsonOptions),
                cancellationToken);
            if (!contractPlanReport.IsValid)
            {
                return Failure(
                    "CINEMATIC_CONTRACT_PLAN_FAILED: " +
                    string.Join(',', contractPlanReport.Violations),
                    request.ClipPaths.Count,
                    request.ClipPaths.Count,
                    watch.ElapsedMilliseconds);
            }
        }
        string?[] normalizedSlots = new string?[request.ClipPaths.Count];
        ConcurrentBag<string> probeErrors = [];
        ConcurrentBag<DynamicEffectClipResult> effectResults = [];
        CinematicSequenceSegment[] cinematicSegments =
            request.CinematicMoviePlan?.Segments
                .OrderBy(value => value.OutputStartSeconds)
                .ThenBy(value => value.Id, StringComparer.Ordinal)
                .ToArray() ?? [];
        int skipped = 0;
        LogCompositionStarted(
            logger,
            request.ClipPaths.Count,
            request.Width,
            request.Height,
            request.Fps,
            request.DynamicEffectPlans?.Count ?? 0);
        await Parallel.ForEachAsync(
            Enumerable.Range(0, request.ClipPaths.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(
                    options.CompilationMaxParallelism,
                    1,
                    Math.Max(1, Environment.ProcessorCount / 2)),
                CancellationToken = cancellationToken
            },
            async (index, clipCancellationToken) =>
        {
            Stopwatch clipWatch = Stopwatch.StartNew();
            string input = Path.GetFullPath(request.ClipPaths[index]);
            if (!File.Exists(input) || new FileInfo(input).Length == 0)
            {
                Interlocked.Increment(ref skipped);
                return;
            }
            MediaMetadata metadata = await ProbeAsync(
                input,
                clipCancellationToken);
            if (metadata.Error is not null)
            {
                probeErrors.Add($"{Path.GetFileName(input)}: {metadata.Error}");
                Interlocked.Increment(ref skipped);
                return;
            }
            if (!metadata.HasVideo || metadata.DurationSeconds <= 0)
            {
                Interlocked.Increment(ref skipped);
                return;
            }
            progress?.Report(new CompilationProgress(
                5 + (int)(60d * index / Math.Max(1, request.ClipPaths.Count)),
                $"Normalizing clip {index + 1}/{request.ClipPaths.Count}"));
            string target = Path.Combine(normalizedDirectory, $"clip-{index + 1:D3}.mp4");
            string signaturePath = target + ".signature";
            string temporaryTarget = Path.Combine(
                normalizedDirectory,
                $"clip-{index + 1:D3}.tmp.mp4");
            if (File.Exists(temporaryTarget)) File.Delete(temporaryTarget);
            List<string> arguments = ["-y", "-hide_banner", "-loglevel", "error", "-i", input];
            if (!metadata.HasAudio)
            {
                arguments.AddRange(
                    ["-f", "lavfi", "-i", "anullsrc=channel_layout=stereo:sample_rate=48000"]);
            }
            HighlightEffectPlan? effectPlan =
                request.EffectPlans is not null && index < request.EffectPlans.Count
                    ? request.EffectPlans[index]
                    : null;
            DynamicEffectPlan? dynamicEffectPlan =
                request.DynamicEffectPlans is not null &&
                index < request.DynamicEffectPlans.Count
                    ? request.DynamicEffectPlans[index]
                    : null;
            LogClipProbed(
                logger,
                index + 1,
                request.ClipPaths.Count,
                metadata.DurationSeconds,
                dynamicEffectPlan?.Effects.Count ?? 0);
            if (request.InputsArePreNormalized)
            {
                if (metadata.Width != request.Width ||
                    metadata.Height != request.Height ||
                    !metadata.HasAudio)
                {
                    probeErrors.Add(
                        $"{Path.GetFileName(input)}: PRENORMALIZED_INPUT_INVALID");
                    Interlocked.Increment(ref skipped);
                    return;
                }
                normalizedSlots[index] = input;
                return;
            }
            FfmpegFilterGraph graph = filterGraphs.Build(
                metadata.DurationSeconds,
                effectPlan,
                new VideoOutputOptions(request.Width, request.Height, request.Fps));
            TimeWarpPlan? timeWarp = request.MusicEditPlan is not null &&
                index < request.MusicEditPlan.Segments.Count
                    ? request.MusicEditPlan.Segments[index].TimeWarp
                    : null;
            CinematicSequenceSegment? cinematicSegment =
                index < cinematicSegments.Length
                    ? cinematicSegments[index]
                    : null;
            if (cinematicSegment is not null)
            {
                timeWarp = cinematicSegment.TimeWarp;
            }
            double speed = timeWarp?.BaseSpeedFactor ?? 1;
            double expectedOutputDuration = cinematicSegment is not null
                ? cinematicSegment.OutputEndSeconds -
                  cinematicSegment.OutputStartSeconds
                : timeWarp is null
                    ? metadata.DurationSeconds
                    : TimeWarpMath.CoveredOutputDuration(
                        timeWarp,
                        metadata.DurationSeconds);
            string videoFilters = graph.Video;
            string audioFilters = graph.Audio;
            (string introVideoFade, string introAudioFade) =
                FfmpegMovieFilterBuilder.CinematicIntroTransition(
                    request.CinematicMoviePlan,
                    index,
                    metadata.DurationSeconds);
            videoFilters += introVideoFade;
            audioFilters += introAudioFade;
            string? selectedLutPath = null;
            List<string> postEffectVideoFilters = [];
            if (request.MovieSettings is not null)
            {
                string color = FfmpegMovieFilterBuilder.Color(
                    request.MovieSettings.ColorGradePreset);
                if (cinematicSegment is not null)
                {
                    ColorNarrativeSection? narrativeColor =
                        request.CinematicMoviePlan!.Color.Sections
                            .FirstOrDefault(value => string.Equals(
                                value.MusicSectionId,
                                cinematicSegment.MusicSectionId,
                                StringComparison.Ordinal));
                    if (narrativeColor is not null)
                    {
                        color += FormattableString.Invariant(
                            $",eq=contrast={narrativeColor.ContrastMultiplier:0.######}:brightness={narrativeColor.ExposureOffset:0.######}");
                    }
                }
                if (dynamicEffectPlan is null)
                    videoFilters += "," + color;
                else
                    postEffectVideoFilters.Add(color);
                selectedLutPath = trustedLuts.Resolve(request.MovieSettings.LutAssetKey);
                if (selectedLutPath is not null)
                {
                    string lut = FfmpegMovieFilterBuilder.Lut(selectedLutPath);
                    if (dynamicEffectPlan is null)
                        videoFilters += "," + lut;
                    else
                        postEffectVideoFilters.Add(lut);
                }
                if (request.MovieSettings.MovieStyle ==
                    MovieStyle.CinematicDirector)
                {
                    string framing =
                        FfmpegMovieFilterBuilder.CinematicFinish();
                    if (dynamicEffectPlan is null)
                        videoFilters += "," + framing;
                    else
                        postEffectVideoFilters.Add(framing);
                }
            }
            string lutFingerprint = selectedLutPath is null
                ? string.Empty
                : Convert.ToHexString(SHA256.HashData(
                    await File.ReadAllBytesAsync(
                        selectedLutPath,
                        clipCancellationToken)));
            string signature = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(string.Join(
                    '\n',
                    input,
                    NormalizationPipelineVersion,
                    new FileInfo(input).Length.ToString(CultureInfo.InvariantCulture),
                    File.GetLastWriteTimeUtc(input).Ticks.ToString(CultureInfo.InvariantCulture),
                    videoFilters,
                    audioFilters,
                    lutFingerprint,
                    JsonSerializer.Serialize(dynamicEffectPlan, JsonOptions),
                    JsonSerializer.Serialize(request.FfmpegCapabilities, JsonOptions),
                    JsonSerializer.Serialize(timeWarp, JsonOptions),
                    expectedOutputDuration.ToString(
                        "0.######",
                        CultureInfo.InvariantCulture))))).ToLowerInvariant();
            if (File.Exists(target) &&
                File.Exists(signaturePath) &&
                string.Equals(
                    await File.ReadAllTextAsync(
                        signaturePath,
                        clipCancellationToken),
                    signature,
                    StringComparison.Ordinal))
            {
                MediaMetadata persisted = await ProbeAsync(
                    target,
                    clipCancellationToken);
                if (persisted.Error is null &&
                    persisted.HasVideo &&
                    persisted.DurationSeconds > 0 &&
                    persisted.Width == request.Width &&
                    persisted.Height == request.Height)
                {
                    normalizedSlots[index] = target;
                    if (dynamicEffectPlan is not null)
                    {
                        effectResults.Add(EffectResult(
                            dynamicEffectPlan,
                            0,
                            persisted.DurationSeconds,
                            0,
                            true));
                    }
                    return;
                }
            }
            if (File.Exists(target)) File.Delete(target);
            if (File.Exists(signaturePath)) File.Delete(signaturePath);
            if (dynamicEffectPlan is not null)
            {
                string audioInput = metadata.HasAudio ? "0:a:0" : "1:a:0";
                DynamicFfmpegFilterGraph dynamicGraph = dynamicFilterGraphs.Build(
                    "0:v:0",
                    audioInput,
                    metadata.DurationSeconds,
                    dynamicEffectPlan,
                    timeWarp,
                    new VideoOutputOptions(
                        request.Width,
                        request.Height,
                        request.Fps),
                    audioFilters,
                    postEffectVideoFilters,
                    expectedOutputDuration);
                if (logger.IsEnabled(LogLevel.Information))
                {
                    string stages = string.Join(
                        " -> ",
                        dynamicGraph.Fragments
                            .Select(value => value.Stage)
                            .Distinct());
                    string effects = string.Join(
                        ", ",
                        dynamicEffectPlan.Effects.Select(value => value.Type));
                    LogGraphReady(
                        logger,
                        index + 1,
                        stages,
                        effects);
                }
                arguments.AddRange(
                [
                    "-filter_complex", dynamicGraph.FilterComplex,
                    "-map", $"[{dynamicGraph.VideoOutputLabel}]",
                    "-map", $"[{dynamicGraph.AudioOutputLabel}]"
                ]);
            }
            else if (timeWarp?.UsesLocalRamp == true && timeWarp.Segments.Count > 1)
            {
                string timeWarpGraph = FfmpegMovieFilterBuilder.TimeWarp(
                    videoFilters,
                    audioFilters,
                    metadata.HasAudio ? "0:a:0" : "1:a:0",
                    timeWarp);
                timeWarpGraph += FfmpegMovieFilterBuilder.DurationLock(
                    "warped_video",
                    "warped_audio",
                    "locked_video",
                    "locked_audio",
                    expectedOutputDuration,
                    request.Fps);
                arguments.AddRange(
                [
                    "-filter_complex", timeWarpGraph,
                    "-map", "[locked_video]",
                    "-map", "[locked_audio]"
                ]);
            }
            else
            {
                if (Math.Abs(speed - 1) > 0.0001)
                {
                    videoFilters += FormattableString.Invariant($",setpts=PTS/{speed:0.######}");
                    audioFilters += FormattableString.Invariant($",atempo={speed:0.######}");
                }
                if (cinematicSegment is not null || timeWarp is not null)
                {
                    videoFilters += FfmpegMovieFilterBuilder.DurationLockVideo(
                        expectedOutputDuration,
                        request.Fps);
                    audioFilters += FfmpegMovieFilterBuilder.DurationLockAudio(
                        expectedOutputDuration);
                }
                arguments.AddRange(
                [
                    "-map", "0:v:0",
                    "-map", metadata.HasAudio ? "0:a:0" : "1:a:0",
                    "-vf", videoFilters,
                    "-af", audioFilters
                ]);
            }
            arguments.AddRange(
            [
                "-c:v", "libx264", "-preset", "fast", "-crf", "18",
                "-c:a", "aac", "-ar", "48000", "-ac", "2", "-b:a", "192k",
                "-shortest", "-movflags", "+faststart",
                "-t", expectedOutputDuration.ToString(
                    "0.######",
                    CultureInfo.InvariantCulture),
                temporaryTarget
            ]);
            ProcessResult normalization = await RunAsync(
                options.FfmpegPath,
                arguments,
                clipCancellationToken);
            LogFfmpegCompleted(
                logger,
                index + 1,
                normalization.ExitCode,
                clipWatch.ElapsedMilliseconds);
            await WriteProcessLogAsync(
                Path.Combine(normalizedDirectory, $"clip-{index + 1:D3}.ffmpeg.log"),
                normalization,
                clipCancellationToken);
            if (normalization.ExitCode != 0 || !File.Exists(temporaryTarget))
            {
                if (dynamicEffectPlan is not null)
                {
                    effectResults.Add(EffectResult(
                        dynamicEffectPlan,
                        clipWatch.ElapsedMilliseconds,
                        0,
                        normalization.ExitCode,
                        false,
                        ["EFFECT_RENDER_FAILED"]));
                }
                if (File.Exists(temporaryTarget)) File.Delete(temporaryTarget);
                LogRenderFailed(
                    logger,
                    index + 1,
                    Path.Combine(normalizedDirectory, $"clip-{index + 1:D3}.ffmpeg.log"));
                Interlocked.Increment(ref skipped);
                return;
            }
            MediaMetadata normalizedMetadata =
                await ProbeAsync(temporaryTarget, clipCancellationToken);
            if (normalizedMetadata.Error is not null ||
                !normalizedMetadata.HasVideo ||
                normalizedMetadata.DurationSeconds <= 0 ||
                normalizedMetadata.Width != request.Width ||
                normalizedMetadata.Height != request.Height)
            {
                if (dynamicEffectPlan is not null)
                {
                    effectResults.Add(EffectResult(
                        dynamicEffectPlan,
                        clipWatch.ElapsedMilliseconds,
                        normalizedMetadata.DurationSeconds,
                        normalization.ExitCode,
                        false,
                        ["EFFECT_OUTPUT_INVALID"]));
                }
                File.Delete(temporaryTarget);
                LogValidationFailed(
                    logger,
                    index + 1,
                    normalizedMetadata.Width,
                    normalizedMetadata.Height,
                    normalizedMetadata.DurationSeconds);
                Interlocked.Increment(ref skipped);
                return;
            }
            File.Move(temporaryTarget, target, true);
            await File.WriteAllTextAsync(
                signaturePath,
                signature,
                Utf8WithoutBom,
                clipCancellationToken);
            normalizedSlots[index] = target;
            if (dynamicEffectPlan is not null)
            {
                effectResults.Add(EffectResult(
                    dynamicEffectPlan,
                    clipWatch.ElapsedMilliseconds,
                    normalizedMetadata.DurationSeconds,
                    normalization.ExitCode,
                    true));
            }
            LogClipVerified(
                logger,
                index + 1,
                normalizedMetadata.DurationSeconds);
        });
        List<string> normalized = normalizedSlots
            .Where(value => value is not null)
            .Select(value => value!)
            .ToList();
        DynamicEffectClipResult[] orderedEffectResults = effectResults
            .OrderBy(value => value.ClipId, StringComparer.Ordinal)
            .ToArray();
        if (request.DynamicEffectPlans is not null)
        {
            await File.WriteAllTextAsync(
                Path.Combine(outputDirectory, "dynamic-effect-result.json"),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = "1.0",
                    clips = orderedEffectResults,
                    plannedEffects = orderedEffectResults.Sum(value => value.PlannedEffects),
                    appliedEffects = orderedEffectResults.Sum(value => value.AppliedEffects),
                    fallbackEffects = orderedEffectResults.Sum(value => value.FallbackEffects),
                    skippedEffects = orderedEffectResults.Sum(value => value.SkippedEffects),
                    verified = orderedEffectResults.Length > 0 &&
                        orderedEffectResults.All(value => value.Verified)
                }, JsonOptions),
                cancellationToken);
        }
        if (enforceCinematicContract)
        {
            int plannedEffectClips = request.DynamicEffectPlans?
                .Count(value => value is not null) ?? 0;
            bool effectsVerified =
                plannedEffectClips == orderedEffectResults.Length &&
                orderedEffectResults.All(value => value.Verified);
            if (!effectsVerified)
            {
                await File.WriteAllTextAsync(
                    Path.Combine(outputDirectory, "cinematic-contract-render-report.json"),
                    JsonSerializer.Serialize(new
                    {
                        schemaVersion = "1.0",
                        contractVersion = CinematicContractPolicy.ContractVersion,
                        passed = false,
                        plan = contractPlanReport,
                        effectsVerified,
                        plannedEffectClips,
                        renderedEffectClips = orderedEffectResults.Length,
                        effectResults = orderedEffectResults
                    }, JsonOptions),
                    cancellationToken);
                return Failure(
                    "CINEMATIC_CONTRACT_EFFECT_RENDER_FAILED",
                    request.ClipPaths.Count,
                    skipped,
                    watch.ElapsedMilliseconds);
            }
        }
        if (normalized.Count == 0)
            return Failure(
                !probeErrors.IsEmpty
                    ? $"CLIP_PROBE_FAILED: {string.Join(" | ", probeErrors)}"
                    : "NO_CLIPS_RENDERED",
                request.ClipPaths.Count,
                skipped,
                watch.ElapsedMilliseconds);

        List<BriefBlackFrameRepair> blackFrameRepairs = [];
        for (int index = 0;
             !request.InputsArePreNormalized && index < normalized.Count;
             index++)
        {
            BriefBlackFrameRepair repair = await RepairBriefBlackFramesAsync(
                normalized[index],
                index + 1,
                request.Fps,
                cancellationToken);
            blackFrameRepairs.Add(repair);
        }
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "brief-black-frame-repair-report.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = "1.0",
                clipsScanned = blackFrameRepairs.Count,
                clipsRepaired = blackFrameRepairs.Count(value => value.Repaired),
                repairedIntervals = blackFrameRepairs.Sum(value => value.RepairedIntervals),
                failedRepairs = blackFrameRepairs.Count(value => value.Error is not null),
                clips = blackFrameRepairs.Where(value =>
                    value.DetectedIntervals > 0 || value.Error is not null).ToArray()
            }, JsonOptions),
            cancellationToken);

        progress?.Report(new CompilationProgress(75, "Composing gameplay timeline"));
        string temporary = Path.Combine(outputDirectory, "final-highlights.tmp.mp4");
        string final = Path.Combine(outputDirectory, "final-highlights.mp4");
        string gameplay = Path.Combine(normalizedDirectory, "gameplay-timeline.mp4");
        if (File.Exists(temporary)) File.Delete(temporary);
        if (File.Exists(gameplay)) File.Delete(gameplay);
        LogDecodedConcat(logger, normalized.Count);
        List<string> concatArguments =
        [
            "-y", "-hide_banner", "-loglevel", "error"
        ];
        foreach (string clip in normalized)
        {
            concatArguments.Add("-i");
            concatArguments.Add(clip);
        }
        string concatGraph = BuildDecodedConcatGraph(
            normalized.Count,
            request.Fps);
        concatArguments.AddRange(
        [
            "-filter_complex", concatGraph,
            "-map", "[gameplay_video]",
            "-map", "[gameplay_audio]",
            "-c:v", "libx264", "-preset", "fast", "-crf", "18",
            "-pix_fmt", "yuv420p", "-r",
            request.Fps.ToString(CultureInfo.InvariantCulture),
            "-c:a", "aac", "-ar", "48000", "-ac", "2", "-b:a", "192k",
            "-movflags", "+faststart",
            gameplay
        ]);
        ProcessResult concatResult = await RunAsync(
            options.FfmpegPath,
            concatArguments,
            cancellationToken);
        await WriteProcessLogAsync(
            Path.Combine(outputDirectory, "composition.ffmpeg.log"),
            concatResult,
            cancellationToken);
        if (concatResult.ExitCode != 0)
        {
            if (File.Exists(gameplay)) File.Delete(gameplay);
            return Failure($"COMPILATION_FAILED: {concatResult.Error}", request.ClipPaths.Count, skipped, watch.ElapsedMilliseconds);
        }
        ProcessResult composition;
        if (request.MusicPath is not null && request.MovieSettings is not null)
        {
            progress?.Report(new CompilationProgress(85, "Mixing music and gameplay audio"));
            AudioMixOptions mixOptions = new()
            {
                MusicGainDb = request.MovieSettings.MusicGainDb,
                GameplayBaseGainDb = request.MovieSettings.GameplayGainDb,
                GameplayKillAccentGainDb =
                    request.MovieSettings.SyncIntensity switch
                    {
                        MusicSyncIntensity.Soft => -9,
                        MusicSyncIntensity.Aggressive => -5,
                        _ => -7
                    },
                KillAccentAttackMilliseconds = 120,
                KillAccentHoldMilliseconds = 110,
                KillAccentReleaseMilliseconds = 280,
                OutputTruePeakDb = AudioSafetyTruePeakDb
            };
            string mix = FfmpegMovieFilterBuilder.AudioMix(
                request.MovieSettings,
                request.MusicEditPlan,
                mixOptions,
                request.CinematicMoviePlan);
            List<string> mixArguments =
            [
                "-y", "-hide_banner", "-loglevel", "error",
                "-i", gameplay
            ];
            if (request.MusicEditPlan?.MusicStartSeconds > 0)
            {
                mixArguments.Add("-ss");
                mixArguments.Add(
                    request.MusicEditPlan.MusicStartSeconds.ToString(
                        "0.######",
                        CultureInfo.InvariantCulture));
            }
            mixArguments.AddRange(
            [
                "-i", request.MusicPath,
                "-filter_complex", mix,
                "-map", "0:v:0", "-map", "[mixed]",
                "-c:v", "copy", "-c:a", "aac", "-ar", "48000",
                "-ac", "2", "-b:a", "256k",
                "-shortest", "-movflags", "+faststart"
            ]);
            if (request.CinematicMoviePlan is not null)
            {
                mixArguments.Add("-t");
                mixArguments.Add(
                    request.CinematicMoviePlan.TargetDurationSeconds.ToString(
                        "0.######",
                        CultureInfo.InvariantCulture));
            }
            mixArguments.Add(temporary);
            composition = await RunAsync(
                options.FfmpegPath,
                mixArguments,
                cancellationToken);
            await WriteProcessLogAsync(
                Path.Combine(outputDirectory, "ffmpeg-mix.log"),
                composition,
                cancellationToken);
        }
        else
        {
            composition = await RunAsync(
                options.FfmpegPath,
                ["-y", "-hide_banner", "-loglevel", "error",
                 "-i", gameplay, "-c", "copy", "-movflags", "+faststart", temporary],
                cancellationToken);
        }
        if (composition.ExitCode != 0)
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            return Failure($"COMPILATION_FAILED: {composition.Error}", request.ClipPaths.Count, skipped, watch.ElapsedMilliseconds);
        }
        progress?.Report(new CompilationProgress(95, "Verifying final video"));
        MediaMetadata finalMetadata = await ProbeAsync(temporary, cancellationToken);
        FileInfo file = new(temporary);
        if (!finalMetadata.HasVideo || finalMetadata.DurationSeconds <= 0 ||
            finalMetadata.Width != request.Width || finalMetadata.Height != request.Height ||
            file.Length < request.MinimumOutputBytes)
            return Failure("FINAL_VIDEO_INVALID", request.ClipPaths.Count, skipped, watch.ElapsedMilliseconds);
        if (enforceCinematicContract)
        {
            double durationTolerance = Math.Max(0.05, 2d / Math.Max(1, request.Fps));
            if (Math.Abs(
                    finalMetadata.DurationSeconds -
                    request.CinematicMoviePlan!.TargetDurationSeconds) >
                durationTolerance)
            {
                await File.WriteAllTextAsync(
                    Path.Combine(outputDirectory, "cinematic-contract-render-report.json"),
                    JsonSerializer.Serialize(new
                    {
                        schemaVersion = "1.0",
                        contractVersion = CinematicContractPolicy.ContractVersion,
                        passed = false,
                        violation = "CINEMATIC_DURATION_RENDER_MISMATCH",
                        plannedDurationSeconds = request.CinematicMoviePlan.TargetDurationSeconds,
                        renderedDurationSeconds = finalMetadata.DurationSeconds,
                        toleranceSeconds = durationTolerance,
                        plan = contractPlanReport
                    }, JsonOptions),
                    cancellationToken);
                return Failure(
                    "CINEMATIC_DURATION_RENDER_MISMATCH",
                    request.ClipPaths.Count,
                    skipped,
                    watch.ElapsedMilliseconds);
            }
        }
        LogDecodeVerification(logger);
        ProcessResult decodeVerification = await RunAsync(
            options.FfmpegPath,
            [
                "-hide_banner", "-loglevel", "error", "-xerror",
                "-i", temporary,
                "-map", "0:v:0",
                "-f", "null", "-"
            ],
            cancellationToken);
        await WriteProcessLogAsync(
            Path.Combine(outputDirectory, "decode-verification.ffmpeg.log"),
            decodeVerification,
            cancellationToken);
        if (decodeVerification.ExitCode != 0)
        {
            File.Delete(temporary);
            return Failure(
                $"FINAL_VIDEO_DECODE_FAILED: {decodeVerification.Error}",
                request.ClipPaths.Count,
                skipped,
                watch.ElapsedMilliseconds);
        }
        FrameContinuityScan continuity = await AnalyzeFrameContinuityAsync(
            temporary,
            request,
            cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "frame-continuity-report.json"),
            JsonSerializer.Serialize(continuity, JsonOptions),
            cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "transition-boundary-report.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = "1.0",
                checkedFromRenderedMedia = true,
                continuity.TransitionBoundaryValid,
                continuity.BlackFrameCount,
                continuity.OneFrameSegmentCount,
                continuity.OneToFiveFrameSegmentCount,
                minimumTransitionShotDurationSeconds = 1.5,
                violations = continuity.Violations.Where(value =>
                    value.Contains(
                        "BOUNDARY",
                        StringComparison.Ordinal) ||
                    value.Contains(
                        "FRAME",
                        StringComparison.Ordinal)).ToArray()
            }, JsonOptions),
            cancellationToken);
        if (!continuity.IsValid)
        {
            File.Delete(temporary);
            return Failure(
                "FRAME_CONTINUITY_FAILED: " +
                string.Join(',', continuity.Violations),
                request.ClipPaths.Count,
                skipped,
                watch.ElapsedMilliseconds);
        }
        DemoUiDetectionReport demoUi = await DemoUiDetector.AnalyzeAsync(
            options.FfmpegPath,
            temporary,
            Math.Min(12, finalMetadata.DurationSeconds),
            cancellationToken);
        await DemoUiDetector.WriteAsync(
            Path.Combine(outputDirectory, "demo-ui-detection-report.json"),
            demoUi,
            cancellationToken);
        if (!demoUi.Analyzed || demoUi.DemoPlaybackStripDetected)
        {
            File.Delete(temporary);
            return Failure(
                demoUi.DemoPlaybackStripDetected
                    ? "DEMO_PLAYBACK_STRIP_DETECTED"
                    : $"DEMO_UI_DETECTION_FAILED: {demoUi.Error}",
                request.ClipPaths.Count,
                skipped,
                watch.ElapsedMilliseconds);
        }
        LoudnessMeasurement? loudness = null;
        if ((request.MusicPath is not null && request.MovieSettings is not null) ||
            enforceCinematicContract)
        {
            loudness = await MeasureLoudnessAsync(temporary, cancellationToken);
            if (!loudness.Success)
            {
                File.Delete(temporary);
                return Failure(
                    $"AUDIO_LOUDNESS_ANALYSIS_FAILED: {loudness.Error}",
                    request.ClipPaths.Count,
                    skipped,
                    watch.ElapsedMilliseconds);
            }
        }
        IReadOnlyList<string> audioContractViolations = [];
        bool audioSafetyPassApplied = false;
        List<AudioSafetyPassAttempt> audioSafetyPassAttempts = [];
        if (enforceCinematicContract)
        {
            audioContractViolations = CinematicContractPolicy.ValidateAudio(
                loudness?.IntegratedLoudnessLufs ?? double.NaN,
                loudness?.LoudnessRangeLu ?? double.NaN,
                loudness?.TruePeakDb ?? double.NaN);
            static bool NeedsAudioCorrection(IReadOnlyList<string> violations) =>
                violations.Contains(
                    "AUDIO_INTEGRATED_LOUDNESS_OUT_OF_RANGE",
                    StringComparer.Ordinal) ||
                violations.Contains(
                    "AUDIO_TRUE_PEAK_EXCEEDED",
                    StringComparer.Ordinal);

            StringBuilder safetyLog = new();
            for (int attempt = 1;
                 attempt <= 3 && NeedsAudioCorrection(audioContractViolations);
                 attempt++)
            {
                double measuredLoudness =
                    loudness?.IntegratedLoudnessLufs ??
                    CinematicContractPolicy.TargetIntegratedLoudnessLufs;
                double measuredPeak = loudness?.TruePeakDb ??
                    CinematicContractPolicy.MaximumTruePeakDb;
                double requestedGain =
                    CinematicContractPolicy.TargetIntegratedLoudnessLufs -
                    measuredLoudness;
                double peakSafeGain =
                    AudioSafetyTruePeakDb - measuredPeak;
                double correctionDb = Math.Min(requestedGain, peakSafeGain);
                if (Math.Abs(correctionDb) < 0.05)
                    break;

                string corrected = temporary + $".audio-safe-{attempt}.mp4";
                if (File.Exists(corrected))
                    File.Delete(corrected);
                string loudnorm = string.Format(
                    CultureInfo.InvariantCulture,
                    "loudnorm=I={0:0.######}:LRA={1:0.######}:TP={2:0.######}:linear=false:print_format=summary," +
                    "alimiter=limit={3:0.######}:attack=5:release=50:level=false",
                    CinematicContractPolicy.TargetIntegratedLoudnessLufs,
                    CinematicContractPolicy.TargetLoudnessRangeLu,
                    AudioSafetyTruePeakDb,
                    Math.Pow(10, AudioSafetyTruePeakDb / 20));
                ProcessResult safetyPass = await RunAsync(
                    options.FfmpegPath,
                    [
                        "-y", "-hide_banner", "-loglevel", "error",
                        "-i", temporary,
                        "-map", "0:v:0",
                        "-map", "0:a:0",
                        "-af", loudnorm,
                        "-c:v", "copy",
                        "-c:a", "aac", "-ar", "48000", "-ac", "2",
                        "-b:a", "256k", "-t",
                        finalMetadata.DurationSeconds.ToString(
                            "0.######",
                            CultureInfo.InvariantCulture),
                        "-movflags", "+faststart",
                        corrected
                    ],
                    cancellationToken);
                safetyLog.AppendLine(CultureInfo.InvariantCulture, $"attempt={attempt}");
                safetyLog.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"beforeIntegrated={measuredLoudness.ToString("0.00", CultureInfo.InvariantCulture)}");
                safetyLog.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"beforeTruePeak={measuredPeak.ToString("0.00", CultureInfo.InvariantCulture)}");
                safetyLog.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"requestedGainDb={requestedGain.ToString("0.00", CultureInfo.InvariantCulture)}");
                safetyLog.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"peakSafeGainDb={peakSafeGain.ToString("0.00", CultureInfo.InvariantCulture)}");
                safetyLog.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"correctionDb={correctionDb.ToString("0.00", CultureInfo.InvariantCulture)}");
                safetyLog.AppendLine(CultureInfo.InvariantCulture,
                    $"exitCode={safetyPass.ExitCode}");
                safetyLog.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"stdout:{Environment.NewLine}{safetyPass.Output}");
                safetyLog.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"stderr:{Environment.NewLine}{safetyPass.Error}");
                if (safetyPass.ExitCode == 0 && File.Exists(corrected))
                {
                    File.Move(corrected, temporary, true);
                    loudness = await MeasureLoudnessAsync(
                        temporary,
                        cancellationToken);
                    audioContractViolations =
                        CinematicContractPolicy.ValidateAudio(
                            loudness.IntegratedLoudnessLufs ?? double.NaN,
                            loudness.LoudnessRangeLu ?? double.NaN,
                            loudness.TruePeakDb ?? double.NaN);
                    audioSafetyPassApplied = true;
                    audioSafetyPassAttempts.Add(new AudioSafetyPassAttempt(
                        attempt,
                        measuredLoudness,
                        measuredPeak,
                        requestedGain,
                        peakSafeGain,
                        correctionDb,
                        safetyPass.ExitCode,
                        loudness.IntegratedLoudnessLufs,
                        loudness.TruePeakDb,
                        audioContractViolations));
                }
                else
                {
                    if (File.Exists(corrected))
                        File.Delete(corrected);
                    audioSafetyPassAttempts.Add(new AudioSafetyPassAttempt(
                        attempt,
                        measuredLoudness,
                        measuredPeak,
                        requestedGain,
                        peakSafeGain,
                        correctionDb,
                        safetyPass.ExitCode,
                        null,
                        null,
                        audioContractViolations));
                    break;
                }
            }
            if (safetyLog.Length > 0)
                await File.WriteAllTextAsync(
                    Path.Combine(outputDirectory, "audio-safety-pass.ffmpeg.log"),
                    safetyLog.ToString(),
                    Utf8WithoutBom,
                    cancellationToken);
            bool passed = continuity.IsValid &&
                audioContractViolations.Count == 0;
            await File.WriteAllTextAsync(
                Path.Combine(outputDirectory, "cinematic-contract-render-report.json"),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = "1.0",
                    contractVersion = CinematicContractPolicy.ContractVersion,
                    passed,
                    plan = contractPlanReport,
                    finalDurationSeconds = finalMetadata.DurationSeconds,
                    audio = new
                    {
                        targetIntegratedLoudnessLufs = CinematicContractPolicy.TargetIntegratedLoudnessLufs,
                        targetLoudnessRangeLu = CinematicContractPolicy.TargetLoudnessRangeLu,
                        maximumTruePeakDb = CinematicContractPolicy.MaximumTruePeakDb,
                        measuredIntegratedLoudnessLufs = loudness?.IntegratedLoudnessLufs,
                        measuredLoudnessRangeLu = loudness?.LoudnessRangeLu,
                        measuredTruePeakDb = loudness?.TruePeakDb,
                        violations = audioContractViolations,
                        audioSafetyPassApplied,
                        audioSafetyPassAttempts,
                        startFadeSeconds = 0.30,
                        endFadeSeconds = 0.50
                    },
                    frameContinuity = continuity,
                    effectsVerified = request.DynamicEffectPlans is null ||
                        orderedEffectResults.All(value => value.Verified),
                    effectResults = orderedEffectResults
                }, JsonOptions),
                cancellationToken);
            if (audioContractViolations.Count > 0)
            {
                File.Delete(temporary);
                static string Metric(double? value, string unit) =>
                    value is null
                        ? "n/a"
                        : value.Value.ToString("0.00", CultureInfo.InvariantCulture) +
                          unit;
                return Failure(
                    "CINEMATIC_CONTRACT_AUDIO_FAILED: " +
                    string.Join(',', audioContractViolations) +
                    "; measured integrated=" +
                    Metric(loudness?.IntegratedLoudnessLufs, " LUFS") +
                    ", LRA=" + Metric(loudness?.LoudnessRangeLu, " LU") +
                    ", truePeak=" + Metric(loudness?.TruePeakDb, " dBTP") +
                    "; report=output/cinematic-contract-render-report.json",
                    request.ClipPaths.Count,
                    skipped,
                    watch.ElapsedMilliseconds);
            }
        }
        File.Move(temporary, final, true);
        LogCompositionVerified(
            logger,
            normalized.Count,
            finalMetadata.DurationSeconds,
            file.Length);
        if (request.MovieSettings is not null)
        {
            string? lutPath = trustedLuts.Resolve(request.MovieSettings.LutAssetKey);
            await File.WriteAllTextAsync(
                Path.Combine(outputDirectory, "color-grade-result.json"),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = "1.0",
                    preset = request.MovieSettings.ColorGradePreset,
                    filter = FfmpegMovieFilterBuilder.Color(
                        request.MovieSettings.ColorGradePreset),
                    styleFinish = request.MovieSettings.MovieStyle ==
                        MovieStyle.CinematicDirector
                            ? FfmpegMovieFilterBuilder.CinematicFinish()
                            : null,
                    lutAssetKey = request.MovieSettings.LutAssetKey,
                    lutSha256 = lutPath is null
                        ? null
                        : Convert.ToHexString(SHA256.HashData(
                            await File.ReadAllBytesAsync(lutPath, cancellationToken)))
                            .ToLowerInvariant(),
                    appliedConsistentlyToAllClips = true
                }, JsonOptions),
                cancellationToken);
        }
        if (request.MusicPath is not null && request.MovieSettings is not null)
        {
            double[] killTimes = request.MusicEditPlan?.Segments
                .Select(value => value.PrimaryKillOutputTimeSeconds)
                .Where(value => value >= 0)
                .OrderBy(value => value)
                .ToArray() ?? request.CinematicMoviePlan?.HighlightMatches
                .Select(value => value.PlannedKillSeconds)
                .Where(value => value >= 0)
                .OrderBy(value => value)
                .ToArray() ?? [];
            await File.WriteAllTextAsync(
                Path.Combine(outputDirectory, "audio-mix-result.json"),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = "2.0",
                    musicGainDb = request.MovieSettings.MusicGainDb,
                    gameplayGainDb = request.MovieSettings.GameplayGainDb,
                    killAccents = killTimes.Length,
                    musicDucking = false,
                    musicGainDeltaAroundKillsDb = killTimes.Select(value =>
                        new
                        {
                            killTimeSeconds = value,
                            windowStartSeconds = Math.Max(0, value - 0.5),
                            windowEndSeconds = Math.Min(
                                finalMetadata.DurationSeconds,
                                value + 0.5),
                            minimumGainDb = request.MovieSettings.MusicGainDb,
                            maximumGainDb = request.MovieSettings.MusicGainDb,
                            deltaDb = 0.0
                        }).ToArray(),
                    limiter = true,
                    targetIntegratedLoudnessLufs =
                        CinematicContractPolicy.TargetIntegratedLoudnessLufs,
                    targetLoudnessRangeLu =
                        CinematicContractPolicy.TargetLoudnessRangeLu,
                    targetTruePeakDb = CinematicContractPolicy.MaximumTruePeakDb,
                    measuredIntegratedLoudnessLufs =
                        loudness?.IntegratedLoudnessLufs,
                    measuredLoudnessRangeLu = loudness?.LoudnessRangeLu,
                    measuredTruePeakDb = loudness?.TruePeakDb
                }, JsonOptions),
                cancellationToken);
            double[] musicTimes = killTimes
                .SelectMany(value => new[]
                {
                    Math.Max(0, value - 0.5),
                    value,
                    Math.Min(finalMetadata.DurationSeconds, value + 0.5)
                })
                .Append(0)
                .Append(finalMetadata.DurationSeconds)
                .Distinct()
                .OrderBy(value => value)
                .ToArray();
            await File.WriteAllTextAsync(
                Path.Combine(outputDirectory, "music-gain-envelope.json"),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = "1.0",
                    source = "ffmpeg-filter-plan",
                    killLinkedGainAutomation = false,
                    points = musicTimes.Select(value => new
                    {
                        timeSeconds = value,
                        gainDb = request.MovieSettings.MusicGainDb
                    }).ToArray()
                }, JsonOptions),
                cancellationToken);
            double gameplayBase = request.MovieSettings.GameplayGainDb;
            double gameplayAccent = request.MovieSettings.SyncIntensity switch
            {
                MusicSyncIntensity.Soft => -9,
                MusicSyncIntensity.Aggressive => -5,
                _ => -7
            };
            await File.WriteAllTextAsync(
                Path.Combine(outputDirectory, "gameplay-audio-envelope.json"),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = "1.0",
                    source = "ffmpeg-filter-plan",
                    transientShaping = true,
                    parallelCompression = true,
                    truePeakLimiter = true,
                    points = killTimes.SelectMany(value => new[]
                    {
                        new
                        {
                            timeSeconds = Math.Max(0, value - 0.12),
                            gainDb = gameplayBase,
                            role = "attack-start"
                        },
                        new
                        {
                            timeSeconds = value,
                            gainDb = gameplayAccent,
                            role = "transient-accent"
                        },
                        new
                        {
                            timeSeconds = Math.Min(
                                finalMetadata.DurationSeconds,
                                value + 0.28),
                            gainDb = gameplayBase,
                            role = "release-end"
                        }
                    }).ToArray()
                }, JsonOptions),
                cancellationToken);
        }
        if (request.MusicEditPlan is not null)
        {
            var alignment = request.MusicEditPlan.Segments
                .Where(value => value.TargetMusicAnchor is not null)
                .Select(value =>
                {
                    double actual = Math.Round(
                        value.PrimaryKillOutputTimeSeconds * request.Fps,
                        MidpointRounding.AwayFromZero) / request.Fps;
                    double error = Math.Abs(
                        actual - value.TargetMusicAnchor!.TimeSeconds) * 1000;
                    return new
                    {
                        value.HighlightId,
                        plannedAnchorTime = value.TargetMusicAnchor.TimeSeconds,
                        actualKillOutputTime = actual,
                        alignmentErrorMilliseconds = error
                    };
                })
                .ToArray();
            await File.WriteAllTextAsync(
                Path.Combine(outputDirectory, "music-alignment-result.json"),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = "1.0",
                    measurement = "frame-timebase projection",
                    segments = alignment,
                    maximumAlignmentErrorMilliseconds =
                        alignment.Select(value => value.alignmentErrorMilliseconds)
                            .DefaultIfEmpty(0).Max(),
                    averageAlignmentErrorMilliseconds =
                        alignment.Select(value => value.alignmentErrorMilliseconds)
                            .DefaultIfEmpty(0).Average()
                }, JsonOptions),
                cancellationToken);
        }
        CompilationResult result = new(
            "1.1",
            true,
            final,
            normalized.Count,
            skipped,
            (long)Math.Round(
                finalMetadata.DurationSeconds * 1000,
                MidpointRounding.AwayFromZero),
            new FileInfo(final).Length,
            new CompilationVideo(request.Width, request.Height, request.Fps, finalMetadata.VideoCodec ?? "h264"),
            new CompilationAudio(finalMetadata.AudioCodec, finalMetadata.AudioSampleRate),
            null);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "compilation-result.json"),
            JsonSerializer.Serialize(result, JsonOptions),
            cancellationToken);
        progress?.Report(new CompilationProgress(100, "Completed"));
        return result;
    }

    private static string BuildDecodedConcatGraph(int clipCount, int fps)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(clipCount, 1);
        double boundaryGuardSeconds =
            FrameContinuityPolicy.TransitionBoundaryGuardSeconds(fps);
        string boundaryGuard = boundaryGuardSeconds.ToString(
            "0.######",
            CultureInfo.InvariantCulture);
        StringBuilder graph = new();
        for (int index = 0; index < clipCount; index++)
        {
            graph.Append('[').Append(index)
                .Append(":v:0]");
            if (index > 0)
            {
                // HLAE/CS2 can emit one or two stale frames immediately after
                // a seek or camera-mode switch. Never expose those frames at a
                // visible edit boundary; replace their time with the last valid
                // frame so the planned timeline duration remains unchanged.
                graph.Append("trim=start=").Append(boundaryGuard)
                    .Append(",setpts=PTS-STARTPTS,tpad=stop_mode=clone:stop_duration=")
                    .Append(boundaryGuard).Append(',');
            }
            graph.Append("settb=AVTB,setpts=PTS-STARTPTS[v")
                .Append(index).Append("];[")
                .Append(index)
                .Append(":a:0]");
            if (index > 0)
            {
                graph.Append("atrim=start=").Append(boundaryGuard)
                    .Append(",asetpts=PTS-STARTPTS,apad=pad_dur=")
                    .Append(boundaryGuard).Append(',');
            }
            graph.Append("asetpts=PTS-STARTPTS,")
                .Append("aresample=48000:async=1:first_pts=0[a")
                .Append(index).Append("];");
        }
        for (int index = 0; index < clipCount; index++)
        {
            graph.Append("[v").Append(index).Append("][a")
                .Append(index).Append(']');
        }
        graph.Append("concat=n=").Append(clipCount)
            .Append(":v=1:a=1[gameplay_video][gameplay_audio]");
        return graph.ToString();
    }

    private static DynamicEffectClipResult EffectResult(
        DynamicEffectPlan plan,
        long renderDurationMilliseconds,
        double outputDurationSeconds,
        int ffmpegExitCode,
        bool verified,
        IReadOnlyList<string>? additionalWarnings = null)
    {
        string[] warnings = plan.Warnings
            .Concat(additionalWarnings ?? [])
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        int fallback = warnings.Count(value =>
            value.Contains("FALLBACK", StringComparison.Ordinal));
        return new DynamicEffectClipResult(
            plan.ClipId,
            plan.Effects.Count,
            verified ? plan.Effects.Count : 0,
            fallback,
            plan.RejectedEffects.Count,
            renderDurationMilliseconds,
            outputDurationSeconds,
            ffmpegExitCode,
            verified,
            warnings);
    }

    private async Task<MediaMetadata> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        ProcessResult result = await RunAsync(
            options.FfprobePath,
            ["-v", "error", "-show_entries",
             "format=duration:stream=codec_type,codec_name,width,height,sample_rate",
             "-of", "json", path],
            cancellationToken);
        if (result.ExitCode != 0)
            return new MediaMetadata
            {
                Error = string.IsNullOrWhiteSpace(result.Error)
                    ? $"FFprobe exited with code {result.ExitCode}."
                    : result.Error.Trim()
            };
        using JsonDocument document = JsonDocument.Parse(result.Output);
        JsonElement[] streams = document.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        JsonElement? video = streams.Cast<JsonElement?>().FirstOrDefault(value =>
            value!.Value.GetProperty("codec_type").GetString() == "video");
        JsonElement? audio = streams.Cast<JsonElement?>().FirstOrDefault(value =>
            value!.Value.GetProperty("codec_type").GetString() == "audio");
        double.TryParse(
            document.RootElement.GetProperty("format").GetProperty("duration").GetString(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double duration);
        int? sampleRate = null;
        if (audio?.TryGetProperty("sample_rate", out JsonElement rate) == true &&
            int.TryParse(rate.GetString(), CultureInfo.InvariantCulture, out int parsedRate))
            sampleRate = parsedRate;
        return new MediaMetadata
        {
            HasVideo = video.HasValue,
            HasAudio = audio.HasValue,
            DurationSeconds = duration,
            Width = video?.GetProperty("width").GetInt32(),
            Height = video?.GetProperty("height").GetInt32(),
            VideoCodec = video?.GetProperty("codec_name").GetString(),
            AudioCodec = audio?.GetProperty("codec_name").GetString(),
            AudioSampleRate = sampleRate
        };
    }

    private async Task<LoudnessMeasurement> MeasureLoudnessAsync(
        string path,
        CancellationToken cancellationToken)
    {
        ProcessResult result = await RunAsync(
            options.FfmpegPath,
            ["-hide_banner", "-nostats", "-i", path, "-filter_complex",
             "ebur128=peak=true", "-f", "null", "-"],
            cancellationToken);
        if (result.ExitCode != 0)
            return new LoudnessMeasurement(
                false,
                null,
                null,
                null,
                result.Error.Trim());
        MatchCollection integratedMatches = Regex.Matches(
            result.Error,
            @"I:\s*(-?\d+(?:\.\d+)?)\s+LUFS",
            RegexOptions.CultureInvariant);
        MatchCollection peakMatches = Regex.Matches(
            result.Error,
            @"Peak:\s*(-?\d+(?:\.\d+)?)\s+dBFS",
            RegexOptions.CultureInvariant);
        MatchCollection rangeMatches = Regex.Matches(
            result.Error,
            @"LRA:\s*(-?\d+(?:\.\d+)?)\s+LU",
            RegexOptions.CultureInvariant);
        double? integrated = ParseLast(integratedMatches);
        double? peak = ParseLast(peakMatches);
        double? range = ParseLast(rangeMatches);
        return new LoudnessMeasurement(
            integrated is not null && range is not null && peak is not null,
            integrated,
            range,
            peak,
            integrated is null || range is null || peak is null
                ? "FFmpeg did not emit a complete loudness summary."
                : null);
    }

    private async Task<FrameContinuityScan> AnalyzeFrameContinuityAsync(
        string path,
        CompilationRequest request,
        CancellationToken cancellationToken)
    {
        double frameDuration = 1d / Math.Max(1, request.Fps);
        double minimumBlackDefectDuration =
            FrameContinuityPolicy.MinimumBlackDefectDurationSeconds(
                request.Fps);
        string filter = FormattableString.Invariant(
            $"blackdetect=d={minimumBlackDefectDuration:0.######}:pix_th=0.02,freezedetect=n=0.001:d=0.08,signalstats,metadata=print:key=lavfi.signalstats.YAVG");
        ProcessResult scan = await RunAsync(
            options.FfmpegPath,
            [
                "-hide_banner", "-nostats", "-i", path,
                "-an", "-vf", filter, "-f", "null", "-"
            ],
            cancellationToken);
        int blackFrames = Regex.Count(
            scan.Error,
            "black_start:",
            RegexOptions.CultureInvariant);
        int duplicateBursts = Regex.Count(
            scan.Error,
            "freeze_start:",
            RegexOptions.CultureInvariant);
        double[] luminance = Regex.Matches(
                scan.Error,
                @"lavfi\.signalstats\.YAVG=(?<value>\d+(?:\.\d+)?)",
                RegexOptions.CultureInvariant)
            .Cast<Match>()
            .Select(value => double.Parse(
                value.Groups["value"].Value,
                CultureInfo.InvariantCulture))
            .ToArray();
        int spikes = luminance.Zip(luminance.Skip(1)).Count(pair =>
            Math.Abs(pair.Second - pair.First) >= 48);
        int flashes = 0;
        for (int index = 1; index + 1 < luminance.Length; index++)
        {
            if (luminance[index] - luminance[index - 1] >= 42 &&
                luminance[index] - luminance[index + 1] >= 42)
                flashes++;
        }
        CinematicSequenceSegment[] segments =
            request.CinematicMoviePlan?.Segments
                .OrderBy(value => value.OutputStartSeconds)
                .ThenBy(value => value.Id, StringComparer.Ordinal)
                .ToArray() ?? [];
        double minimumShot = segments
            .Select(value =>
                value.OutputEndSeconds - value.OutputStartSeconds)
            .DefaultIfEmpty(0)
            .Min();
        int oneFrame = segments.Count(value =>
            value.OutputEndSeconds - value.OutputStartSeconds <=
            frameDuration * 1.5);
        int microSegments = segments.Count(value =>
            value.OutputEndSeconds - value.OutputStartSeconds <=
            frameDuration * 5.5);
        int boundaryDefects = segments.Zip(segments.Skip(1)).Count(pair =>
            Math.Abs(
                pair.Second.OutputStartSeconds -
                pair.First.OutputEndSeconds) > frameDuration * 0.55);
        List<string> violations = [];
        if (scan.ExitCode != 0)
            violations.Add("FRAME_SCAN_FFMPEG_FAILED");
        if (oneFrame > 0)
            violations.Add("ONE_FRAME_SEGMENT_DETECTED");
        if (microSegments > 0)
            violations.Add("ONE_TO_FIVE_FRAME_SEGMENT_DETECTED");
        if (blackFrames > 0)
            violations.Add("BLACK_FRAME_TRANSITION_DEFECT");
        if (boundaryDefects > 0)
            violations.Add("TRANSITION_BOUNDARY_INVALID");
        return new FrameContinuityScan(
            "1.0",
            violations.Count == 0,
            Math.Round(minimumShot, 6),
            oneFrame,
            microSegments,
            blackFrames,
            duplicateBursts,
            flashes,
            spikes,
            boundaryDefects == 0,
            scan.ExitCode == 0,
            violations);
    }

    private async Task<BriefBlackFrameRepair> RepairBriefBlackFramesAsync(
        string path,
        int clipNumber,
        int fps,
        CancellationToken cancellationToken)
    {
        double minimumDuration =
            FrameContinuityPolicy.MinimumBlackDefectDurationSeconds(fps);
        ProcessResult scan = await RunAsync(
            options.FfmpegPath,
            [
                "-hide_banner", "-nostats", "-i", path,
                "-an", "-vf",
                FormattableString.Invariant(
                    $"blackdetect=d={minimumDuration:0.######}:pix_th=0.02"),
                "-f", "null", "-"
            ],
            cancellationToken);
        if (scan.ExitCode != 0)
        {
            return new BriefBlackFrameRepair(
                clipNumber, Path.GetFileName(path), 0, 0, false,
                "BLACK_FRAME_SCAN_FAILED");
        }

        MediaMetadata metadata = await ProbeAsync(path, cancellationToken);
        if (metadata.Error is not null || metadata.DurationSeconds <= 0)
        {
            return new BriefBlackFrameRepair(
                clipNumber, Path.GetFileName(path), 0, 0, false,
                "BLACK_FRAME_REPAIR_PROBE_FAILED");
        }

        BlackInterval[] detected = Regex.Matches(
                scan.Error,
                @"black_start:(?<start>\d+(?:\.\d+)?)\s+black_end:(?<end>\d+(?:\.\d+)?)",
                RegexOptions.CultureInvariant)
            .Cast<Match>()
            .Select(value => new BlackInterval(
                double.Parse(value.Groups["start"].Value, CultureInfo.InvariantCulture),
                double.Parse(value.Groups["end"].Value, CultureInfo.InvariantCulture)))
            .ToArray();
        BlackInterval[] repairable = detected.Where(value =>
            FrameContinuityPolicy.IsBriefRepairableBlackDefect(
                value.StartSeconds,
                value.EndSeconds,
                metadata.DurationSeconds,
                fps)).ToArray();
        if (repairable.Length == 0)
        {
            return new BriefBlackFrameRepair(
                clipNumber, Path.GetFileName(path), detected.Length, 0, false, null);
        }

        List<string> filterParts = [];
        StringBuilder split = new("[0:v]split=");
        split.Append(repairable.Length + 1).Append("[repair_base]");
        for (int index = 0; index < repairable.Length; index++)
            split.Append("[repair_reference_").Append(index).Append(']');
        filterParts.Add(split.ToString());
        string repairInput = "repair_base";
        for (int index = 0; index < repairable.Length; index++)
        {
            BlackInterval value = repairable[index];
            (int first, int last, int replacement) =
                FrameContinuityPolicy.RepairFrameRange(
                    value.StartSeconds,
                    value.EndSeconds,
                    fps);
            string repairOutput = $"repair_output_{index}";
            filterParts.Add(
                $"[{repairInput}][repair_reference_{index}]" +
                $"freezeframes=first={first}:last={last}:replace={replacement}" +
                $"[{repairOutput}]");
            repairInput = repairOutput;
        }
        string filter = string.Join(';', filterParts);
        string repaired = Path.Combine(
            Path.GetDirectoryName(path)!,
            Path.GetFileNameWithoutExtension(path) + ".black-repair.tmp.mp4");
        if (File.Exists(repaired)) File.Delete(repaired);
        ProcessResult repair = await RunAsync(
            options.FfmpegPath,
            [
                "-y", "-hide_banner", "-loglevel", "error", "-i", path,
                "-filter_complex", filter,
                "-map", $"[{repairInput}]", "-map", "0:a?",
                "-c:v", "libx264", "-preset", "fast", "-crf", "18",
                "-c:a", "copy", "-movflags", "+faststart", repaired
            ],
            cancellationToken);
        await WriteProcessLogAsync(
            Path.Combine(
                Path.GetDirectoryName(path)!,
                $"clip-{clipNumber:D3}.black-repair.ffmpeg.log"),
            repair,
            cancellationToken);
        if (repair.ExitCode != 0 || !File.Exists(repaired))
        {
            if (File.Exists(repaired)) File.Delete(repaired);
            return new BriefBlackFrameRepair(
                clipNumber, Path.GetFileName(path), detected.Length, 0, false,
                "BLACK_FRAME_REPAIR_FAILED");
        }

        File.Move(repaired, path, true);
        return new BriefBlackFrameRepair(
            clipNumber,
            Path.GetFileName(path),
            detected.Length,
            repairable.Length,
            true,
            null);
    }

    private static double? ParseLast(MatchCollection matches)
    {
        if (matches.Count == 0)
            return null;
        return double.TryParse(
            matches[^1].Groups[1].Value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double value)
                ? value
                : null;
    }

    private async Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        string? resolved = PipelinePathResolver.Resolve(executable);
        if (resolved is null)
            return new ProcessResult(
                -1,
                string.Empty,
                $"Executable was not found: {executable}");
        ProcessStartInfo start = new()
        {
            FileName = resolved,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = new() { StartInfo = start };
        try
        {
            if (!process.Start()) return new ProcessResult(-1, string.Empty, "Process did not start.");
        }
        catch (Exception exception) when (
            exception is Win32Exception or IOException or UnauthorizedAccessException)
        {
            return new ProcessResult(-1, string.Empty, exception.Message);
        }
        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(
            Math.Max(1, options.FfmpegTimeoutSeconds)));
        Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            if (!cancellationToken.IsCancellationRequested)
                return new ProcessResult(
                    -1,
                    string.Empty,
                    $"Process timed out after {options.FfmpegTimeoutSeconds} seconds.");
            throw;
        }
        return new ProcessResult(process.ExitCode, await output, await error);
    }

    private static Task WriteProcessLogAsync(
        string path,
        ProcessResult result,
        CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(
            path,
            $"exitCode={result.ExitCode}{Environment.NewLine}" +
            $"stdout:{Environment.NewLine}{result.Output}{Environment.NewLine}" +
            $"stderr:{Environment.NewLine}{result.Error}",
            Utf8WithoutBom,
            cancellationToken);

    private static CompilationResult Failure(string error, int total, int skipped, long duration) =>
        new("1.1", false, null, 0, Math.Max(skipped, total), duration, 0, null, null, error);

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
    private sealed record LoudnessMeasurement(
        bool Success,
        double? IntegratedLoudnessLufs,
        double? LoudnessRangeLu,
        double? TruePeakDb,
        string? Error);
    private sealed record AudioSafetyPassAttempt(
        int Attempt,
        double BeforeIntegratedLoudnessLufs,
        double BeforeTruePeakDb,
        double RequestedGainDb,
        double PeakSafeGainDb,
        double CorrectionDb,
        int ExitCode,
        double? AfterIntegratedLoudnessLufs,
        double? AfterTruePeakDb,
        IReadOnlyList<string> RemainingViolations);

    private sealed record FrameContinuityScan(
        string SchemaVersion,
        bool IsValid,
        double MinimumShotDurationSeconds,
        int OneFrameSegmentCount,
        int OneToFiveFrameSegmentCount,
        int BlackFrameCount,
        int DuplicateFrameBurstCount,
        int FlashEventCount,
        int UnexpectedLuminanceSpikeCount,
        bool TransitionBoundaryValid,
        bool StablePts,
        IReadOnlyList<string> Violations);
    private sealed record BlackInterval(double StartSeconds, double EndSeconds);
    private sealed record BriefBlackFrameRepair(
        int ClipNumber,
        string FileName,
        int DetectedIntervals,
        int RepairedIntervals,
        bool Repaired,
        string? Error);
    private sealed class MediaMetadata
    {
        public bool HasVideo { get; init; }
        public bool HasAudio { get; init; }
        public double DurationSeconds { get; init; }
        public int? Width { get; init; }
        public int? Height { get; init; }
        public string? VideoCodec { get; init; }
        public string? AudioCodec { get; init; }
        public int? AudioSampleRate { get; init; }
        public string? Error { get; init; }
    }
}

public static class FfmpegMovieFilterBuilder
{
    public static string Color(ColorGradePreset preset) => preset switch
    {
        ColorGradePreset.None => "null",
        ColorGradePreset.Natural => "eq=contrast=1.02:saturation=1.03",
        ColorGradePreset.Competitive => "eq=contrast=1.08:saturation=1.08:brightness=0.01",
        ColorGradePreset.CinematicCool =>
            "colorbalance=bs=.05:gs=.01:rh=.02,curves=preset=medium_contrast",
        ColorGradePreset.CinematicWarm =>
            "colorbalance=rs=.04:gh=.01:bh=-.02,curves=preset=medium_contrast",
        ColorGradePreset.HighContrast => "eq=contrast=1.16:saturation=1.04",
        ColorGradePreset.Neon => "eq=contrast=1.08:saturation=1.18,colorbalance=bs=.03:rh=.03",
        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, null)
    };

    public static string Lut(string trustedAbsolutePath)
    {
        string path = Path.GetFullPath(trustedAbsolutePath)
            .Replace('\\', '/')
            .Replace(":", "\\:", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal);
        return $"lut3d=file='{path}'";
    }

    public static string CinematicFinish() =>
        "eq=contrast=1.14:saturation=1.22:gamma=0.97:brightness=-0.015," +
        "colorbalance=bs=.035:gs=.012:rh=.045:gh=.015," +
        // Keep the strong cinematic separation, but avoid an additional
        // full-frame curves pass: contrast is already shaped by the EQ above.
        "vignette=PI/7:eval=frame,unsharp=3:3:.28:3:3:0";

    public static string AudioMix(
        GenerationMovieSettings settings,
        MusicEditPlan? plan = null,
        AudioMixOptions? options = null,
        CinematicMoviePlan? cinematic = null)
    {
        options ??= new AudioMixOptions
        {
            MusicGainDb = settings.MusicGainDb,
            GameplayBaseGainDb = settings.GameplayGainDb
        };
        double[] killTimes = plan?.Segments
            .Select(value => value.PrimaryKillOutputTimeSeconds)
            .Where(value => value >= 0)
            .OrderBy(value => value)
            .ToArray() ?? [];
        string pulse = AccentPulse(killTimes, options);
        double gameplayBase = Linear(options.GameplayBaseGainDb);
        double gameplayAccent = Linear(Math.Max(
            options.GameplayBaseGainDb,
            options.GameplayKillAccentGainDb));
        double musicBase = Linear(options.MusicGainDb);
        string gameplayVolume = cinematic is null
            ? Number(gameplayBase)
            : NarrativeVolume(
                cinematic,
                gameplay: true,
                fallbackDb: options.GameplayBaseGainDb,
                adjustmentDb:
                    options.GameplayBaseGainDb - (-16));
        string musicVolume = cinematic is null
            ? Number(musicBase)
            : NarrativeVolume(
                cinematic,
                gameplay: false,
                fallbackDb: options.MusicGainDb,
                adjustmentDb: options.MusicGainDb - (-3));
        double outputDuration = cinematic?.TargetDurationSeconds ??
            PlanDurationSeconds(plan);
        double availableMusicDuration = plan is null
            ? 0
            : Math.Max(0, plan.MusicDurationSeconds);
        string musicTail = MusicTailFade(
            availableMusicDuration,
            outputDuration);
        if (killTimes.Length > 0)
        {
            gameplayVolume =
                $"({gameplayVolume})*(1+({Number(gameplayAccent / gameplayBase)}-1)*({pulse}))";
        }
        StringBuilder graph = new();
        graph.Append("[0:a:0]aresample=48000,volume='")
            .Append(gameplayVolume).Append("':eval=frame[game];")
            .Append("[1:a:0]aresample=48000")
            .Append(musicTail)
            .Append(",volume='")
            .Append(musicVolume).Append("':eval=frame[music];")
            .Append("[music][game]amix=inputs=2:duration=shortest:normalize=0,")
            .Append("loudnorm=I=-14:TP=")
            .Append(Number(options.OutputTruePeakDb))
            .Append(":LRA=7");
        if (options.EnableLimiter)
            graph.Append(",alimiter=limit=")
                .Append(Number(Linear(options.OutputTruePeakDb)))
                .Append(":attack=5:release=50:level=false");
        if (cinematic is not null)
        {
            graph.Append(",afade=t=in:st=0:d=0.30");
            if (outputDuration > 0.5)
            {
                double fadeDuration = Math.Min(0.5, outputDuration);
                double fadeStart = Math.Max(0, outputDuration - fadeDuration);
                graph.Append(",afade=t=out:st=")
                    .Append(Number(fadeStart))
                    .Append(":d=")
                    .Append(Number(fadeDuration));
            }
        }
        graph.Append("[mixed]");
        return graph.ToString();
    }

    private static string NarrativeVolume(
        CinematicMoviePlan cinematic,
        bool gameplay,
        double fallbackDb,
        double adjustmentDb)
    {
        Dictionary<string, SoundDesignSection> soundBySection =
            cinematic.SoundDesign.Sections.ToDictionary(
                value => value.MusicSectionId,
                StringComparer.Ordinal);
        string expression = Number(Linear(fallbackDb));
        foreach (CinematicSequenceSegment segment in cinematic.Segments
                     .OrderByDescending(value => value.OutputStartSeconds))
        {
            if (!soundBySection.TryGetValue(
                    segment.MusicSectionId,
                    out SoundDesignSection? sound))
                continue;
            double decibels = (gameplay
                    ? sound.GameplayGainDb
                    : sound.MusicGainDb) +
                adjustmentDb;
            expression = string.Create(
                CultureInfo.InvariantCulture,
                $"if(between(t\\,{segment.OutputStartSeconds:0.######}\\,{segment.OutputEndSeconds:0.######})\\,{Linear(decibels):0.######}\\,{expression})");
        }
        return expression;
    }

    public static string TimeWarp(
        string videoFilters,
        string audioFilters,
        string audioInput,
        TimeWarpPlan plan)
    {
        if (plan.Segments.Count == 0)
            throw new ArgumentException("Time-warp plan has no segments.", nameof(plan));
        StringBuilder graph = new();
        graph.Append("[0:v:0]").Append(videoFilters)
            .Append(",split=").Append(plan.Segments.Count);
        for (int index = 0; index < plan.Segments.Count; index++)
            graph.Append("[warp_v").Append(index).Append(']');
        graph.Append(';').Append('[').Append(audioInput).Append(']')
            .Append(audioFilters)
            .Append(",asplit=").Append(plan.Segments.Count);
        for (int index = 0; index < plan.Segments.Count; index++)
            graph.Append("[warp_a").Append(index).Append(']');
        graph.Append(';');
        for (int index = 0; index < plan.Segments.Count; index++)
        {
            TimeWarpSegment segment = plan.Segments[index];
            string start = Number(segment.SourceStartSeconds);
            string end = Number(segment.SourceEndSeconds);
            string speed = Number(segment.Speed);
            graph.Append("[warp_v").Append(index).Append("]trim=start=")
                .Append(start).Append(":end=").Append(end)
                .Append(",setpts=(PTS-STARTPTS)/").Append(speed)
                .Append("[warp_vo").Append(index).Append("];");
            graph.Append("[warp_a").Append(index).Append("]atrim=start=")
                .Append(start).Append(":end=").Append(end)
                .Append(",asetpts=PTS-STARTPTS,atempo=").Append(speed)
                .Append("[warp_ao").Append(index).Append("];");
        }
        for (int index = 0; index < plan.Segments.Count; index++)
            graph.Append("[warp_vo").Append(index).Append(']');
        graph.Append("concat=n=").Append(plan.Segments.Count)
            .Append(":v=1:a=0[warped_video];");
        for (int index = 0; index < plan.Segments.Count; index++)
            graph.Append("[warp_ao").Append(index).Append(']');
        graph.Append("concat=n=").Append(plan.Segments.Count)
            .Append(":v=0:a=1[warped_audio]");
        return graph.ToString();
    }

    private static double PlanDurationSeconds(MusicEditPlan? plan)
    {
        if (plan is null || plan.Segments.Count == 0)
            return 0;
        return plan.Segments.Max(segment =>
            segment.OutputStartSeconds + TimeWarpMath.OutputDuration(
                segment.TimeWarp,
                Math.Max(0.001,
                    segment.SourceEndSeconds - segment.SourceStartSeconds)));
    }

    private static string MusicTailFade(
        double availableMusicDuration,
        double outputDuration)
    {
        if (outputDuration <= 0 ||
            availableMusicDuration <= outputDuration + 0.05)
        {
            return string.Empty;
        }
        double fadeDuration = Math.Min(
            outputDuration,
            Math.Clamp(outputDuration * 0.08, 0.75, 1.5));
        double fadeStart = Math.Max(0, outputDuration - fadeDuration);
        return ",atrim=duration=" + Number(outputDuration) +
            ",afade=t=out:st=" + Number(fadeStart) +
            ":d=" + Number(fadeDuration);
    }

    public static string DurationLock(
        string videoInput,
        string audioInput,
        string videoOutput,
        string audioOutput,
        double durationSeconds,
        int fps) =>
        $";[{videoInput}]{DurationLockVideo(durationSeconds, fps).TrimStart(',')}[{videoOutput}];" +
        $"[{audioInput}]{DurationLockAudio(durationSeconds).TrimStart(',')}[{audioOutput}]";

    public static string DurationLockVideo(
        double durationSeconds,
        int fps) =>
        ",fps=" + Math.Max(1, fps).ToString(CultureInfo.InvariantCulture) +
        ",tpad=stop_mode=clone:stop_duration=" + Number(durationSeconds) +
        ",trim=duration=" + Number(durationSeconds) +
        ",setpts=PTS-STARTPTS";

    public static string DurationLockAudio(double durationSeconds) =>
        ",apad=whole_dur=" + Number(durationSeconds) +
        ",atrim=duration=" + Number(durationSeconds) +
        ",asetpts=PTS-STARTPTS";

    private static string Db(double value) =>
        FormattableString.Invariant($"{Math.Clamp(value, -60, 12):0.###}dB");

    public static (string Video, string Audio) CinematicIntroTransition(
        CinematicMoviePlan? cinematic,
        int clipIndex,
        double sourceDuration)
    {
        if (cinematic is null || cinematic.Segments.Count < 2)
            return (string.Empty, string.Empty);
        int firstHighlight = cinematic.Segments
            .Select((segment, index) => (segment, index))
            .Where(value => value.segment.HighlightId is not null)
            .Select(value => value.index)
            .DefaultIfEmpty(-1)
            .First();
        if (firstHighlight <= 0)
            return (string.Empty, string.Empty);
        const double duration = 0.14;
        if (clipIndex == firstHighlight)
        {
            return (
                FormattableString.Invariant(
                    $",fade=t=in:st=0:d={duration:0.##}:color=white"),
                FormattableString.Invariant(
                    $",afade=t=in:st=0:d={duration:0.##}"));
        }
        if (clipIndex == firstHighlight - 1)
        {
            double start = Math.Max(0, sourceDuration - duration);
            return (
                FormattableString.Invariant(
                    $",fade=t=out:st={start:0.######}:d={duration:0.##}:color=white"),
                FormattableString.Invariant(
                    $",afade=t=out:st={start:0.######}:d={duration:0.##}"));
        }
        return (string.Empty, string.Empty);
    }

    private static string Number(double value) =>
        value.ToString("0.######", CultureInfo.InvariantCulture);

    private static double Linear(double decibels) =>
        Math.Pow(10, Math.Clamp(decibels, -60, 12) / 20);

    private static string AccentPulse(
        IReadOnlyList<double> killTimes,
        AudioMixOptions options)
    {
        string[] pulses = killTimes.Select(kill =>
        {
            double attack = options.KillAccentAttackMilliseconds / 1000;
            double hold = options.KillAccentHoldMilliseconds / 1000;
            double release = options.KillAccentReleaseMilliseconds / 1000;
            double start = Math.Max(0, kill - attack);
            double holdEnd = kill + hold;
            double end = holdEnd + release;
            return string.Create(
                CultureInfo.InvariantCulture,
                $"if(between(t\\,{start:0.######}\\,{kill:0.######})\\,(t-{start:0.######})/{Math.Max(attack, 0.001):0.######}\\,if(between(t\\,{kill:0.######}\\,{holdEnd:0.######})\\,1\\,if(between(t\\,{holdEnd:0.######}\\,{end:0.######})\\,({end:0.######}-t)/{Math.Max(release, 0.001):0.######}\\,0)))");
        }).ToArray();
        return pulses.Length switch
        {
            0 => "0",
            1 => pulses[0],
            _ => pulses.Aggregate((left, right) => $"max({left}\\,{right})")
        };
    }
}

public static class FfmpegEffectFilterBuilder
{
    public static string Build(
        int width,
        int height,
        int fps,
        double durationSeconds,
        HighlightEffectPlan? plan)
    {
        List<string> filters =
        [
            $"scale={width}:{height}:force_original_aspect_ratio=decrease",
            $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2",
            $"fps={fps}"
        ];
        if (plan?.Preset == Domain.EffectPreset.Clean)
        {
            filters.Add("eq=saturation=1.02:contrast=1.01");
            filters.Add("fade=t=in:st=0:d=0.15");
            filters.Add(FormattableString.Invariant(
                $"fade=t=out:st={Math.Max(0, durationSeconds - 0.3):0.###}:d=0.3"));
        }
        if (plan?.Preset == Domain.EffectPreset.Dynamic)
        {
            filters.Add("eq=saturation=1.10:contrast=1.06");
            filters.Add("unsharp=5:5:0.65:3:3:0.25");
            EffectTimelineEvent[] zooms = plan.Events
                .Where(value => value.Type == EffectType.SmoothZoom)
                .ToArray();
            EffectTimelineEvent[] shakes = plan.Events
                .Where(value => value.Type == EffectType.ImpactShake)
                .ToArray();
            if (zooms.Length > 0 || shakes.Length > 0)
            {
                string zoomActivity = zooms.Length == 0
                    ? "0"
                    : zooms.Select(value => Pulse(value))
                        .Aggregate((left, right) => $"max({left}\\,{right})");
                string shakeActivity = shakes.Length == 0
                    ? "0"
                    : shakes.Select(value => Pulse(value))
                        .Aggregate((left, right) => $"max({left}\\,{right})");
                double intensity = zooms.Length == 0
                    ? 0
                    : zooms.Max(value => value.Intensity);
                string factor = FormattableString.Invariant(
                    $"1.025+{intensity:0.####}*{zoomActivity}");
                filters.Add($"scale=w='{width}*({factor})':h='{height}*({factor})':eval=frame");
                filters.Add(
                    $"crop={width}:{height}:" +
                    $"x='(iw-ow)/2+7*({shakeActivity})*sin(95*t)':" +
                    $"y='(ih-oh)/2+5*({shakeActivity})*cos(83*t)'");
            }
            foreach (EffectTimelineEvent color in plan.Events.Where(value =>
                         value.Type == EffectType.ColorPunch))
            {
                filters.Add(FormattableString.Invariant(
                    $"eq=saturation={1.0 + color.Intensity * 2:0.####}:contrast={1.0 + color.Intensity * 0.45:0.####}:enable='{Between(color)}'"));
            }
            foreach (EffectTimelineEvent flash in plan.Events.Where(value =>
                         value.Type == EffectType.HeadshotFlash))
            {
                filters.Add(FormattableString.Invariant(
                    $"eq=brightness={flash.Intensity:0.####}:enable='{Between(flash)}'"));
            }
            foreach (EffectTimelineEvent vignette in plan.Events.Where(value =>
                         value.Type == EffectType.VignettePulse))
            {
                filters.Add(
                    $"vignette=PI/12:eval=frame:enable='{Between(vignette)}'");
            }
        }
        filters.Add("format=yuv420p");
        return string.Join(',', filters);
    }

    public static string BuildAudio(
        double durationSeconds,
        HighlightEffectPlan? plan)
    {
        List<string> filters = ["aresample=48000"];
        if (plan?.Preset == Domain.EffectPreset.Clean)
        {
            filters.Add("loudnorm=I=-16:TP=-1.5:LRA=11");
            filters.Add("afade=t=in:st=0:d=0.15");
            filters.Add(FormattableString.Invariant(
                $"afade=t=out:st={Math.Max(0, durationSeconds - 0.3):0.###}:d=0.3"));
        }
        else if (plan?.Preset == Domain.EffectPreset.Dynamic)
        {
            filters.Add("loudnorm=I=-14:TP=-1.2:LRA=9");
            filters.Add("afade=t=in:st=0:d=0.035");
            filters.Add(FormattableString.Invariant(
                $"afade=t=out:st={Math.Max(0, durationSeconds - 0.08):0.###}:d=0.08"));
            filters.Add("alimiter=limit=0.92:attack=3:release=35");
        }
        return string.Join(',', filters);
    }

    private static string Between(EffectTimelineEvent value)
    {
        double start = value.StartMilliseconds / 1000d;
        double end = (value.StartMilliseconds + value.DurationMilliseconds) / 1000d;
        return FormattableString.Invariant($"between(t\\,{start:0.###}\\,{end:0.###})");
    }

    private static string Pulse(EffectTimelineEvent value)
    {
        double start = value.StartMilliseconds / 1000d;
        double duration = Math.Max(0.001, value.DurationMilliseconds / 1000d);
        double end = start + duration;
        double rise = value.PeakOffsetMilliseconds > 0
            ? Math.Min(duration, value.PeakOffsetMilliseconds / 1000d)
            : duration / 2;
        rise = Math.Max(0.001, rise);
        double fall = Math.Max(0.001, duration - rise);
        double peak = start + rise;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"if(between(t\\,{start:0.###}\\,{end:0.###})\\,if(lt(t\\,{peak:0.###})\\,(t-{start:0.###})/{rise:0.###}\\,({end:0.###}-t)/{fall:0.###})\\,0)");
    }
}
