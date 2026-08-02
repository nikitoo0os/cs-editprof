namespace Cs2Highlight.Web.Services;

public sealed record MusicSelectionCapacity(
    long MaximumTimelineMilliseconds,
    int MaximumCount,
    int RequiredRemovalCount,
    long SelectedTimelineMilliseconds);

public static class MusicSelectionCapacityPolicy
{
    public const double MaximumRetimingRatio = 1.30;

    public static MusicSelectionCapacity Calculate(
        IEnumerable<long> availableDurationsMilliseconds,
        IEnumerable<long> selectedDurationsMilliseconds,
        long musicDurationMilliseconds,
        int transitionDurationMilliseconds)
    {
        long maximum = Math.Max(
            0,
            (long)Math.Floor(
                musicDurationMilliseconds * MaximumRetimingRatio));
        long[] available = availableDurationsMilliseconds
            .Select(value => Math.Max(0, value))
            .OrderBy(value => value)
            .ToArray();
        long[] selected = selectedDurationsMilliseconds
            .Select(value => Math.Max(0, value))
            .OrderByDescending(value => value)
            .ToArray();
        int maximumCount = 0;
        for (int count = 1; count <= available.Length; count++)
        {
            if (TimelineDuration(
                    available.Take(count),
                    transitionDurationMilliseconds) > maximum)
            {
                break;
            }
            maximumCount = count;
        }
        long selectedDuration = TimelineDuration(
            selected,
            transitionDurationMilliseconds);
        int removals = 0;
        while (removals < selected.Length &&
               TimelineDuration(
                   selected.Skip(removals),
                   transitionDurationMilliseconds) > maximum)
        {
            removals++;
        }
        return new MusicSelectionCapacity(
            maximum,
            maximumCount,
            removals,
            selectedDuration);
    }

    private static long TimelineDuration(
        IEnumerable<long> durations,
        int transitionDurationMilliseconds)
    {
        long[] values = durations.ToArray();
        if (values.Length == 0)
            return 0;
        return Math.Max(
            0,
            values.Sum() -
            Math.Max(0, values.Length - 1) *
            Math.Max(0, transitionDurationMilliseconds));
    }
}

public sealed class MusicSelectionCapacityException(
    MusicSelectionCapacity capacity)
    : InvalidOperationException("MUSIC_TOO_SHORT_FOR_SELECTION")
{
    public MusicSelectionCapacity Capacity { get; } = capacity;
}
