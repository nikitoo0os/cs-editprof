using Cs2Highlight.Analysis;

namespace Cs2Highlight.Music;

public interface IHighlightImportanceCalculator
{
    HighlightImportance Calculate(HighlightCandidate highlight, int selectionOrder = 0);
}

public sealed class HighlightImportanceCalculator : IHighlightImportanceCalculator
{
    public HighlightImportance Calculate(HighlightCandidate highlight, int selectionOrder = 0)
    {
        Dictionary<string, double> values = new(StringComparer.Ordinal)
        {
            ["type"] = highlight.Type switch
            {
                HighlightType.Ace => 4.0,
                HighlightType.QuadKill => 3.0,
                HighlightType.TripleKill => 2.2,
                HighlightType.DoubleKill => 1.6,
                _ => 1.0
            }
        };
        Add(values, "beauty", Math.Min(0.8, highlight.BeautyScore / 100d));
        Add(values, "headshots", Math.Min(0.5, highlight.HeadshotCount * 0.1));
        Add(values, "wallbang", highlight.Kills.Any(value => value.Wallbang == true) ? 0.25 : 0);
        Add(values, "oneTap", highlight.Kills.Any(value => value.OneTap == true) ? 0.20 : 0);
        Add(values, "specialWeapon", highlight.Kills.Any(value =>
            value.WeaponCode is "knife" or "taser") ? 0.30 : 0);
        Add(values, "lowHp", highlight.Tags.Contains("LOW_HP", StringComparer.Ordinal) ? 0.15 : 0);
        Add(values, "roundEnding", highlight.Tags.Contains(
            "ROUND_ENDING_KILL", StringComparer.Ordinal) ? 0.20 : 0);
        Add(values, "weaponSwap", highlight.WeaponSequence.Any(value => value.SwapBefore) ? 0.10 : 0);
        Add(values, "selectionOrder", selectionOrder > 0 ? 0.2 / selectionOrder : 0);
        return new HighlightImportance(values.Values.Sum(), values);
    }

    private static void Add(Dictionary<string, double> values, string key, double value)
    {
        if (value > 0) values[key] = value;
    }
}

public interface IMusicalAnchorBuilder
{
    IReadOnlyList<MusicalAnchor> Build(MusicAnalysis analysis);
}

public sealed class MusicalAnchorBuilder : IMusicalAnchorBuilder
{
    public IReadOnlyList<MusicalAnchor> Build(MusicAnalysis analysis)
    {
        List<MusicalAnchor> anchors = [];
        anchors.AddRange(analysis.DropCandidates.Select(value => new MusicalAnchor(
            $"drop-{value.Index:D3}", MusicalAnchorType.Drop, value.TimeSeconds,
            Clamp(value.Score), Clamp(value.Confidence ?? 0.5))));
        anchors.AddRange(analysis.Downbeats.Select(value => new MusicalAnchor(
            $"downbeat-{value.Index:D4}", MusicalAnchorType.Downbeat, value.TimeSeconds,
            Clamp(value.Strength), Clamp(value.Confidence ?? 0.5))));
        anchors.AddRange(analysis.Sections.Skip(1).Select(value => new MusicalAnchor(
            $"section-{value.Index:D3}", MusicalAnchorType.SectionBoundary, value.StartSeconds,
            Clamp(value.Energy), 0.65)));
        anchors.AddRange(analysis.Onsets.Where(value => value.Strength >= 0.65).Select(value =>
            new MusicalAnchor(
                $"onset-{value.Index:D4}", MusicalAnchorType.Onset, value.TimeSeconds,
                Clamp(value.Strength), 0.6)));
        anchors.AddRange(analysis.Beats.Select(value => new MusicalAnchor(
            $"beat-{value.Index:D4}",
            value.Strength >= 0.7 ? MusicalAnchorType.StrongBeat : MusicalAnchorType.Beat,
            value.TimeSeconds,
            Clamp(value.Strength),
            Clamp(value.Confidence ?? 0.5))));
        return anchors
            .Where(value => value.TimeSeconds >= 0 &&
                value.TimeSeconds <= analysis.Audio.DurationSeconds)
            .GroupBy(value => Math.Round(value.TimeSeconds, 3))
            .Select(group => group
                .OrderByDescending(value => Rank(value.Type))
                .ThenByDescending(value => value.Strength)
                .ThenBy(value => value.Id, StringComparer.Ordinal)
                .First())
            .OrderBy(value => value.TimeSeconds)
            .ThenByDescending(value => Rank(value.Type))
            .ThenBy(value => value.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static int Rank(MusicalAnchorType type) => type switch
    {
        MusicalAnchorType.Drop => 6,
        MusicalAnchorType.Downbeat => 5,
        MusicalAnchorType.SectionBoundary => 4,
        MusicalAnchorType.StrongBeat => 3,
        MusicalAnchorType.Onset => 2,
        _ => 1
    };

    private static double Clamp(double value) => Math.Clamp(value, 0, 1);
}

public interface ITimeWarpPlanner
{
    TimeWarpPlan Create(
        SafeClipBounds timing,
        MusicalAnchor? targetAnchor,
        double outputStartSeconds,
        MusicSyncIntensity intensity,
        TimeWarpOptions options);
}

public sealed class TimeWarpPlanner : ITimeWarpPlanner
{
    public TimeWarpPlan Create(
        SafeClipBounds timing,
        MusicalAnchor? targetAnchor,
        double outputStartSeconds,
        MusicSyncIntensity intensity,
        TimeWarpOptions options)
    {
        double sourceStart = timing.SafeStartSeconds;
        double sourceEnd = Math.Max(timing.SafeEndSeconds, timing.PlannedEndSeconds);
        double duration = sourceEnd - sourceStart;
        double killOffset = timing.PrimaryKillSeconds - sourceStart;
        if (duration <= 0 || killOffset < 0)
            return new TimeWarpPlan(1, [], false, ["INVALID_SAFE_CLIP_BOUNDS"]);
        if (targetAnchor is null)
            return Constant(1, duration, ["NATURAL_TIMING_FALLBACK"]);

        double targetOffset = targetAnchor.TimeSeconds - outputStartSeconds;
        if (targetOffset <= 0)
            return Constant(1, duration, ["ANCHOR_BEFORE_CLIP_FALLBACK"]);
        double requested = killOffset / targetOffset;
        (double minimum, double maximum) = intensity switch
        {
            MusicSyncIntensity.Soft =>
                (options.SoftMinimumSpeed, options.SoftMaximumSpeed),
            MusicSyncIntensity.Expressive =>
                (options.ExpressiveMinimumBaseSpeed, options.ExpressiveMaximumBaseSpeed),
            _ => (options.AggressiveMinimumRampSpeed, options.AggressiveMaximumRampSpeed)
        };
        if (requested < minimum || requested > maximum)
            return Constant(1, duration, ["EXCESSIVE_TIME_WARP_FALLBACK"]);

        double speed = Math.Clamp(requested, minimum, maximum);
        return Constant(speed, duration, []);
    }

    private static TimeWarpPlan Constant(
        double speed,
        double duration,
        IReadOnlyList<string> warnings) =>
        new(speed, [new TimeWarpSegment(0, duration, speed)], false, warnings);
}

public interface IMusicEditPlanner
{
    MusicEditPlan Create(
        string generationId,
        string musicFile,
        MusicAnalysis music,
        IReadOnlyList<SelectedHighlight> highlights,
        MusicEditOptions options);
}

public sealed class MusicEditPlanner(
    IMusicalAnchorBuilder anchorBuilder,
    IHighlightImportanceCalculator importanceCalculator,
    ITimeWarpPlanner timeWarpPlanner) : IMusicEditPlanner
{
    public MusicEditPlan Create(
        string generationId,
        string musicFile,
        MusicAnalysis music,
        IReadOnlyList<SelectedHighlight> highlights,
        MusicEditOptions options)
    {
        if (music.SchemaVersion != "1.0")
            throw new InvalidOperationException("UNSUPPORTED_MUSIC_ANALYSIS_SCHEMA");
        SelectedHighlight[] ordered = highlights
            .OrderBy(value => value.SelectionOrder)
            .ThenBy(value => value.Highlight.FirstKillTick)
            .ThenBy(value => value.Id, StringComparer.Ordinal)
            .ToArray();
        MusicalAnchor[] anchors = anchorBuilder.Build(music).ToArray();
        List<BeamState> beam = [new(0, 0, 0, [])];
        foreach (SelectedHighlight selected in ordered)
        {
            List<BeamState> next = [];
            foreach (BeamState state in beam)
            {
                HighlightImportance importance =
                    importanceCalculator.Calculate(selected.Highlight, selected.SelectionOrder);
                MusicalAnchor[] candidates = anchors
                    .Skip(state.NextAnchorIndex)
                    .Where(value => value.TimeSeconds > state.CursorSeconds)
                    .Take(Math.Max(1, options.MaximumAnchorsPerStep))
                    .ToArray();
                foreach (MusicalAnchor anchor in candidates)
                {
                    TimeWarpPlan warp = timeWarpPlanner.Create(
                        selected.Bounds,
                        anchor,
                        state.CursorSeconds,
                        options.SyncIntensity,
                        new TimeWarpOptions());
                    if (warp.Warnings.Contains("EXCESSIVE_TIME_WARP_FALLBACK", StringComparer.Ordinal))
                        continue;
                    double duration = SourceDuration(selected.Bounds) / warp.BaseSpeedFactor;
                    if (state.CursorSeconds + duration > music.Audio.DurationSeconds)
                        continue;
                    MusicEditScoreBreakdown score = Score(
                        selected.Highlight, importance.Total, anchor, warp.BaseSpeedFactor, options);
                    int anchorIndex = Array.IndexOf(anchors, anchor);
                    MusicEditSegment segment = Segment(
                        state.Segments.Count + 1,
                        selected,
                        importance.Total,
                        anchor,
                        state.CursorSeconds,
                        warp,
                        score,
                        options.Style);
                    next.Add(new BeamState(
                        state.Score + score.Total,
                        state.CursorSeconds + duration,
                        anchorIndex + 1,
                        [.. state.Segments, segment]));
                }

                TimeWarpPlan fallback = timeWarpPlanner.Create(
                    selected.Bounds, null, state.CursorSeconds,
                    options.SyncIntensity, new TimeWarpOptions());
                double fallbackDuration = SourceDuration(selected.Bounds);
                if (state.CursorSeconds + fallbackDuration <= music.Audio.DurationSeconds)
                {
                    MusicEditScoreBreakdown score = new(0, 0, options.ChronologyBonus, 0, 1, -0.75);
                    next.Add(new BeamState(
                        state.Score + score.Total,
                        state.CursorSeconds + fallbackDuration,
                        state.NextAnchorIndex,
                        [.. state.Segments, Segment(
                            state.Segments.Count + 1, selected, importance.Total, null,
                            state.CursorSeconds, fallback, score, options.Style)]));
                }
            }
            beam = next
                .OrderByDescending(value => value.Score)
                .ThenBy(value => value.CursorSeconds)
                .ThenBy(value => string.Join('|', value.Segments.Select(segment =>
                    segment.TargetMusicAnchor?.Id ?? "~")), StringComparer.Ordinal)
                .Take(Math.Max(1, options.BeamWidth))
                .ToList();
            if (beam.Count == 0)
                throw new InvalidOperationException("MUSIC_TOO_SHORT_FOR_SELECTION");
        }
        BeamState best = beam[0];
        string[] warnings = best.Segments
            .SelectMany(value => value.Warnings.Concat(value.TimeWarp.Warnings))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        return new MusicEditPlan(
            "1.0", generationId, musicFile, music.Audio.DurationSeconds,
            options.Style, options.SyncIntensity, best.Segments, warnings);
    }

    private static MusicEditSegment Segment(
        int index,
        SelectedHighlight selected,
        double importance,
        MusicalAnchor? anchor,
        double outputStart,
        TimeWarpPlan warp,
        MusicEditScoreBreakdown score,
        MovieStyle style)
    {
        double sourceStart = selected.Bounds.SafeStartSeconds;
        double primaryOffset =
            (selected.Bounds.PrimaryKillSeconds - sourceStart) / warp.BaseSpeedFactor;
        return new MusicEditSegment(
            index,
            selected.Id,
            selected.Highlight.Type,
            importance,
            sourceStart,
            Math.Max(selected.Bounds.SafeEndSeconds, selected.Bounds.PlannedEndSeconds),
            selected.Bounds.PrimaryKillSeconds,
            anchor,
            outputStart,
            outputStart + primaryOffset,
            warp,
            style == MovieStyle.Cinematic ? "Fade" : "Cut",
            "Cut",
            score,
            anchor is null ? ["MUSICAL_ANCHOR_FALLBACK"] : []);
    }

    private static MusicEditScoreBreakdown Score(
        HighlightCandidate highlight,
        double importance,
        MusicalAnchor anchor,
        double speed,
        MusicEditOptions options)
    {
        double importanceAnchor = importance * anchor.Strength * anchor.Confidence;
        double compatibility = Compatibility(highlight.Type, anchor.Type);
        double speedPenalty = Math.Abs(speed - 1) * options.SpeedAdjustmentPenaltyWeight;
        double weakPenalty = (1 - anchor.Strength) * options.WeakAnchorPenaltyWeight;
        double total = importanceAnchor + compatibility + options.ChronologyBonus -
            speedPenalty - weakPenalty;
        return new(
            importanceAnchor,
            compatibility,
            options.ChronologyBonus,
            speedPenalty,
            weakPenalty,
            total);
    }

    private static double Compatibility(HighlightType highlight, MusicalAnchorType anchor) =>
        (highlight, anchor) switch
        {
            (HighlightType.Ace, MusicalAnchorType.Drop) => 2,
            (HighlightType.QuadKill, MusicalAnchorType.Drop or MusicalAnchorType.Downbeat) => 1.5,
            (HighlightType.TripleKill, MusicalAnchorType.Downbeat or MusicalAnchorType.StrongBeat) => 1.2,
            (HighlightType.DoubleKill, MusicalAnchorType.StrongBeat) => 0.8,
            (HighlightType.SoloKill, MusicalAnchorType.StrongBeat or MusicalAnchorType.Onset) => 0.6,
            (_, MusicalAnchorType.Beat) => 0.1,
            _ => 0
        };

    private static double SourceDuration(SafeClipBounds bounds) =>
        Math.Max(bounds.SafeEndSeconds, bounds.PlannedEndSeconds) - bounds.SafeStartSeconds;

    private sealed record BeamState(
        double Score,
        double CursorSeconds,
        int NextAnchorIndex,
        IReadOnlyList<MusicEditSegment> Segments);
}
