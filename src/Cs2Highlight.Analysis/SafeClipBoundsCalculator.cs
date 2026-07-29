namespace Cs2Highlight.Analysis;

public sealed record SafeClipTimingRequest(
    HighlightType HighlightType,
    long PlannedStartTick,
    long PrimaryKillTick,
    long LastKillTick,
    long? RoundEndTick,
    bool RoundEnding,
    long DemoDurationTicks,
    int TickRate,
    double RequestedPostRollSeconds);

public static class SafeClipBoundsCalculator
{
    public static (SafeClipBounds Bounds, long SafeEndTick, long PlannedEndTick) Calculate(
        SafeClipTimingRequest request,
        SafeClipTimingOptions options,
        double maximumClipDurationSeconds)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        if (request.TickRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Tick rate must be positive.");
        if (request.DemoDurationTicks <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Demo duration must be positive.");

        long start = Math.Clamp(request.PlannedStartTick, 0, request.DemoDurationTicks);
        double typeHold = request.HighlightType == HighlightType.SoloKill
            ? options.SoloPostKillHoldSeconds
            : options.MultikillPostKillHoldSeconds;
        typeHold = Math.Max(typeHold, request.RequestedPostRollSeconds);
        if (request.RoundEnding)
            typeHold = Math.Max(typeHold, options.RoundEndingPostKillHoldSeconds);

        double requiredTail = new[]
        {
            typeHold,
            options.DeathAnimationAllowanceSeconds,
            options.KillfeedAllowanceSeconds,
            options.AudioTailAllowanceSeconds
        }.Max();
        long safeEnd = request.LastKillTick + ToTicks(requiredTail, request.TickRate);
        safeEnd = Math.Clamp(safeEnd, start, request.DemoDurationTicks);

        long minimumEnd = start + ToTicks(options.MinimumClipDurationSeconds, request.TickRate);
        long maximumEnd = start + ToTicks(
            Math.Max(options.MinimumClipDurationSeconds, maximumClipDurationSeconds),
            request.TickRate);
        long plannedEnd = Math.Max(safeEnd, minimumEnd);
        if (plannedEnd > maximumEnd && maximumEnd >= safeEnd)
            plannedEnd = maximumEnd;
        plannedEnd = Math.Clamp(plannedEnd, safeEnd, request.DemoDurationTicks);

        double Seconds(long tick) => tick / (double)request.TickRate;
        SafeClipBounds bounds = new(
            Seconds(start),
            Seconds(start),
            Seconds(request.PrimaryKillTick),
            Seconds(request.LastKillTick),
            Seconds(safeEnd),
            Seconds(plannedEnd));
        return (bounds, safeEnd, plannedEnd);
    }

    private static long ToTicks(double seconds, int tickRate) =>
        checked((long)Math.Round(
            Math.Max(0, seconds) * tickRate,
            MidpointRounding.AwayFromZero));
}
