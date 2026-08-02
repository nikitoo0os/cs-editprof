using Cs2Highlight.Web.Services;

namespace Cs2Highlight.Web.Tests;

public sealed class MusicSelectionCapacityPolicyTests
{
    [Fact]
    public void CalculatesMaximumCountAndMinimumRequiredRemovals()
    {
        MusicSelectionCapacity capacity =
            MusicSelectionCapacityPolicy.Calculate(
                [4_000, 5_000, 6_000, 9_000],
                [4_000, 5_000, 6_000, 9_000],
                12_000,
                300);

        Assert.Equal(15_600, capacity.MaximumTimelineMilliseconds);
        Assert.Equal(3, capacity.MaximumCount);
        Assert.Equal(1, capacity.RequiredRemovalCount);
    }

    [Fact]
    public void NoMusicOverflowRequiresNoRemoval()
    {
        MusicSelectionCapacity capacity =
            MusicSelectionCapacityPolicy.Calculate(
                [3_000, 4_000, 5_000],
                [4_000, 5_000],
                10_000,
                300);

        Assert.Equal(0, capacity.RequiredRemovalCount);
        Assert.Equal(3, capacity.MaximumCount);
    }
}
