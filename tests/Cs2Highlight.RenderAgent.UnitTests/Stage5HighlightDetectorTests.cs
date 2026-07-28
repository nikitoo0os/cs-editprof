using Cs2Highlight.Analysis;

namespace Cs2Highlight.RenderAgent.UnitTests;

public sealed class Stage5HighlightDetectorTests
{
    private readonly RuleBasedHighlightDetector detector = new();

    [Fact]
    public void SignificantSoloKillIsDetectedWithExplainedBeautyScore()
    {
        KillEvent kill = Kill(1, 640, "ak47", headshot: true) with
        {
            Wallbang = true,
            OneTap = true,
            KillerHealth = 12,
            DistanceMeters = 30
        };

        HighlightCandidate result = Assert.Single(
            detector.Detect(Analysis([kill]), new HighlightDetectionOptions()));

        Assert.Equal(HighlightType.SoloKill, result.Type);
        Assert.Equal(20, result.ScoreBreakdown.HeadshotBonus);
        Assert.Equal(25, result.ScoreBreakdown.WallbangBonus);
        Assert.Equal(20, result.ScoreBreakdown.OneTapBonus);
        Assert.Equal(10, result.ScoreBreakdown.LowHealthBonus);
        Assert.Equal(10, result.ScoreBreakdown.LongDistanceBonus);
        Assert.Equal(
            result.ScoreBreakdown.CombatScore + result.ScoreBreakdown.BeautyScore,
            result.TotalScore);
    }

    [Fact]
    public void PlainSoloBelowThresholdIsExcludedAndNullableSignalsAreSafe()
    {
        IReadOnlyList<HighlightCandidate> result = detector.Detect(
            Analysis([Kill(1, 640, "unknown")]),
            new HighlightDetectionOptions());

        Assert.Empty(result);
    }

    [Fact]
    public void SoloMaximumIsAppliedPerPlayerDeterministically()
    {
        DemoAnalysis analysis = Analysis(
        [
            Kill(1, 640, "ak47", headshot: true),
            Kill(2, 1280, "ak47", headshot: true),
            Kill(3, 1920, "ak47", headshot: true)
        ]);
        HighlightDetectionOptions options = new()
        {
            MaximumGapBetweenKillsSeconds = 1,
            SoloKills = new SoloKillDetectionOptions
            {
                MaximumSoloCandidatesPerDemo = 2
            }
        };

        IReadOnlyList<HighlightCandidate> result = detector.Detect(analysis, options);

        Assert.Equal(2, result.Count);
        Assert.Equal([640L, 1280L], result.Select(value => value.FirstKillTick));
    }

    [Fact]
    public void WeaponSequenceNormalizesAliasesAndMarksSwaps()
    {
        DemoAnalysis analysis = Analysis(
        [
            Kill(1, 640, "weapon_m4a1_silencer"),
            Kill(2, 700, "M4A1-S"),
            Kill(3, 760, "weapon_deagle")
        ]);

        HighlightCandidate result = Assert.Single(
            detector.Detect(analysis, new HighlightDetectionOptions()));

        Assert.Collection(
            result.WeaponSequence,
            first =>
            {
                Assert.Equal("m4a1_silencer", first.WeaponCode);
                Assert.Equal(2, first.KillCount);
                Assert.False(first.SwapBefore);
                Assert.StartsWith("/assets/weapons/", first.IconPath);
            },
            second =>
            {
                Assert.Equal("deagle", second.WeaponCode);
                Assert.True(second.SwapBefore);
            });
    }

    [Fact]
    public void TimingKeepsPostRollMinimumDurationRoundHoldAndDemoBound()
    {
        KillEvent kill = Kill(1, 1850, "ak47", headshot: true) with
        {
            RoundEndingKill = true
        };
        DemoAnalysis analysis = Analysis([kill], durationTicks: 2100);
        HighlightCandidate result = Assert.Single(detector.Detect(
            analysis,
            new HighlightDetectionOptions
            {
                PreRollSeconds = 1,
                PostRollSeconds = 3,
                RoundEndHoldSeconds = 2.5,
                MinimumClipDurationSeconds = 6
            }));

        Assert.Equal(1786, result.StartTick);
        Assert.Equal(2100, result.EndTick);
        Assert.True(result.EndTick > result.LastKillTick);
    }

    private static DemoAnalysis Analysis(
        IReadOnlyList<KillEvent> kills,
        long durationTicks = 4000) =>
        new(
            "1.1",
            new ParserInfo("test", "1"),
            new DemoMetadata("match.dem", "de_test", 64, durationTicks, null),
            [],
            [new DemoRound(1, 0, 64, 2000, "T", "TargetBombed")],
            kills,
            []);

    private static KillEvent Kill(
        int index,
        long tick,
        string weapon,
        bool headshot = false) =>
        new(
            index,
            tick,
            1,
            "p1",
            "Player",
            $"v{index}",
            $"Victim {index}",
            null,
            weapon,
            headshot,
            "T",
            "CT");
}
