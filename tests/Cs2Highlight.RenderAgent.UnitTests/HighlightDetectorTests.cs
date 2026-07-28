using Cs2Highlight.Analysis;
using Cs2Highlight.RenderAgent.Application;

namespace Cs2Highlight.RenderAgent.UnitTests;

public sealed class HighlightDetectorTests
{
    private readonly RuleBasedHighlightDetector detector = new();

    [Theory]
    [InlineData(2, HighlightType.DoubleKill)]
    [InlineData(3, HighlightType.TripleKill)]
    [InlineData(4, HighlightType.QuadKill)]
    [InlineData(5, HighlightType.Ace)]
    public void ClassifiesMaximalMultikillWithoutNestedDuplicates(
        int killCount,
        HighlightType expectedType)
    {
        DemoAnalysis analysis = Analysis(Kills("p1", 1, killCount, 100, 100));

        IReadOnlyList<HighlightCandidate> result = detector.Detect(
            analysis,
            new HighlightDetectionOptions());

        HighlightCandidate candidate = Assert.Single(result);
        Assert.Equal(expectedType, candidate.Type);
        Assert.Equal(killCount, candidate.KillCount);
    }

    [Fact]
    public void DoesNotCombineKillsOutsideGap()
    {
        DemoAnalysis analysis = Analysis(
        [
            Kill(1, 100, "p1", "v1"),
            Kill(2, 600, "p1", "v2")
        ]);

        Assert.Empty(detector.Detect(analysis, new HighlightDetectionOptions()));
    }

    [Fact]
    public void KeepsPlayersAndRoundsSeparate()
    {
        DemoAnalysis analysis = Analysis(
        [
            Kill(1, 100, "p1", "v1", round: 1),
            Kill(2, 110, "p2", "v2", round: 1),
            Kill(3, 120, "p1", "v3", round: 2),
            Kill(4, 130, "p2", "v4", round: 2)
        ]);

        Assert.Empty(detector.Detect(analysis, new HighlightDetectionOptions()));
    }

    [Fact]
    public void ExcludesMissingKillerSuicideAndTeamkill()
    {
        DemoAnalysis analysis = Analysis(
        [
            Kill(1, 100, null, "v1"),
            Kill(2, 110, "p1", "p1"),
            Kill(3, 120, "p1", "v2", killerTeam: "T", victimTeam: "T")
        ]);

        Assert.Empty(detector.Detect(analysis, new HighlightDetectionOptions()));
    }

    [Fact]
    public void SupportsUnsortedAndSameTickInput()
    {
        DemoAnalysis analysis = Analysis(
        [
            Kill(2, 100, "p1", "v2"),
            Kill(1, 100, "p1", "v1")
        ]);

        HighlightCandidate result = Assert.Single(detector.Detect(
            analysis,
            new HighlightDetectionOptions()));

        Assert.Equal([1, 2], result.SourceEventIndexes);
    }

    [Fact]
    public void GapAtBoundaryIsIncluded()
    {
        DemoAnalysis analysis = Analysis(
        [
            Kill(1, 100, "p1", "v1"),
            Kill(2, 484, "p1", "v2")
        ]);

        Assert.Single(detector.Detect(analysis, new HighlightDetectionOptions()));
    }

    [Fact]
    public void SeparateSeriesRemainSeparate()
    {
        DemoAnalysis analysis = Analysis(
        [
            Kill(1, 100, "p1", "v1"),
            Kill(2, 120, "p1", "v2"),
            Kill(3, 1000, "p1", "v3"),
            Kill(4, 1020, "p1", "v4")
        ]);

        Assert.Equal(2, detector.Detect(analysis, new HighlightDetectionOptions()).Count);
    }

    [Fact]
    public void WindowIsClampedToRoundAndDemo()
    {
        DemoAnalysis analysis = Analysis(
            Kills("p1", 1, 2, 110, 10),
            new DemoRound(1, 100, 105, 130, "T", "CTWin"),
            durationTicks: 130);

        HighlightCandidate result = Assert.Single(detector.Detect(
            analysis,
            new HighlightDetectionOptions()));

        Assert.Equal(100, result.StartTick);
        Assert.Equal(130, result.EndTick);
    }

    [Fact]
    public void ScoringIsDeterministicAndExplained()
    {
        DemoAnalysis analysis = Analysis(
        [
            Kill(1, 100, "p1", "v1", headshot: true),
            Kill(2, 110, "p1", "v2", headshot: true),
            Kill(3, 120, "p1", "v3", headshot: true)
        ]);

        HighlightCandidate first = Assert.Single(detector.Detect(analysis, new HighlightDetectionOptions()));
        HighlightCandidate second = Assert.Single(detector.Detect(analysis, new HighlightDetectionOptions()));

        Assert.Equal(first.Score, second.Score);
        Assert.Equal(first.ScoreBreakdown.Total, first.Score);
        Assert.Contains("HEADSHOT_STREAK", first.Tags);
    }

    [Fact]
    public void SelectorUsesStableTieBreakers()
    {
        HighlightCandidate later = Candidate("b", 100, 200);
        HighlightCandidate earlier = Candidate("a", 90, 190);

        HighlightCandidate? selected = new BestHighlightSelector().SelectBest([later, earlier]);

        Assert.Equal("a", selected?.Id);
        Assert.Null(new BestHighlightSelector().SelectBest([]));
    }

    [Fact]
    public void RenderJobBuilderUsesExistingStageOneContract()
    {
        string demo = Path.Combine(Path.GetTempPath(), "match.dem");
        HighlightCandidate candidate = Candidate("candidate", 100, 200) with
        {
            PlayerId = "76561199031052443",
            PlayerName = "Игрок"
        };

        RenderJob job = new RenderJobBuilder().Build(
            demo,
            candidate,
            new RenderJobBuildOptions { OutputRoot = Path.GetTempPath() });

        Assert.Equal("76561199031052443", job.Player.SteamId);
        Assert.Equal(100, job.Segment.StartTick);
        Assert.Equal(200, job.Segment.EndTick);
    }

    private static DemoAnalysis Analysis(
        IReadOnlyList<KillEvent> kills,
        DemoRound? round = null,
        long durationTicks = 2000) =>
        new(
            "1.0",
            new ParserInfo("test", "1"),
            new DemoMetadata("match.dem", "de_test", 64, durationTicks, null),
            [],
            round is null ? [new DemoRound(1, 0, 10, durationTicks, "T", "CTWin")] : [round],
            kills,
            []);

    private static KillEvent[] Kills(
        string player,
        int round,
        int count,
        long start,
        long gap) =>
        Enumerable.Range(0, count)
            .Select(index => Kill(index + 1, start + index * gap, player, $"v{index}", round))
            .ToArray();

    private static KillEvent Kill(
        int index,
        long tick,
        string? killer,
        string victim,
        int round = 1,
        bool headshot = false,
        string? killerTeam = "T",
        string? victimTeam = "CT") =>
        new(
            index,
            tick,
            round,
            killer,
            killer,
            victim,
            victim,
            null,
            "ak47",
            headshot,
            killerTeam,
            victimTeam);

    private static HighlightCandidate Candidate(string id, long start, long end) =>
        new(
            id,
            HighlightType.DoubleKill,
            "76561198000000001",
            "Player",
            1,
            start,
            end,
            start,
            end,
            2,
            0,
            50,
            new ScoreBreakdown(40, 0, 0, 10, 0, 0, 50),
            [1, 2],
            []);
}
