using Cs2Highlight.Analysis;

namespace Cs2Highlight.Music;

public interface ICinematicDirector
{
    CinematicMoviePlan Create(
        MusicNarrative music,
        MusicExcerptPlan excerpt,
        IReadOnlyList<SelectedHighlight> highlights,
        IReadOnlyList<BrollCandidate> broll,
        CinematicDirectorOptions options);
}

public sealed class CinematicDirector(
    IHighlightPeakMatcher matcher,
    ICameraPathPlanner cameraPlanner,
    ICinematicTimeWarpPolicy timeWarp,
    IMotivatedEffectPlanner effects,
    ISoundDesignPlanner sound,
    IColorNarrativePlanner color,
    ICinematicDurationPolicy durationPolicy) : ICinematicDirector
{
    public const string SchemaVersion = "1.0";
    public const string PlannerVersion = "8.0";

    public CinematicMoviePlan Create(
        MusicNarrative music,
        MusicExcerptPlan excerpt,
        IReadOnlyList<SelectedHighlight> highlights,
        IReadOnlyList<BrollCandidate> broll,
        CinematicDirectorOptions options)
    {
        if (!excerpt.IsValid)
            throw new InvalidOperationException("CINEMATIC_EXCERPT_INVALID");
        MusicSection[] sections = music.Sections
            .Where(value => excerpt.SectionIds.Contains(
                value.Id,
                StringComparer.Ordinal))
            .OrderBy(value => value.StartSeconds)
            .ToArray();
        HighlightPeakMatchPlan matching = matcher.Match(
            highlights,
            excerpt,
            new HighlightPeakMatchingOptions());
        Dictionary<string, SelectedHighlight> highlightById =
            highlights.ToDictionary(value => value.Id, StringComparer.Ordinal);
        Dictionary<string, MusicSection> sectionById =
            sections.ToDictionary(value => value.Id, StringComparer.Ordinal);
        MovieDurationBudget duration = durationPolicy.Calculate(
            highlights,
            options.Duration);
        List<CinematicSequenceSegment> segments = [];
        List<string> warnings =
        [
            .. excerpt.Warnings,
            .. matching.Warnings
        ];
        bool relaxedEnergy = excerpt.Warnings.Contains(
            MusicExcerptSelector.RelaxedEnergyFallbackWarning,
            StringComparer.Ordinal);
        HighlightPeakMatch[] matches = matching.Matches
            .Where(value =>
                sectionById.TryGetValue(value.Peak.SectionId, out MusicSection? section) &&
                MusicalPeakDetector.IsAllowedPrimaryKillSection(
                    section.Type,
                    relaxedEnergy))
            .OrderBy(value => value.PlannedPeakSeconds)
            .ToArray();
        if (relaxedEnergy)
        {
            HighlightPeakMatch[] timelineSafe = CreateTimelineSafeMatches(
                highlights,
                excerpt,
                sectionById,
                matching,
                options);
            if (timelineSafe.Length == highlights.Count)
            {
                matches = timelineSafe;
                warnings.Add("HIGHLIGHT_PEAK_TIMELINE_FALLBACK");
            }
        }
        List<HighlightPeakMatch> effectiveMatches = [];
        double highlightCursor = 0;
        for (int index = 0; index < matches.Length; index++)
        {
            HighlightPeakMatch match = matches[index];
            SelectedHighlight highlight = highlightById[match.HighlightId];
            MusicSection section = sectionById[match.Peak.SectionId];
            double sourceDuration = Math.Max(
                0.001,
                highlight.Bounds.SafeEndSeconds -
                highlight.Bounds.SafeStartSeconds);
            double killOffset = Math.Clamp(
                highlight.Bounds.PrimaryKillSeconds -
                highlight.Bounds.SafeStartSeconds,
                0,
                sourceDuration);
            double outputStart = Math.Max(
                0,
                match.PlannedPeakSeconds - killOffset);
            if (outputStart < highlightCursor - 0.001)
            {
                warnings.Add(
                    $"HIGHLIGHT_PEAK_SPACING_INSUFFICIENT:{highlight.Id}");
                continue;
            }
            TimeWarpPlan warp = timeWarp.Create(
                highlight,
                match,
                outputStart,
                options.TimeWarp);
            double outputDuration = TimeWarpMath.OutputDuration(
                warp,
                sourceDuration);
            double outputEnd = outputStart + outputDuration;
            if (outputEnd > excerpt.DurationSeconds + 0.001)
            {
                warnings.Add(
                    $"HIGHLIGHT_EXCEEDS_MUSIC_EXCERPT:{highlight.Id}");
                continue;
            }
            if (outputEnd - outputStart < 0.25)
            {
                warnings.Add(
                    $"HIGHLIGHT_SEGMENT_TOO_SHORT:{highlight.Id}");
                continue;
            }
            CameraShotPlan camera = HighlightPov(highlight);
            double actualKill = outputStart + TimeWarpMath.MapSourceTime(
                warp,
                killOffset);
            HighlightPeakMatch effectiveMatch = match with
            {
                PlannedKillSeconds = actualKill,
                AlignmentErrorMilliseconds =
                    Math.Abs(actualKill - match.PlannedPeakSeconds) * 1000
            };
            effectiveMatches.Add(effectiveMatch);
            HighlightPeakMatch localMatch = effectiveMatch with
            {
                PlannedPeakSeconds =
                    effectiveMatch.PlannedPeakSeconds - outputStart,
                PlannedKillSeconds = actualKill - outputStart
            };
            bool final = index == matches.Length - 1;
            CinematicSequenceRole role = final ||
                highlight.Highlight.Type is
                    HighlightType.Ace or
                    HighlightType.QuadKill
                    ? CinematicSequenceRole.PeakHighlight
                    : CinematicSequenceRole.Highlight;
            segments.Add(new CinematicSequenceSegment
            {
                Id = $"segment-highlight-{index + 1:D3}",
                Role = role,
                OutputStartSeconds = outputStart,
                OutputEndSeconds = outputEnd,
                MusicSectionId = section.Id,
                HighlightId = highlight.Id,
                Camera = camera,
                TimeWarp = warp,
                Effects = effects.Plan(
                    role,
                    section,
                    localMatch,
                    camera,
                    outputEnd - outputStart,
                    options.Effects,
                    final)
            });
            highlightCursor = outputEnd;
        }
        CinematicSequenceSegment? finalHighlightSegment = segments
            .Where(value => value.HighlightId is not null)
            .OrderBy(value => value.OutputStartSeconds)
            .LastOrDefault();
        if (finalHighlightSegment is not null)
        {
            SelectedHighlight finalHighlight =
                highlightById[finalHighlightSegment.HighlightId!];
            MusicSection finalSection =
                sectionById[finalHighlightSegment.MusicSectionId];
            HighlightPeakMatch finalMatch = effectiveMatches.Single(value =>
                value.HighlightId == finalHighlight.Id);
            HighlightPeakMatch localFinalMatch = finalMatch with
            {
                PlannedPeakSeconds =
                    finalMatch.PlannedPeakSeconds -
                    finalHighlightSegment.OutputStartSeconds,
                PlannedKillSeconds =
                    finalMatch.PlannedKillSeconds -
                    finalHighlightSegment.OutputStartSeconds
            };
            int finalIndex = segments.IndexOf(finalHighlightSegment);
            segments[finalIndex] = finalHighlightSegment with
            {
                Role = CinematicSequenceRole.PeakHighlight,
                Effects = effects.Plan(
                    CinematicSequenceRole.PeakHighlight,
                    finalSection,
                    localFinalMatch,
                    finalHighlightSegment.Camera,
                    finalHighlightSegment.OutputEndSeconds -
                    finalHighlightSegment.OutputStartSeconds,
                    options.Effects,
                    finalHighlight: true)
            };
        }
        double targetDuration = Math.Max(
            segments
                .Where(value => value.HighlightId is not null)
                .Select(value => value.OutputEndSeconds)
                .DefaultIfEmpty(0)
                .Max(),
            Math.Min(excerpt.DurationSeconds, duration.TargetSeconds));
        AddBrollSegments(
            segments,
            sections,
            broll,
            excerpt,
            options,
            duration,
            targetDuration,
            warnings);
        CinematicSequenceSegment[] ordered = segments
            .OrderBy(value => value.OutputStartSeconds)
            .ThenBy(value => value.Role)
            .ThenBy(value => value.Id, StringComparer.Ordinal)
            .ToArray();
        int highFpsShots = 0;
        ordered = ordered.Select(value =>
        {
            if (!value.Camera.RequiresHighFpsCapture)
                return value;
            highFpsShots++;
            if (highFpsShots <=
                options.Capture.MaximumHighFpsShotsPerMovie)
                return value;
            warnings.Add(
                $"HIGH_FPS_SHOT_BUDGET_EXCEEDED:{value.Camera.Id}");
            return value with
            {
                Camera = value.Camera with
                {
                    RequiresHighFpsCapture = false
                }
            };
        }).ToArray();
        if (ordered
            .Where(value => value.HighlightId is not null)
            .Any(value => !MusicalPeakDetector.IsAllowedPrimaryKillSection(
                sectionById[value.MusicSectionId].Type,
                relaxedEnergy)))
        {
            throw new InvalidOperationException(
                "PRIMARY_KILL_OUTSIDE_HIGH_ENERGY_SECTION");
        }
        if (!ordered.Any(value => value.BrollCandidateId is not null))
            warnings.Add("CINEMATIC_BROLL_UNAVAILABLE");
        if (ordered
            .Where(value => value.BrollCandidateId is not null)
            .Sum(value => value.OutputEndSeconds - value.OutputStartSeconds) >
            duration.MaximumBrollSeconds + 0.001)
        {
            if (relaxedEnergy)
            {
                warnings.Add(
                    "CINEMATIC_BROLL_RATIO_RELAXED_FOR_CONTINUITY");
            }
            else
            {
                throw new InvalidOperationException(
                    "CINEMATIC_BROLL_RATIO_EXCEEDED");
            }
        }
        return new CinematicMoviePlan
        {
            SchemaVersion = SchemaVersion,
            PlannerVersion = PlannerVersion,
            GenerationId = options.GenerationId,
            MusicExcerpt = excerpt,
            TargetDurationSeconds = Math.Min(
                targetDuration,
                duration.MaximumTotalSeconds),
            Segments = ordered,
            HighlightMatches = effectiveMatches,
            SoundDesign = sound.Create(sections),
            Color = color.Create(sections, options.ColorGrade),
            Warnings = warnings.Distinct(StringComparer.Ordinal).ToArray()
        };
    }

    private HighlightPeakMatch[] CreateTimelineSafeMatches(
        IReadOnlyList<SelectedHighlight> highlights,
        MusicExcerptPlan excerpt,
        Dictionary<string, MusicSection> sections,
        HighlightPeakMatchPlan original,
        CinematicDirectorOptions options)
    {
        MusicalPeak[] available = excerpt.Peaks
            .Where(value =>
                value.Strength >= 0.45 &&
                value.Confidence >= 0.40 &&
                sections.TryGetValue(
                    value.SectionId,
                    out MusicSection? section) &&
                MusicalPeakDetector.IsAllowedPrimaryKillSection(
                    section.Type,
                    relaxedEnergy: true))
            .OrderBy(value => value.TimeSeconds)
            .ThenByDescending(value => value.Strength * value.Confidence)
            .ThenBy(value => value.Id, StringComparer.Ordinal)
            .ToArray();
        Dictionary<string, HighlightPeakMatch> originalByHighlight =
            original.Matches.ToDictionary(
                value => value.HighlightId,
                StringComparer.Ordinal);
        List<HighlightPeakMatch> result = [];
        int peakIndex = 0;
        double cursor = 0;
        foreach (SelectedHighlight highlight in highlights
                     .OrderBy(value => value.SelectionOrder)
                     .ThenBy(value => value.Id, StringComparer.Ordinal))
        {
            double sourceDuration = Math.Max(
                0.001,
                highlight.Bounds.SafeEndSeconds -
                highlight.Bounds.SafeStartSeconds);
            double killOffset = Math.Clamp(
                highlight.Bounds.PrimaryKillSeconds -
                highlight.Bounds.SafeStartSeconds,
                0,
                sourceDuration);
            HighlightPeakMatch? selected = null;
            double selectedEnd = 0;
            for (int index = peakIndex; index < available.Length; index++)
            {
                MusicalPeak peak = available[index];
                double planned = peak.TimeSeconds - excerpt.StartSeconds;
                double outputStart = Math.Max(0, planned - killOffset);
                if (outputStart < cursor - 0.001)
                    continue;
                HighlightPeakMatch candidate = new()
                {
                    HighlightId = highlight.Id,
                    Peak = peak,
                    HighlightImportance = originalByHighlight.TryGetValue(
                        highlight.Id,
                        out HighlightPeakMatch? originalMatch)
                            ? originalMatch.HighlightImportance
                            : 1,
                    PlannedPeakSeconds = planned,
                    PlannedKillSeconds = planned,
                    AlignmentErrorMilliseconds = 0,
                    Score = peak.Strength * peak.Confidence,
                    Warnings = ["TIMELINE_SAFE_PEAK_SELECTION"]
                };
                TimeWarpPlan warp = timeWarp.Create(
                    highlight,
                    candidate,
                    outputStart,
                    options.TimeWarp);
                double outputEnd = outputStart +
                    TimeWarpMath.OutputDuration(warp, sourceDuration);
                if (outputEnd > excerpt.DurationSeconds + 0.001)
                    continue;
                selected = candidate;
                selectedEnd = outputEnd;
                peakIndex = index + 1;
                break;
            }
            if (selected is null)
                return [];
            result.Add(selected);
            cursor = selectedEnd;
        }
        return result
            .OrderBy(value => value.PlannedPeakSeconds)
            .ToArray();
    }

    private void AddBrollSegments(
        List<CinematicSequenceSegment> segments,
        IReadOnlyList<MusicSection> sections,
        IReadOnlyList<BrollCandidate> broll,
        MusicExcerptPlan excerpt,
        CinematicDirectorOptions options,
        MovieDurationBudget duration,
        double targetDuration,
        List<string> warnings)
    {
        double used = 0;
        bool continuityFallback = excerpt.Warnings.Contains(
            MusicExcerptSelector.RelaxedEnergyFallbackWarning,
            StringComparer.Ordinal);
        double brollLimit = continuityFallback
            ? targetDuration
            : duration.MaximumBrollSeconds;
        HashSet<string> selected = new(StringComparer.Ordinal);
        int index = 0;
        CinematicSequenceSegment[] highlights = segments
            .Where(value => value.HighlightId is not null)
            .OrderBy(value => value.OutputStartSeconds)
            .ToArray();
        double cursor = 0;
        foreach (CinematicSequenceSegment? next in highlights
                     .Cast<CinematicSequenceSegment?>()
                     .Append(null))
        {
            double gapEnd = next?.OutputStartSeconds ?? targetDuration;
            while (gapEnd - cursor >= 0.05 &&
                   used < brollLimit - 0.001)
            {
                double absoluteMusicTime =
                    excerpt.StartSeconds + cursor;
                MusicSection section = sections.FirstOrDefault(value =>
                        absoluteMusicTime >= value.StartSeconds &&
                        absoluteMusicTime < value.EndSeconds) ??
                    sections.OrderBy(value =>
                            Math.Abs(value.StartSeconds - absoluteMusicTime))
                        .First();
                BrollCandidate? candidate = broll
                    .Where(value => !selected.Contains(value.Id))
                    .OrderByDescending(value =>
                        BrollCompatibility(value, section.Type))
                    .ThenByDescending(value => value.CinematicScore)
                    .ThenBy(value => value.StartTick)
                    .ThenBy(value => value.Id, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (candidate is null)
                    break;
                double clipDuration = Math.Min(
                    candidate.DurationSeconds,
                    Math.Min(
                        gapEnd - cursor,
                        brollLimit - used));
                if (section.Type == MusicSectionType.Intro)
                    clipDuration = Math.Min(
                        clipDuration,
                        options.Duration.MaximumIntroSeconds);
                if (section.Type == MusicSectionType.Outro)
                    clipDuration = Math.Min(
                        clipDuration,
                        options.Duration.MaximumOutroSeconds);
                if (clipDuration < 0.05)
                    break;
                BrollCandidate planned = TrimCandidate(
                    candidate,
                    clipDuration);
                CameraShotPlan camera = cameraPlanner.Create(
                    planned,
                    options.Camera);
                if (camera.Type == CameraShotType.PlayerPov)
                {
                    warnings.AddRange(camera.Warnings.Select(value =>
                        $"{candidate.Id}:{value}"));
                }
                double end = cursor + clipDuration;
                selected.Add(candidate.Id);
                used += clipDuration;
                segments.Add(new CinematicSequenceSegment
                {
                    Id = $"segment-broll-{++index:D3}",
                    Role = Role(section.Type),
                    OutputStartSeconds = cursor,
                    OutputEndSeconds = end,
                    MusicSectionId = section.Id,
                    BrollCandidateId = candidate.Id,
                    Camera = camera,
                    TimeWarp = Natural(clipDuration),
                    Effects = []
                });
                cursor = end;
            }
            if (gapEnd - cursor > 0.05)
            {
                warnings.Add(
                    $"CINEMATIC_TIMELINE_GAP:{cursor:F3}-{gapEnd:F3}");
            }
            if (next is not null)
                cursor = Math.Max(cursor, next.OutputEndSeconds);
        }
    }

    private static CinematicSequenceRole Role(MusicSectionType section) =>
        section switch
        {
            MusicSectionType.Intro => CinematicSequenceRole.Intro,
            MusicSectionType.BuildUp or MusicSectionType.PreDrop =>
                CinematicSequenceRole.BuildUp,
            MusicSectionType.Drop or MusicSectionType.Chorus or
                MusicSectionType.HighEnergy =>
                CinematicSequenceRole.PreKill,
            MusicSectionType.Breakdown =>
                CinematicSequenceRole.Breakdown,
            MusicSectionType.Outro => CinematicSequenceRole.Outro,
            _ => CinematicSequenceRole.CalmBroll
        };

    private static BrollCandidate TrimCandidate(
        BrollCandidate candidate,
        double duration)
    {
        double ratio = duration / candidate.DurationSeconds;
        long endTick = candidate.StartTick + (long)Math.Round(
            (candidate.EndTick - candidate.StartTick) * ratio,
            MidpointRounding.AwayFromZero);
        PlayerTransformSample[] samples = candidate.Trajectory.Samples
            .Where(value => value.Tick <= endTick)
            .ToArray();
        if (samples.Length < 2)
        {
            samples = candidate.Trajectory.Samples
                .Take(2)
                .ToArray();
        }
        return candidate with
        {
            EndTick = endTick,
            DurationSeconds = duration,
            Trajectory = new PlayerTrajectory(samples)
        };
    }

    private static double BrollCompatibility(
        BrollCandidate candidate,
        MusicSectionType section) =>
        section switch
        {
            MusicSectionType.Intro when candidate.Type is
                BrollCandidateType.EstablishingShot or
                BrollCandidateType.PlayerApproach => 1,
            MusicSectionType.BuildUp or MusicSectionType.PreDrop
                when candidate.Type is
                    BrollCandidateType.UtilityPreparation or
                    BrollCandidateType.UtilityThrow or
                    BrollCandidateType.WeaponDraw or
                    BrollCandidateType.WeaponReload or
                    BrollCandidateType.ScopePreparation => 1,
            MusicSectionType.Breakdown when candidate.Type ==
                BrollCandidateType.PostFightExit => 1,
            _ => 0.5
        };

    private static CameraShotPlan HighlightPov(SelectedHighlight highlight) =>
        new()
        {
            Id = $"camera-highlight-{highlight.Id}-pov",
            Type = CameraShotType.PlayerPov,
            DemoId = highlight.Highlight.SourceDemoId,
            StartTick = highlight.Highlight.StartTick,
            EndTick = highlight.Highlight.EndTick,
            TargetDurationSeconds =
                highlight.Bounds.SafeEndSeconds -
                highlight.Bounds.SafeStartSeconds,
            Keyframes = [],
            FovStart = 90,
            FovEnd = 90,
            RequiresHighFpsCapture =
                highlight.Highlight.Type is
                    HighlightType.Ace or
                    HighlightType.QuadKill,
            FallbackShotId = string.Empty,
            Warnings = []
        };

    private static TimeWarpPlan Natural(double duration) =>
        new(
            1,
            [new TimeWarpSegment(0, duration, 1)],
            false,
            []);
}

public static class CinematicAlignmentReportBuilder
{
    public static CinematicAlignmentReport FromPlan(
        CinematicMoviePlan plan,
        IReadOnlyList<MusicSection> sections)
    {
        double[] errors = plan.HighlightMatches
            .Select(value => Math.Abs(value.AlignmentErrorMilliseconds))
            .ToArray();
        bool relaxedEnergy = plan.MusicExcerpt.Warnings.Contains(
            MusicExcerptSelector.RelaxedEnergyFallbackWarning,
            StringComparer.Ordinal);
        int outside = plan.HighlightMatches.Count(match =>
        {
            MusicSection? section = sections.FirstOrDefault(value =>
                value.Id == match.Peak.SectionId);
            return section is null ||
                !MusicalPeakDetector.IsAllowedPrimaryKillSection(
                    section.Type,
                    relaxedEnergy);
        });
        return new CinematicAlignmentReport
        {
            HighlightMatches = plan.HighlightMatches,
            MaximumAlignmentErrorMilliseconds =
                errors.DefaultIfEmpty(0).Max(),
            AverageAlignmentErrorMilliseconds =
                errors.DefaultIfEmpty(0).Average(),
            KillsOutsideHighEnergySections = outside,
            VerifiedFromRenderedMedia = false,
            Warnings =
            [
                "PLANNED_ALIGNMENT_ONLY_RENDERED_MEDIA_NOT_MEASURED"
            ]
        };
    }
}
