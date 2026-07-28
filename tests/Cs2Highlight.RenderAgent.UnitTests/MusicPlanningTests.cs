using Cs2Highlight.Analysis;
using Cs2Highlight.Music;

namespace Cs2Highlight.RenderAgent.UnitTests;

public sealed class MusicPlanningTests
{
    [Fact]
    public void AnchorsPreferDropWhenEventsShareTimestamp()
    {
        MusicAnalysis analysis = Music(
            beats: [new MusicBeat(1, 10, 0.9, 0.8)],
            drops: [new MusicDropCandidate(1, 10, 0.92, 0.8, 0.9, 0.7, 0.8)]);

        MusicalAnchor anchor = Assert.Single(new MusicalAnchorBuilder().Build(analysis));

        Assert.Equal(MusicalAnchorType.Drop, anchor.Type);
    }

    [Fact]
    public void AceImportanceIsHigherThanSoloAndBreakdownIsDeterministic()
    {
        HighlightImportanceCalculator calculator = new();

        HighlightImportance solo = calculator.Calculate(Highlight(HighlightType.SoloKill), 2);
        HighlightImportance ace = calculator.Calculate(Highlight(HighlightType.Ace), 2);

        Assert.True(ace.Total > solo.Total);
        Assert.Equal(ace.Total, calculator.Calculate(Highlight(HighlightType.Ace), 2).Total);
    }

    [Fact]
    public void PlannerAssignsImportantKillToDropDeterministically()
    {
        MusicAnalysis music = Music(
            beats: [new MusicBeat(1, 3, 0.8, 0.8)],
            drops: [new MusicDropCandidate(1, 5, 1, 1, 1, 1, 0.9)]);
        HighlightCandidate highlight = Highlight(HighlightType.Ace);
        SelectedHighlight selected = new(
            highlight.Id,
            highlight,
            new SafeClipBounds(0, 0, 5, 5, 8, 8),
            1);
        MusicEditPlanner planner = new(
            new MusicalAnchorBuilder(),
            new HighlightImportanceCalculator(),
            new TimeWarpPlanner());

        MusicEditPlan first = planner.Create(
            "g1", "music.mp3", music, [selected], new MusicEditOptions());
        MusicEditPlan second = planner.Create(
            "g1", "music.mp3", music, [selected], new MusicEditOptions());

        MusicEditSegment segment = Assert.Single(first.Segments);
        Assert.Equal(MusicalAnchorType.Drop, segment.TargetMusicAnchor?.Type);
        Assert.Equal(segment.TargetMusicAnchor?.Id, Assert.Single(second.Segments).TargetMusicAnchor?.Id);
    }

    [Fact]
    public void ExcessiveWarpFallsBackToNaturalTiming()
    {
        TimeWarpPlan result = new TimeWarpPlanner().Create(
            new SafeClipBounds(0, 0, 5, 5, 8, 8),
            new MusicalAnchor("beat", MusicalAnchorType.StrongBeat, 1, 1, 1),
            0,
            MusicSyncIntensity.Expressive,
            new TimeWarpOptions());

        Assert.Equal(1, result.BaseSpeedFactor);
        Assert.Contains("EXCESSIVE_TIME_WARP_FALLBACK", result.Warnings);
    }

    private static MusicAnalysis Music(
        IReadOnlyList<MusicBeat> beats,
        IReadOnlyList<MusicDropCandidate> drops) =>
        new(
            "1.0",
            new MusicAnalyzerInfo("test", "1", "fixture"),
            new MusicAudioInfo("music.mp3", 30, 48000, 2, 120, 0.8, -14),
            beats,
            [],
            [],
            [],
            drops,
            []);

    private static HighlightCandidate Highlight(HighlightType type) =>
        new(
            $"highlight-{type}",
            type,
            "76561198000000001",
            "Player",
            1,
            100,
            100,
            0,
            512,
            type == HighlightType.SoloKill ? 1 : 5,
            1,
            50,
            new ScoreBreakdown(20, 0, 0, 0, 0, 0, 20),
            [1],
            [])
        {
            BeautyScore = 20,
            Kills = [new KillDescriptor(1, 100, "p", "v", "ak47", true)]
        };
}
