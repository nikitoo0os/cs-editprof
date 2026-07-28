using System.Diagnostics;
using System.Text.Json;
using Cs2Highlight.RenderAgent.Application;
using Microsoft.Extensions.Logging;

namespace Cs2Highlight.Analysis;

public sealed partial class AnalysisPipeline(
    IDemoParser demoParser,
    IHighlightDetector detector,
    IBestHighlightSelector selector,
    IRenderJobBuilder renderJobBuilder,
    TimeProvider timeProvider,
    ILogger<AnalysisPipeline> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<AnalysisArtifacts> RunAsync(
        string demoPath,
        string outputDirectory,
        HighlightDetectionOptions detectionOptions,
        RenderJobBuildOptions renderOptions,
        CancellationToken cancellationToken)
    {
        string demo = Path.GetFullPath(demoPath);
        string output = Path.GetFullPath(outputDirectory);
        if (!File.Exists(demo))
        {
            throw Error("DEMO_NOT_FOUND", $"Demo was not found: {demo}", AnalysisStage.ValidatingInput);
        }
        if (!string.Equals(Path.GetExtension(demo), ".dem", StringComparison.OrdinalIgnoreCase))
        {
            throw Error("DEMO_NOT_FOUND", "Input must be a .dem file.", AnalysisStage.ValidatingInput);
        }
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
        {
            throw Error(
                "OUTPUT_WRITE_FAILED",
                $"Output directory must be empty: {output}",
                AnalysisStage.ValidatingInput);
        }
        Directory.CreateDirectory(Path.Combine(output, "logs"));

        string analysisPath = Path.Combine(output, "demo-analysis.json");
        Stopwatch parserWatch = Stopwatch.StartNew();
        LogRunningParser(logger, demo);
        DemoAnalysis analysis = await demoParser.AnalyzeAsync(demo, analysisPath, cancellationToken);
        LogParserCompleted(logger, parserWatch.ElapsedMilliseconds);
        analysis = AnalysisValidator.Validate(analysis);
        LogAnalysisSummary(
            logger,
            analysis.Demo.MapName,
            analysis.Demo.TickRate,
            analysis.Players.Count,
            analysis.Rounds.Count,
            analysis.Kills.Count);
        if (detectionOptions.TargetPlayerId is not null &&
            !analysis.Players.Any(player =>
                string.Equals(
                    player.PlayerId,
                    detectionOptions.TargetPlayerId,
                    StringComparison.Ordinal)))
        {
            throw Error(
                "PLAYER_NOT_FOUND",
                $"Player {detectionOptions.TargetPlayerId} was not found in the demo.",
                AnalysisStage.ValidatingAnalysis);
        }

        Stopwatch detectionWatch = Stopwatch.StartNew();
        IReadOnlyList<HighlightCandidate> candidates = detector.Detect(analysis, detectionOptions);
        LogHighlightsDetected(
            logger,
            candidates.Count,
            detectionWatch.ElapsedMilliseconds);
        HighlightCandidate? best = selector.SelectBest(candidates);

        DateTimeOffset generatedAt = timeProvider.GetUtcNow();
        HighlightsDocument highlights = new(
            "1.1",
            analysis.Demo.FileName,
            generatedAt,
            new HighlightOptionsDocument(
                detectionOptions.TargetPlayerId,
                detectionOptions.MaximumGapBetweenKillsSeconds,
                detectionOptions.PreRollSeconds,
                detectionOptions.PostRollSeconds)
            {
                RoundEndHoldSeconds = detectionOptions.RoundEndHoldSeconds,
                MinimumClipDurationSeconds = detectionOptions.MinimumClipDurationSeconds,
                MaximumClipDurationSeconds = detectionOptions.MaximumClipDurationSeconds
            },
            candidates);
        BestHighlightDocument bestDocument = new(
            "1.1",
            best is not null,
            best,
            best is null ? "NO_HIGHLIGHTS_FOUND" : null);
        string highlightsPath = Path.Combine(output, "highlights.json");
        string bestPath = Path.Combine(output, "best-highlight.json");
        await WriteJsonAsync(highlightsPath, highlights, cancellationToken);
        await WriteJsonAsync(bestPath, bestDocument, cancellationToken);

        string? renderJobPath = null;
        if (best is not null)
        {
            RenderJob renderJob;
            try
            {
                renderJob = renderJobBuilder.Build(demo, best, renderOptions);
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
            {
                throw Error(
                    "RENDER_JOB_BUILD_FAILED",
                    exception.Message,
                    AnalysisStage.BuildingRenderJob,
                    exception);
            }
            renderJobPath = Path.Combine(output, "render-job.json");
            await WriteJsonAsync(renderJobPath, renderJob, cancellationToken);
            LogBestSelected(
                logger,
                best.Type,
                best.PlayerId,
                best.RoundNumber,
                best.Score,
                best.StartTick,
                best.EndTick);
        }

        await WriteDetectorLogAsync(
            output,
            analysis,
            detectionOptions.TargetPlayerId,
            candidates,
            best,
            cancellationToken);
        return new AnalysisArtifacts(analysisPath, highlightsPath, bestPath, renderJobPath, best);
    }

    private static async Task WriteJsonAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            throw Error(
                "OUTPUT_WRITE_FAILED",
                $"Artifact already exists: {path}",
                AnalysisStage.WritingArtifacts);
        }
        await using FileStream stream = new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.Asynchronous);
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);
    }

    private static async Task WriteDetectorLogAsync(
        string output,
        DemoAnalysis analysis,
        string? targetPlayerId,
        IReadOnlyList<HighlightCandidate> candidates,
        HighlightCandidate? best,
        CancellationToken cancellationToken)
    {
        string[] lines =
        [
            $"schema={analysis.SchemaVersion}",
            $"map={analysis.Demo.MapName}",
            $"tickRate={analysis.Demo.TickRate}",
            $"players={analysis.Players.Count}",
            $"rounds={analysis.Rounds.Count}",
            $"kills={analysis.Kills.Count}",
            $"targetPlayerId={targetPlayerId ?? "<all>"}",
            $"candidates={candidates.Count}",
            best is null
                ? "result=NO_HIGHLIGHTS_FOUND"
                : $"best={best.Id} type={best.Type} score={best.Score} ticks={best.StartTick}-{best.EndTick}"
        ];
        await File.WriteAllLinesAsync(
            Path.Combine(output, "logs", "highlight-detector.log"),
            lines,
            cancellationToken);
    }

    private static AnalysisException Error(
        string code,
        string message,
        AnalysisStage stage,
        Exception? exception = null) =>
        new(new AnalysisError(code, message, stage, false, exception));

    [LoggerMessage(Level = LogLevel.Information, Message = "Running demo parser for {DemoPath}")]
    private static partial void LogRunningParser(ILogger logger, string demoPath);

    [LoggerMessage(Level = LogLevel.Information, Message = "Parser completed in {ElapsedMilliseconds} ms")]
    private static partial void LogParserCompleted(ILogger logger, long elapsedMilliseconds);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Parsed map={Map} tickRate={TickRate} players={Players} rounds={Rounds} kills={Kills}")]
    private static partial void LogAnalysisSummary(
        ILogger logger,
        string map,
        int tickRate,
        int players,
        int rounds,
        int kills);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Detected {CandidateCount} highlights in {ElapsedMilliseconds} ms")]
    private static partial void LogHighlightsDetected(
        ILogger logger,
        int candidateCount,
        long elapsedMilliseconds);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Selected {Type} player={PlayerId} round={Round} score={Score} ticks={StartTick}-{EndTick}")]
    private static partial void LogBestSelected(
        ILogger logger,
        HighlightType type,
        string playerId,
        int round,
        double score,
        long startTick,
        long endTick);
}
