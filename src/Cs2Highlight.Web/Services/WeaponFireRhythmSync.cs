using Cs2Highlight.Music;

namespace Cs2Highlight.Web.Services;

public static class WeaponFireRhythmSync
{
    private const double MaximumSnapDistanceSeconds = 0.22;
    private const double MinimumAnchorSpacingSeconds = 0.04;
    private const double MinimumSpeed = 0.50;
    private const double MaximumSpeed = 1.30;
    private const int MaximumSyncedShots = 4;

    public static TimeWarpPlan Apply(
        TimeWarpPlan baseline,
        double sourceDurationSeconds,
        double killSourceSeconds,
        double outputStartSeconds,
        double killOutputSeconds,
        double outputEndSeconds,
        IReadOnlyList<double> weaponFireSourceSeconds,
        IReadOnlyList<double> musicalAccentOutputSeconds)
    {
        if (sourceDurationSeconds <= 0 ||
            killSourceSeconds <= 0 ||
            killSourceSeconds >= sourceDurationSeconds ||
            killOutputSeconds <= outputStartSeconds ||
            killOutputSeconds >= outputEndSeconds)
        {
            return baseline;
        }

        List<SyncAnchor> anchors =
        [
            new(0, outputStartSeconds),
            new(killSourceSeconds, killOutputSeconds),
            new(sourceDurationSeconds, outputEndSeconds)
        ];
        double[] accents = musicalAccentOutputSeconds
            .Where(value =>
                value > outputStartSeconds + MinimumAnchorSpacingSeconds &&
                value < outputEndSeconds - MinimumAnchorSpacingSeconds &&
                Math.Abs(value - killOutputSeconds) >=
                    MinimumAnchorSpacingSeconds)
            .DistinctBy(value => Math.Round(value, 3))
            .OrderBy(value => value)
            .ToArray();
        if (accents.Length == 0)
            return baseline;

        double[] significantShots = ClusterShots(weaponFireSourceSeconds)
            .Where(value =>
                value > MinimumAnchorSpacingSeconds &&
                value < sourceDurationSeconds - MinimumAnchorSpacingSeconds &&
                Math.Abs(value - killSourceSeconds) >=
                    MinimumAnchorSpacingSeconds)
            .OrderBy(value => Math.Abs(value - killSourceSeconds))
            .Take(MaximumSyncedShots)
            .ToArray();
        HashSet<double> usedAccents = [];
        foreach (double shot in significantShots)
        {
            double baselineOutput = outputStartSeconds +
                TimeWarpMath.MapSourceTime(baseline, shot);
            foreach (double accent in accents
                         .Where(value => !usedAccents.Contains(value))
                         .OrderBy(value => Math.Abs(value - baselineOutput)))
            {
                if (Math.Abs(accent - baselineOutput) >
                    MaximumSnapDistanceSeconds)
                    break;
                List<SyncAnchor> candidate =
                [.. anchors, new SyncAnchor(shot, accent)];
                candidate.Sort((left, right) =>
                    left.SourceSeconds.CompareTo(right.SourceSeconds));
                if (!IsSafe(candidate))
                    continue;
                anchors = candidate;
                usedAccents.Add(accent);
                break;
            }
        }
        if (anchors.Count == 3)
            return baseline;

        TimeWarpSegment[] segments = anchors
            .Zip(anchors.Skip(1))
            .Select(pair => new TimeWarpSegment(
                pair.First.SourceSeconds,
                pair.Second.SourceSeconds,
                (pair.Second.SourceSeconds - pair.First.SourceSeconds) /
                (pair.Second.OutputSeconds - pair.First.OutputSeconds)))
            .ToArray();
        return new TimeWarpPlan(
            1,
            segments,
            true,
            baseline.Warnings
                .Append("WEAPON_FIRE_MUSIC_ACCENT_SYNC")
                .Distinct(StringComparer.Ordinal)
                .ToArray());
    }

    private static List<double> ClusterShots(
        IReadOnlyList<double> source)
    {
        double[] ordered = source
            .Where(double.IsFinite)
            .DistinctBy(value => Math.Round(value, 4))
            .OrderBy(value => value)
            .ToArray();
        if (ordered.Length == 0)
            return [];
        List<double> result = [];
        double selected = ordered[0];
        foreach (double shot in ordered.Skip(1))
        {
            if (shot - selected < 0.16)
            {
                selected = shot;
                continue;
            }
            result.Add(selected);
            selected = shot;
        }
        result.Add(selected);
        return result;
    }

    private static bool IsSafe(IReadOnlyList<SyncAnchor> anchors)
    {
        foreach ((SyncAnchor first, SyncAnchor second) in
                 anchors.Zip(anchors.Skip(1)))
        {
            double sourceDuration =
                second.SourceSeconds - first.SourceSeconds;
            double outputDuration =
                second.OutputSeconds - first.OutputSeconds;
            if (sourceDuration < MinimumAnchorSpacingSeconds ||
                outputDuration < MinimumAnchorSpacingSeconds)
                return false;
            double speed = sourceDuration / outputDuration;
            if (speed is < MinimumSpeed or > MaximumSpeed)
                return false;
        }
        return true;
    }

    private sealed record SyncAnchor(
        double SourceSeconds,
        double OutputSeconds);
}
