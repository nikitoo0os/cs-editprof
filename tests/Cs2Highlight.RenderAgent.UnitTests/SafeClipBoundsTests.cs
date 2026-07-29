using Cs2Highlight.Analysis;
using Cs2Highlight.RenderAgent.Application;
using Cs2Highlight.RenderAgent.Infrastructure;

namespace Cs2Highlight.RenderAgent.UnitTests;

public sealed class SafeClipBoundsTests
{
    [Fact]
    public void DefaultHighlightWindowIsOneSecondBeforeAndAfter()
    {
        HighlightDetectionOptions options = new();

        Assert.Equal(1, options.PreRollSeconds);
        Assert.Equal(1, options.PostRollSeconds);
        Assert.Equal(1, options.SafeTiming.SoloPostKillHoldSeconds);
        Assert.Equal(1, options.SafeTiming.MultikillPostKillHoldSeconds);
        Assert.Equal(2, options.SafeTiming.MinimumClipDurationSeconds);
    }

    [Theory]
    [InlineData(HighlightType.SoloKill, false, 1.0)]
    [InlineData(HighlightType.TripleKill, false, 1.0)]
    [InlineData(HighlightType.Ace, false, 1.0)]
    public void SafeEndPreservesConfiguredTail(
        HighlightType type,
        bool roundEnding,
        double expectedTail)
    {
        var result = SafeClipBoundsCalculator.Calculate(
            new SafeClipTimingRequest(
                type, 64, 320, 320, null, roundEnding, 1280, 64, 0),
            new SafeClipTimingOptions(),
            25);

        Assert.Equal(320 + (long)(expectedTail * 64), result.SafeEndTick);
        Assert.True(result.PlannedEndTick >= result.SafeEndTick);
    }

    [Fact]
    public void RoundEndingClipStopsOneSecondAfterLastKill()
    {
        var result = SafeClipBoundsCalculator.Calculate(
            new SafeClipTimingRequest(
                HighlightType.DoubleKill, 100, 900, 900, 1000, true, 1200, 64, 0),
            new SafeClipTimingOptions(),
            25);

        Assert.Equal(964, result.SafeEndTick);
        Assert.Equal(964, result.PlannedEndTick);
    }

    [Fact]
    public void MinimumDurationAndAudioTailNeverMoveEndBeforeSafeEnd()
    {
        var result = SafeClipBoundsCalculator.Calculate(
            new SafeClipTimingRequest(
                HighlightType.SoloKill, 100, 110, 110, null, false, 5000, 64, 0),
            new SafeClipTimingOptions
            {
                SoloPostKillHoldSeconds = 0,
                DeathAnimationAllowanceSeconds = 0,
                KillfeedAllowanceSeconds = 0,
                AudioTailAllowanceSeconds = 1.5,
                MinimumClipDurationSeconds = 7
            },
            25);

        Assert.Equal(206, result.SafeEndTick);
        Assert.Equal(548, result.PlannedEndTick);
    }

    [Theory]
    [InlineData(0, 70)]
    [InlineData(80, 80)]
    [InlineData(95, 95)]
    public void WarmupNeverCrossesRoundOrStart(long roundStart, long expected)
    {
        RenderSegment segment = new(100, 200)
        {
            TickRate = 10,
            RoundStartTick = roundStart
        };

        long result = NetConsoleDemoController.ComputeWarmupTick(
            segment,
            new RenderWarmupOptions { WarmupGameSeconds = 3 });

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("[blackdetect] black_start:0 black_end:1.2", true)]
    [InlineData("[freezedetect] freeze_start:0", true)]
    [InlineData("[blackdetect] black_start:3.2 black_end:4", false)]
    [InlineData("normal ffmpeg output", false)]
    public void ClipStartQualityRecognizesOnlyOpeningDefects(
        string diagnostic,
        bool expected)
    {
        Assert.Equal(expected, RenderOutputWatcher.HasStartDefect(diagnostic));
    }
}
