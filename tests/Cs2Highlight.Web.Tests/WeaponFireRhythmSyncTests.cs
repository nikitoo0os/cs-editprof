using Cs2Highlight.Music;
using Cs2Highlight.Web.Services;

namespace Cs2Highlight.Web.Tests;

public sealed class WeaponFireRhythmSyncTests
{
    [Fact]
    public void SignificantShotsSnapToAccentsWithoutMovingKillOrDuration()
    {
        TimeWarpPlan baseline = new(
            1,
            [
                new TimeWarpSegment(0, 2, 1),
                new TimeWarpSegment(2, 3, 1)
            ],
            true,
            ["BASELINE"]);

        TimeWarpPlan synced = WeaponFireRhythmSync.Apply(
            baseline,
            3,
            2,
            10,
            12,
            13,
            [0.58, 1.18, 1.78],
            [10.6, 11.2, 11.8]);

        Assert.Contains("WEAPON_FIRE_MUSIC_ACCENT_SYNC", synced.Warnings);
        Assert.Equal(10.6, 10 + TimeWarpMath.MapSourceTime(synced, 0.58), 6);
        Assert.Equal(11.2, 10 + TimeWarpMath.MapSourceTime(synced, 1.18), 6);
        Assert.Equal(11.8, 10 + TimeWarpMath.MapSourceTime(synced, 1.78), 6);
        Assert.Equal(12, 10 + TimeWarpMath.MapSourceTime(synced, 2), 6);
        Assert.Equal(13, 10 + TimeWarpMath.MapSourceTime(synced, 3), 6);
    }

    [Fact]
    public void UnsafeSnapKeepsBaseline()
    {
        TimeWarpPlan baseline = new(1, [], false, []);

        TimeWarpPlan synced = WeaponFireRhythmSync.Apply(
            baseline,
            3,
            2,
            0,
            2,
            3,
            [0.2],
            [0.41]);

        Assert.Same(baseline, synced);
    }
}
