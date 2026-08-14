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
    public const string SchemaVersion = "2.0";
    public const string PlannerVersion = "10.9";

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
        double detectedIntro = IntroReservationSeconds(
            sections,
            excerpt,
            options.Duration.MaximumIntroSeconds);
        double boundedDetectedIntro = broll.Count == 0
            ? 0
            : Math.Min(
                detectedIntro,
                duration.MaximumBrollSeconds);
        double desiredIntro = broll.Count == 0
            ? 0
            : Math.Clamp(
                Math.Max(
                    boundedDetectedIntro,
                    Math.Min(3.2, excerpt.DurationSeconds * 0.10)),
                0,
                Math.Min(
                    duration.MaximumBrollSeconds,
                    Math.Min(
                        options.Duration.MaximumIntroSeconds,
                        excerpt.DurationSeconds)));
        double[] introReservations =
        [
            desiredIntro,
            Math.Min(desiredIntro, 2.4),
            boundedDetectedIntro,
            0
        ];
        bool narrativeReflowApplied = false;
        foreach (double reservation in introReservations
                     .Distinct()
                     .OrderByDescending(value => value))
        {
            HighlightPeakMatch[] timelineSafe = CreateTimelineSafeMatches(
                highlights,
                excerpt,
                sectionById,
                matching,
                options,
                reservation,
                relaxedEnergy);
            if (timelineSafe.Length == highlights.Count)
            {
                matches = timelineSafe;
                narrativeReflowApplied = true;
                warnings.Add(relaxedEnergy
                    ? "HIGHLIGHT_PEAK_TIMELINE_FALLBACK"
                    : "HIGHLIGHT_NARRATIVE_REFLOW");
                if (reservation > 0.001)
                {
                    warnings.Add(
                        $"CINEMATIC_INTRO_RESERVED:{reservation:F2}");
                }
                if (reservation + 0.001 < desiredIntro)
                {
                    warnings.Add(
                        $"CINEMATIC_INTRO_SHORTENED:{desiredIntro:F2}->{reservation:F2}");
                }
                break;
            }
        }
        if (!narrativeReflowApplied && desiredIntro > 0.001)
            warnings.Add("CINEMATIC_INTRO_RESERVATION_UNAVAILABLE");
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
            double gap = outputStart - highlightCursor;
            if (highlightCursor > 0 &&
                gap > 0.001 &&
                gap < 0.75)
            {
                CinematicTimeWarpOptions snappedOptions =
                    MusicAwareTimeWarpOptions(
                        options.TimeWarp,
                        music.Frames,
                        excerpt.StartSeconds + match.PlannedPeakSeconds);
                TimeWarpPlan snappedWarp = timeWarp.Create(
                    highlight,
                    match,
                    highlightCursor,
                    snappedOptions);
                double snappedEnd = highlightCursor +
                    TimeWarpMath.OutputDuration(snappedWarp, sourceDuration);
                double nextNaturalStart = index + 1 < matches.Length
                    ? NaturalOutputStart(
                        matches[index + 1],
                        highlightById[matches[index + 1].HighlightId])
                    : double.PositiveInfinity;
                if (snappedEnd <= excerpt.DurationSeconds + 0.001 &&
                    snappedEnd <= nextNaturalStart + 0.001)
                {
                    outputStart = highlightCursor;
                    warnings.Add(
                        $"HIGHLIGHT_MICRO_GAP_SNAPPED:{highlight.Id}:{gap:F3}");
                }
                else
                {
                    warnings.Add(
                        $"HIGHLIGHT_MICRO_GAP_PRESERVED:{highlight.Id}:{gap:F3}");
                }
            }
            if (outputStart < highlightCursor - 0.001)
            {
                warnings.Add(
                    $"HIGHLIGHT_PEAK_SPACING_INSUFFICIENT:{highlight.Id}");
                continue;
            }
            CinematicTimeWarpOptions warpOptions = MusicAwareTimeWarpOptions(
                options.TimeWarp,
                music.Frames,
                excerpt.StartSeconds + match.PlannedPeakSeconds);
            TimeWarpPlan warp = timeWarp.Create(
                highlight,
                match,
                outputStart,
                warpOptions);
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
                    final,
                    index)
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
        double targetDuration = Math.Min(
            duration.TargetSeconds,
            excerpt.DurationSeconds);
        AddBrollSegments(
            segments,
            sections,
            music.Frames,
            broll,
            highlightById,
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
        bool hasReportedGap = warnings.Any(value => value.StartsWith(
            "CINEMATIC_TIMELINE_GAP:",
            StringComparison.Ordinal));
        if (options.CompactTimelineWhenMaterialIsInsufficient &&
            (hasReportedGap || TimelineIsDiscontinuous(ordered)))
        {
            CompactTimelineResult compacted = CompactTimeline(
                ordered,
                effectiveMatches,
                highlightById);
            ordered = compacted.Segments;
            effectiveMatches = compacted.Matches.ToList();
            targetDuration = compacted.TargetDurationSeconds;
            warnings.RemoveAll(value => value.StartsWith(
                "CINEMATIC_TIMELINE_GAP:",
                StringComparison.Ordinal));
            warnings.Add(
                "CINEMATIC_TIMELINE_COMPACTED_FOR_AVAILABLE_MATERIAL");
            warnings.Add("MUSIC_TRIMMED_TO_COMPACT_TIMELINE");
        }
        ordered = EffectRarityPolicy.Apply(
                ordered,
                out EffectRarityReport effectRarity)
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
        if (options.Duration.Selection == MovieDurationSelection.Auto &&
            ordered
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
            Warnings = warnings.Distinct(StringComparer.Ordinal).ToArray(),
            EffectRarity = effectRarity,
            CameraDiversity = ShotDiversityPolicy.AnalyzeFilm(
                ordered.Select(value => value.Camera).ToArray(),
                targetDuration)
        };
    }

    private HighlightPeakMatch[] CreateTimelineSafeMatches(
        IReadOnlyList<SelectedHighlight> highlights,
        MusicExcerptPlan excerpt,
        Dictionary<string, MusicSection> sections,
        HighlightPeakMatchPlan original,
        CinematicDirectorOptions options,
        double introReservationSeconds,
        bool relaxedEnergy)
    {
        double minimumPeakStrength = excerpt.Warnings.Contains(
            MusicExcerptSelector.RelaxedEnergyFallbackWarning,
            StringComparer.Ordinal)
                ? 0.25
                : 0.45;
        MusicalPeak[] available = excerpt.Peaks
            .Where(value =>
                value.Strength >= minimumPeakStrength &&
                value.Confidence >= 0.40 &&
                sections.TryGetValue(
                    value.SectionId,
                    out MusicSection? section) &&
                MusicalPeakDetector.IsAllowedPrimaryKillSection(
                    section.Type,
                    relaxedEnergy))
            .OrderBy(value => value.TimeSeconds)
            .ThenByDescending(value => value.Strength * value.Confidence)
            .ThenBy(value => value.Id, StringComparer.Ordinal)
            .ToArray();
        Dictionary<string, HighlightPeakMatch> originalByHighlight =
            original.Matches.ToDictionary(
                value => value.HighlightId,
                StringComparer.Ordinal);
        List<HighlightPeakMatch> result = [];
        SelectedHighlight[] story = StoryOrder(highlights).ToArray();
        int peakIndex = 0;
        double cursor = Math.Clamp(
            introReservationSeconds,
            0,
            Math.Min(
                options.Duration.MaximumIntroSeconds,
                excerpt.DurationSeconds));
        double firstTarget = Math.Max(
            cursor + 0.75,
            excerpt.DurationSeconds * 0.10);
        double lastTarget = Math.Max(
            firstTarget,
            Math.Min(
                excerpt.DurationSeconds - 0.75,
                excerpt.DurationSeconds * 0.88));
        for (int storyIndex = 0; storyIndex < story.Length; storyIndex++)
        {
            SelectedHighlight highlight = story[storyIndex];
            double sourceDuration = Math.Max(
                0.001,
                highlight.Bounds.SafeEndSeconds -
                highlight.Bounds.SafeStartSeconds);
            double killOffset = Math.Clamp(
                highlight.Bounds.PrimaryKillSeconds -
                highlight.Bounds.SafeStartSeconds,
                0,
                sourceDuration);
            double desiredKill = story.Length == 1
                ? Math.Clamp(
                    excerpt.DurationSeconds * 0.55,
                    firstTarget,
                    lastTarget)
                : firstTarget +
                  (lastTarget - firstTarget) * storyIndex /
                  (story.Length - 1d);
            HighlightPeakMatch? selected = null;
            double selectedEnd = 0;
            int selectedIndex = -1;
            double selectedScore = double.PositiveInfinity;
            int remainingHighlights = story.Length - storyIndex - 1;
            int maximumCandidateIndex = available.Length -
                remainingHighlights - 1;
            for (int index = peakIndex;
                 index <= maximumCandidateIndex;
                 index++)
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
                double spreadScore = Math.Abs(planned - desiredKill) -
                    peak.Strength * peak.Confidence * 0.50;
                if (spreadScore >= selectedScore - 0.000001)
                    continue;
                selected = candidate;
                selectedEnd = outputEnd;
                selectedIndex = index;
                selectedScore = spreadScore;
            }
            if (selected is null)
                return [];
            result.Add(selected);
            peakIndex = selectedIndex + 1;
            cursor = selectedEnd;
        }
        return result
            .OrderBy(value => value.PlannedPeakSeconds)
            .ToArray();
    }

    private static double NaturalOutputStart(
        HighlightPeakMatch match,
        SelectedHighlight highlight)
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
        return Math.Max(0, match.PlannedPeakSeconds - killOffset);
    }

    private static IEnumerable<SelectedHighlight> StoryOrder(
        IReadOnlyList<SelectedHighlight> highlights) =>
        highlights
            .OrderBy(value => value.Highlight.KillCount > 1 ? 1 : 0)
            .ThenBy(value => value.Highlight.KillCount)
            .ThenBy(value =>
                value.Highlight.KillCount > 1
                    ? value.Bounds.PrimaryKillSeconds -
                        value.Bounds.SafeStartSeconds
                    : 0)
            .ThenBy(value => value.Highlight.BeautyScore)
            .ThenBy(value => value.Highlight.TotalScore)
            .ThenBy(value => value.SelectionOrder)
            .ThenBy(value => value.Id, StringComparer.Ordinal);

    private static double IntroReservationSeconds(
        IEnumerable<MusicSection> sections,
        MusicExcerptPlan excerpt,
        double maximumIntroSeconds)
    {
        double end = sections
            .Where(value => value.Type == MusicSectionType.Intro)
            .Select(value => value.EndSeconds - excerpt.StartSeconds)
            .Where(value => value > 0)
            .DefaultIfEmpty(0)
            .Max();
        return Math.Clamp(
            end,
            0,
            Math.Min(maximumIntroSeconds, excerpt.DurationSeconds));
    }

    private void AddBrollSegments(
        List<CinematicSequenceSegment> segments,
        IReadOnlyList<MusicSection> sections,
        IReadOnlyList<MusicFrame> musicFrames,
        IReadOnlyList<BrollCandidate> broll,
        IReadOnlyDictionary<string, SelectedHighlight> highlightById,
        MusicExcerptPlan excerpt,
        CinematicDirectorOptions options,
        MovieDurationBudget duration,
        double targetDuration,
        List<string> warnings)
    {
        double used = 0;
        double brollLimit = options.Duration.Selection ==
                MovieDurationSelection.Auto
            ? duration.MaximumBrollSeconds
            : targetDuration;
        HashSet<string> selected = new(StringComparer.Ordinal);
        List<CameraShotPlan> selectedCameras = [];
        int index = 0;
        CinematicSequenceSegment[] highlights = segments
            .Where(value => value.HighlightId is not null)
            .OrderBy(value => value.OutputStartSeconds)
            .ToArray();
        HashSet<string> selectedIntervals = highlights
            .Select(value =>
                $"{value.Camera.DemoId}:{value.Camera.StartTick}-" +
                $"{value.Camera.EndTick}")
            .ToHashSet(StringComparer.Ordinal);
        double cursor = 0;
        CinematicSequenceSegment? previousHighlight = null;
        foreach (CinematicSequenceSegment? next in highlights
                     .Cast<CinematicSequenceSegment?>()
                     .Append(null))
        {
            double gapEnd = next?.OutputStartSeconds ?? targetDuration;
            while (gapEnd - cursor >=
                       MeaningfulShotMinimumSeconds &&
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
                double availableDuration = Math.Min(
                    gapEnd - cursor,
                    brollLimit - used);
                GameplayVector3? nextHighlightPosition =
                    NextHighlightPosition(next, highlightById);
                var choice = broll
                    .Where(value =>
                        !selected.Contains(value.Id) &&
                        !selectedIntervals.Contains(SourceInterval(value)) &&
                        (value.Type != BrollCandidateType.VictimReaction ||
                         (selectedCameras.Count(camera =>
                              camera.Family ==
                                  CameraShotFamily.VictimReaction) <
                              Math.Max(1, highlights.Length / 3) &&
                          VictimReactionMatches(
                              value,
                              previousHighlight,
                              highlightById))))
                    .Select(candidate =>
                    {
                        double durationForCandidate = Math.Min(
                            candidate.DurationSeconds,
                            availableDuration);
                        if (section.Type == MusicSectionType.Intro)
                        {
                            durationForCandidate = Math.Min(
                                durationForCandidate,
                                options.Duration.MaximumIntroSeconds);
                        }
                        if (section.Type == MusicSectionType.Outro)
                        {
                            durationForCandidate = Math.Min(
                                durationForCandidate,
                                options.Duration.MaximumOutroSeconds);
                        }
                        BrollCandidate plannedCandidate = TrimCandidate(
                            candidate,
                            durationForCandidate);
                        CameraShotPlan camera = cameraPlanner.Create(
                            plannedCandidate,
                            options.Camera with
                            {
                                DestinationSubjectPosition =
                                    string.Equals(
                                        next?.Camera.DemoId,
                                        plannedCandidate.DemoId,
                                        StringComparison.Ordinal)
                                        ? nextHighlightPosition
                                        : null
                            });
                        if (durationForCandidate <
                            MinimumFreeCameraShotSeconds)
                            return null;
                        if (camera.Family == CameraShotFamily.PlayerPov ||
                            camera.Type == CameraShotType.PlayerPov)
                            return null;
                        ShotDiversityDecision diversity =
                            ShotDiversityPolicy.Evaluate(
                                camera,
                                options.MapName,
                                selectedCameras);
                        if (!diversity.Accepted)
                        {
                            camera = camera with
                            {
                                Warnings = camera.Warnings.Concat(
                                    diversity.RejectionReasons.Select(value =>
                                        $"SHOT_DIVERSITY_ADVISORY:{value}"))
                                    .Distinct(StringComparer.Ordinal)
                                    .ToArray()
                            };
                        }
                        return new
                        {
                            Candidate = candidate,
                            Planned = plannedCandidate,
                            Duration = durationForCandidate,
                            Camera = camera
                        };
                    })
                    .Where(value => value is not null && value.Duration >=
                        MeaningfulShotMinimumSeconds)
                    .OrderByDescending(value =>
                        value!.Candidate.Type ==
                            BrollCandidateType.VictimReaction)
                    .ThenByDescending(value =>
                        value!.Duration + 0.001 >= availableDuration)
                    .ThenByDescending(value =>
                        BrollCompatibility(
                            value!.Candidate,
                            section.Type))
                    .ThenBy(value => value!.Candidate.ActionDensity)
                    .ThenByDescending(value =>
                        value!.Candidate.CinematicScore)
                    .ThenBy(value => value!.Candidate.StartTick)
                    .ThenBy(value =>
                        value!.Candidate.Id,
                        StringComparer.Ordinal)
                    .FirstOrDefault();
                if (choice is null)
                    break;
                BrollCandidate candidate = choice!.Candidate;
                double clipDuration = choice.Duration;
                if (clipDuration < MeaningfulShotMinimumSeconds)
                    break;
                CameraShotPlan camera = choice.Camera;
                double end = cursor + clipDuration;
                selected.Add(candidate.Id);
                selectedIntervals.Add(SourceInterval(candidate));
                selectedCameras.Add(camera);
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
                    TimeWarp = BrollTimeWarp(
                        choice.Planned,
                        camera,
                        musicFrames,
                        excerpt.StartSeconds + cursor,
                        excerpt.StartSeconds + end),
                    Effects = []
                });
                cursor = end;
            }
            double remainder = gapEnd - cursor;
            if (remainder is > 0.001 and < MeaningfulShotMinimumSeconds)
            {
                int previousSegmentIndex = segments.FindLastIndex(value =>
                    Math.Abs(value.OutputEndSeconds - cursor) < 0.001);
                if (previousSegmentIndex >= 0)
                {
                    CinematicSequenceSegment previous =
                        segments[previousSegmentIndex];
                    if (previous.BrollCandidateId is not null)
                    {
                        double sourceDuration = previous.OutputEndSeconds -
                            previous.OutputStartSeconds;
                        double extendedDuration = gapEnd -
                            previous.OutputStartSeconds;
                        double speed = sourceDuration / extendedDuration;
                        segments[previousSegmentIndex] = previous with
                        {
                            OutputEndSeconds = gapEnd,
                            TimeWarp = new TimeWarpPlan(
                                speed,
                                [],
                                false,
                                ["MICRO_GAP_ABSORBED_BY_BROLL_RETIMING"])
                        };
                        used += remainder;
                    }
                    else
                    {
                        segments[previousSegmentIndex] = previous with
                        {
                            OutputEndSeconds = gapEnd
                        };
                        warnings.Add(
                            $"HIGHLIGHT_POST_KILL_TAIL_EXTENDED:{remainder:F3}");
                    }
                    cursor = gapEnd;
                    warnings.Add(
                        $"CINEMATIC_MICRO_GAP_ABSORBED:{remainder:F3}");
                }
            }
            if (gapEnd - cursor >= MeaningfulShotMinimumSeconds)
            {
                warnings.Add(
                    $"CINEMATIC_TIMELINE_GAP:{cursor:F3}-{gapEnd:F3}");
            }
            if (next is not null)
            {
                cursor = Math.Max(cursor, next.OutputEndSeconds);
                previousHighlight = next;
            }
        }
    }

    private const double MeaningfulShotMinimumSeconds = 1.5;
    private const double MinimumFreeCameraShotSeconds = 1.5;

    private static TimeWarpPlan BrollTimeWarp(
        BrollCandidate candidate,
        CameraShotPlan camera,
        IReadOnlyList<MusicFrame> frames,
        double musicStartSeconds,
        double musicEndSeconds)
    {
        double duration = candidate.DurationSeconds;
        if (camera.Family == CameraShotFamily.PlayerPov || duration < 1.5)
            return Natural(duration);

        if (candidate.Type == BrollCandidateType.PlayerJump &&
            candidate.FocusTick is long focusTick &&
            UseOccasionalJumpSlowMotion(candidate.Id))
        {
            double spanTicks = Math.Max(1, candidate.EndTick - candidate.StartTick);
            double focus = Math.Clamp(
                (focusTick - candidate.StartTick) / spanTicks,
                0.20,
                0.80);
            return BalancedSlowMotion(
                duration,
                focus,
                0.30,
                0.72,
                "FREE_CAMERA_JUMP_SLOW_MOTION");
        }

        return Natural(duration);
    }

    private static CinematicTimeWarpOptions MusicAwareTimeWarpOptions(
        CinematicTimeWarpOptions options,
        IReadOnlyList<MusicFrame> frames,
        double musicalImpactSeconds)
    {
        MusicFrame[] before = frames.Where(value =>
                value.TimeSeconds >= musicalImpactSeconds - 0.55 &&
                value.TimeSeconds < musicalImpactSeconds)
            .ToArray();
        MusicFrame[] after = frames.Where(value =>
                value.TimeSeconds >= musicalImpactSeconds &&
                value.TimeSeconds <= musicalImpactSeconds + 0.55)
            .ToArray();
        if (before.Length == 0 || after.Length == 0)
            return options;
        double energyDelta = Math.Abs(
            after.Average(value => value.Energy) -
            before.Average(value => value.Energy));
        double onset = after.Max(value => value.OnsetStrength);
        bool transition = energyDelta >= 0.10 || onset >= 0.72;
        return options with { MusicEnergyTransition = transition };
    }

    private static bool UseOccasionalJumpSlowMotion(string candidateId)
    {
        int checksum = 0;
        foreach (char character in candidateId)
            checksum = unchecked(checksum * 31 + character);
        return (checksum & 1) == 0;
    }

    private static TimeWarpPlan BalancedSlowMotion(
        double duration,
        double focusFraction,
        double slowFraction,
        double slowSpeed,
        string warning)
    {
        double slowDuration = duration * slowFraction;
        double slowStart = Math.Clamp(
            duration * focusFraction - slowDuration / 2,
            0,
            duration - slowDuration);
        double slowEnd = slowStart + slowDuration;
        double slowOutputDuration = slowDuration / slowSpeed;
        double fastSourceDuration = duration - slowDuration;
        double fastOutputDuration = duration - slowOutputDuration;
        if (fastOutputDuration <= 0.05 || fastSourceDuration <= 0.05)
            return Natural(duration);
        double fastSpeed = fastSourceDuration / fastOutputDuration;
        List<TimeWarpSegment> segments = [];
        if (slowStart > 0.001)
            segments.Add(new TimeWarpSegment(0, slowStart, fastSpeed));
        segments.Add(new TimeWarpSegment(slowStart, slowEnd, slowSpeed));
        if (duration - slowEnd > 0.001)
            segments.Add(new TimeWarpSegment(slowEnd, duration, fastSpeed));
        return new TimeWarpPlan(
            1,
            segments,
            true,
            [warning]);
    }

    private static GameplayVector3? NextHighlightPosition(
        CinematicSequenceSegment? segment,
        IReadOnlyDictionary<string, SelectedHighlight> highlightById)
    {
        if (segment?.HighlightId is not string highlightId ||
            !highlightById.TryGetValue(
                highlightId,
                out SelectedHighlight? highlight))
        {
            return null;
        }
        KillDescriptor? primary = highlight.Highlight.Kills
            .OrderBy(value => Math.Abs(
                value.Tick - highlight.Highlight.PrimaryKillTick))
            .FirstOrDefault(value =>
                value.ShooterPosition is not null ||
                value.HitPosition is not null ||
                value.VictimPosition is not null);
        return primary?.ShooterPosition ??
            primary?.HitPosition ??
            primary?.VictimPosition;
    }

    private static bool VictimReactionMatches(
        BrollCandidate candidate,
        CinematicSequenceSegment? previousHighlight,
        IReadOnlyDictionary<string, SelectedHighlight> highlightById)
    {
        if (candidate.FocusTick is not long focusTick ||
            previousHighlight?.HighlightId is not string highlightId ||
            !highlightById.TryGetValue(
                highlightId,
                out SelectedHighlight? highlight) ||
            !string.Equals(
                candidate.DemoId,
                highlight.Highlight.SourceDemoId,
                StringComparison.Ordinal))
        {
            return false;
        }
        return highlight.Highlight.Kills.Any(kill =>
            Math.Abs(kill.Tick - focusTick) <=
                Math.Max(1, highlight.Highlight.TickRate / 3) &&
            candidate.SubjectIds.Contains(
                kill.VictimPlayerId,
                StringComparer.Ordinal));
    }

    private static bool TimelineIsDiscontinuous(
        CinematicSequenceSegment[] segments)
    {
        if (segments.Length == 0 ||
            segments[0].OutputStartSeconds > 0.05)
        {
            return true;
        }
        return segments.Zip(segments.Skip(1)).Any(pair =>
            Math.Abs(
                pair.First.OutputEndSeconds -
                pair.Second.OutputStartSeconds) > 0.05);
    }

    private static string SourceInterval(BrollCandidate candidate) =>
        $"{candidate.DemoId}:{candidate.StartTick}-{candidate.EndTick}";

    private static CompactTimelineResult CompactTimeline(
        IReadOnlyList<CinematicSequenceSegment> segments,
        IReadOnlyList<HighlightPeakMatch> matches,
        Dictionary<string, SelectedHighlight> highlights)
    {
        Dictionary<string, HighlightPeakMatch> matchesByHighlight =
            matches.ToDictionary(value => value.HighlightId, StringComparer.Ordinal);
        List<CinematicSequenceSegment> compacted = [];
        List<HighlightPeakMatch> compactedMatches = [];
        double cursor = 0;
        foreach (CinematicSequenceSegment segment in segments
                     .OrderBy(value => value.OutputStartSeconds)
                     .ThenBy(value => value.Id, StringComparer.Ordinal))
        {
            double duration = Math.Max(
                0.001,
                segment.OutputEndSeconds - segment.OutputStartSeconds);
            CinematicSequenceSegment shifted = segment with
            {
                OutputStartSeconds = cursor,
                OutputEndSeconds = cursor + duration
            };
            compacted.Add(shifted);
            if (shifted.HighlightId is not null &&
                highlights.TryGetValue(
                    shifted.HighlightId,
                    out SelectedHighlight? highlight) &&
                matchesByHighlight.TryGetValue(
                    shifted.HighlightId,
                    out HighlightPeakMatch? match))
            {
                double killOffset = Math.Clamp(
                    highlight.Bounds.PrimaryKillSeconds -
                    highlight.Bounds.SafeStartSeconds,
                    0,
                    Math.Max(
                        0.001,
                        highlight.Bounds.SafeEndSeconds -
                        highlight.Bounds.SafeStartSeconds));
                double actualKill = cursor + TimeWarpMath.MapSourceTime(
                    shifted.TimeWarp,
                    killOffset);
                compactedMatches.Add(match with
                {
                    PlannedPeakSeconds = actualKill,
                    PlannedKillSeconds = actualKill,
                    AlignmentErrorMilliseconds = Math.Abs(
                        actualKill - match.PlannedPeakSeconds) * 1000,
                    Warnings = match.Warnings
                        .Append(
                            "MUSIC_PEAK_ALIGNMENT_RELAXED_FOR_COMPACT_TIMELINE")
                        .Distinct(StringComparer.Ordinal)
                        .ToArray()
                });
            }
            cursor += duration;
        }
        return new CompactTimelineResult(
            compacted.ToArray(),
            compactedMatches
                .OrderBy(value => value.PlannedKillSeconds)
                .ToArray(),
            cursor);
    }

    private sealed record CompactTimelineResult(
        CinematicSequenceSegment[] Segments,
        HighlightPeakMatch[] Matches,
        double TargetDurationSeconds);

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
            FocusTick = candidate.FocusTick is long focusTick &&
                        focusTick <= endTick
                ? focusTick
                : null,
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
            MusicSectionType.Calm or MusicSectionType.Verse
                when candidate.Type is
                    BrollCandidateType.VictimReaction => 1.35,
            MusicSectionType.Breakdown
                when candidate.Type is
                    BrollCandidateType.VictimReaction => 1.25,
            MusicSectionType.Calm or MusicSectionType.Verse
                when candidate.Type is
                    BrollCandidateType.EstablishingShot or
                    BrollCandidateType.PlayerApproach or
                    BrollCandidateType.SideMovement or
                    BrollCandidateType.RearMovement or
                    BrollCandidateType.PlayerJump or
                    BrollCandidateType.EnvironmentShot => 1,
            MusicSectionType.Calm or MusicSectionType.Verse
                when candidate.Type is
                    BrollCandidateType.UtilityThrow or
                    BrollCandidateType.BombPlant or
                    BrollCandidateType.BombDefuse => 0.2,
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
