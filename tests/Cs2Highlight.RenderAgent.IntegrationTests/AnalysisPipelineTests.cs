using System.Text.Json;
using Cs2Highlight.Analysis;
using Cs2Highlight.RenderAgent.Application;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cs2Highlight.RenderAgent.IntegrationTests;

public sealed class AnalysisPipelineTests : IDisposable
{
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string root = Path.Combine(Path.GetTempPath(), $"analysis-pipeline-{Guid.NewGuid():N}");

    [Fact]
    public async Task ProducesAllStageTwoArtifactsFromParserOutput()
    {
        Directory.CreateDirectory(root);
        string demo = Path.Combine(root, "match.dem");
        await File.WriteAllBytesAsync(demo, [1, 2, 3]);
        string output = Path.Combine(root, "analysis");
        DemoAnalysis analysis = CreateAnalysis();
        AnalysisPipeline pipeline = new(
            new FakeParser(analysis),
            new RuleBasedHighlightDetector(),
            new BestHighlightSelector(),
            new RenderJobBuilder(),
            new FixedTimeProvider(),
            NullLogger<AnalysisPipeline>.Instance);

        AnalysisArtifacts result = await pipeline.RunAsync(
            demo,
            output,
            new HighlightDetectionOptions(),
            new RenderJobBuildOptions { OutputRoot = output },
            CancellationToken.None);

        Assert.True(File.Exists(result.DemoAnalysisPath));
        Assert.True(File.Exists(result.HighlightsPath));
        Assert.True(File.Exists(result.BestHighlightPath));
        Assert.True(File.Exists(result.RenderJobPath));
        Assert.NotNull(result.BestHighlight);
        string renderJson = await File.ReadAllTextAsync(result.RenderJobPath!);
        Assert.Contains("\"steamId\": \"76561198000000001\"", renderJson);
        RenderJob? renderJob = JsonSerializer.Deserialize<RenderJob>(renderJson, WebJsonOptions);
        Assert.NotNull(renderJob);
        Assert.True(
            RenderJobValidator.Validate(renderJob, new RenderEnvironmentOptions()).IsValid);
    }

    [Fact]
    public async Task WritesNoHighlightResultWithoutMaskingParserSuccess()
    {
        Directory.CreateDirectory(root);
        string demo = Path.Combine(root, "empty.dem");
        await File.WriteAllBytesAsync(demo, [1]);
        string output = Path.Combine(root, "empty-analysis");
        AnalysisPipeline pipeline = new(
            new FakeParser(CreateAnalysis() with { Kills = [] }),
            new RuleBasedHighlightDetector(),
            new BestHighlightSelector(),
            new RenderJobBuilder(),
            new FixedTimeProvider(),
            NullLogger<AnalysisPipeline>.Instance);

        AnalysisArtifacts result = await pipeline.RunAsync(
            demo,
            output,
            new HighlightDetectionOptions(),
            new RenderJobBuildOptions { OutputRoot = output },
            CancellationToken.None);

        Assert.Null(result.BestHighlight);
        Assert.Null(result.RenderJobPath);
        string json = await File.ReadAllTextAsync(result.BestHighlightPath);
        Assert.Contains("NO_HIGHLIGHTS_FOUND", json);
    }

    private static DemoAnalysis CreateAnalysis() =>
        new(
            "1.0",
            new ParserInfo("fake", "1"),
            new DemoMetadata("match.dem", "de_test", 64, 1000, 10000),
            [new DemoPlayer("76561198000000001", "76561198000000001", "Player")],
            [new DemoRound(1, 1, 10, 900, "T", "TerroristsWin")],
            [
                new KillEvent(
                    1, 100, 1, "76561198000000001", "Player", "v1", "Victim1",
                    null, "ak47", true, "T", "CT"),
                new KillEvent(
                    2, 120, 1, "76561198000000001", "Player", "v2", "Victim2",
                    null, "ak47", false, "T", "CT")
            ],
            []);

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class FakeParser(DemoAnalysis analysis) : IDemoParser
    {
        private static readonly JsonSerializerOptions JsonOptions =
            new(JsonSerializerDefaults.Web) { WriteIndented = true };

        public async Task<DemoAnalysis> AnalyzeAsync(
            string demoPath,
            string outputPath,
            CancellationToken cancellationToken)
        {
            await using FileStream stream = File.Create(outputPath);
            await JsonSerializer.SerializeAsync(
                stream,
                analysis,
                JsonOptions,
                cancellationToken);
            return analysis;
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);
    }
}
