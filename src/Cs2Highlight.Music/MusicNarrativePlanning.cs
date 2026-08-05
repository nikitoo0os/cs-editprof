using Cs2Highlight.Analysis;

namespace Cs2Highlight.Music;

public interface IMusicSectionClassifier
{
    IReadOnlyList<MusicSection> Classify(MusicAnalysis analysis);
}

public sealed class MusicSectionClassifier(
    IMusicalAnchorBuilder anchorBuilder) : IMusicSectionClassifier
{
    public IReadOnlyList<MusicSection> Classify(MusicAnalysis analysis)
    {
        MusicSection[] source = analysis.Sections.Count > 0
            ? analysis.Sections.OrderBy(value => value.StartSeconds).ToArray()
            :
            [
                new MusicSection(
                    1,
                    0,
                    analysis.Audio.DurationSeconds,
                    "Unknown",
                    analysis.Frames.Count == 0
                        ? 0
                        : analysis.Frames.Average(value => value.Energy))
            ];
        IReadOnlyList<MusicalAnchor> anchors = anchorBuilder.Build(analysis);
        List<MusicSection> result = new(source.Length);
        double previousEnergy = source[0].Energy;
        for (int index = 0; index < source.Length; index++)
        {
            MusicSection raw = source[index];
            MusicFrame[] frames = analysis.Frames
                .Where(value =>
                    value.TimeSeconds >= raw.StartSeconds &&
                    value.TimeSeconds < raw.EndSeconds)
                .OrderBy(value => value.TimeSeconds)
                .ToArray();
            double energy = Average(frames, value => value.Energy, raw.Energy);
            double bass = Average(frames, value => value.BassEnergy, raw.BassEnergy);
            double rhythm = Average(
                frames,
                value => value.RhythmicDensity,
                raw.RhythmicDensity);
            double brightness = Average(
                frames,
                value => value.SpectralBrightness,
                raw.SpectralBrightness);
            double flux = Average(frames, value => value.SpectralFlux, 0);
            double novelty = Average(frames, value => value.Novelty, 0);
            double harmonic = Average(frames, value => value.HarmonicChange, 0);
            double onset = frames.Length == 0
                ? analysis.Onsets
                    .Where(value =>
                        value.TimeSeconds >= raw.StartSeconds &&
                        value.TimeSeconds < raw.EndSeconds)
                    .Select(value => value.Strength)
                    .DefaultIfEmpty(0)
                    .Average()
                : frames.Average(value => value.OnsetStrength);
            double slope = frames.Length >= 4
                ? Average(
                    frames.Skip(frames.Length / 2),
                    value => value.Energy,
                    energy) -
                  Average(
                    frames.Take(frames.Length / 2),
                    value => value.Energy,
                    energy)
                : energy - previousEnergy;
            double contrast = frames.Length == 0
                ? raw.DynamicContrast
                : Math.Clamp(
                    frames.Max(value => value.Energy) -
                    frames.Min(value => value.Energy),
                    0,
                    1);
            MusicalAnchor[] sectionAnchors = anchors
                .Where(value =>
                    value.TimeSeconds >= raw.StartSeconds &&
                    value.TimeSeconds < raw.EndSeconds)
                .ToArray();
            bool downbeatStart = sectionAnchors.Any(value =>
                value.Type == MusicalAnchorType.Downbeat &&
                Math.Abs(value.TimeSeconds - raw.StartSeconds) <= 0.25);
            double calmScore =
                0.35 * (1 - energy) +
                0.25 * (1 - rhythm) +
                0.20 * (1 - flux) +
                0.20 * (1 - bass);
            double buildScore =
                0.35 * Math.Clamp(slope * 2, 0, 1) +
                0.20 * rhythm +
                0.15 * brightness +
                0.15 * onset +
                0.15 * novelty;
            double dropScore =
                0.20 * novelty +
                0.20 * onset +
                0.20 * Math.Clamp(slope * 2, 0, 1) +
                0.20 * bass +
                0.20 * (downbeatStart ? 1 : 0);
            double highEnergyScore =
                0.40 * energy +
                0.25 * rhythm +
                0.20 * bass +
                0.15 * onset;
            double breakdownScore =
                0.40 * Math.Clamp(-slope * 2, 0, 1) +
                0.25 * (1 - energy) +
                0.20 * (1 - rhythm) +
                0.15 * harmonic;
            Dictionary<string, double> scores = new(StringComparer.Ordinal)
            {
                ["calm"] = Clamp(calmScore),
                ["buildUp"] = Clamp(buildScore),
                ["drop"] = Clamp(dropScore),
                ["highEnergy"] = Clamp(highEnergyScore),
                ["breakdown"] = Clamp(breakdownScore),
                ["energySlope"] = Math.Clamp(slope, -1, 1),
                ["onsetDensity"] = Clamp(onset),
                ["spectralFlux"] = Clamp(flux),
                ["novelty"] = Clamp(novelty),
                ["harmonicChange"] = Clamp(harmonic),
                ["downbeatAtStart"] = downbeatStart ? 1 : 0
            };
            MusicSectionType type = SelectType(
                index,
                source.Length,
                raw,
                energy,
                slope,
                bass,
                onset,
                scores);
            double[] ranked =
            [
                calmScore,
                buildScore,
                dropScore,
                highEnergyScore,
                breakdownScore
            ];
            Array.Sort(ranked);
            double confidence = Math.Clamp(
                0.45 +
                Math.Max(0, ranked[^1] - ranked[^2]) * 0.8 +
                Math.Min(0.15, raw.EndSeconds - raw.StartSeconds > 2 ? 0.1 : 0),
                0.35,
                0.98);
            result.Add(raw with
            {
                Id = string.IsNullOrWhiteSpace(raw.Id)
                    ? $"section-{index + 1:D3}"
                    : raw.Id,
                Type = type,
                Label = type.ToString(),
                Energy = Clamp(energy),
                RhythmicDensity = Clamp(rhythm),
                BassEnergy = Clamp(bass),
                SpectralBrightness = Clamp(brightness),
                DynamicContrast = Clamp(contrast),
                Confidence = confidence,
                Anchors = sectionAnchors,
                ScoreBreakdown = scores
            });
            previousEnergy = energy;
        }
        return result;
    }

    private static MusicSectionType SelectType(
        int index,
        int count,
        MusicSection raw,
        double energy,
        double slope,
        double bass,
        double onset,
        Dictionary<string, double> scores)
    {
        double duration = raw.EndSeconds - raw.StartSeconds;
        if (index == 0 &&
            raw.StartSeconds <= 0.1 &&
            duration <= 8 &&
            energy < 0.55)
            return MusicSectionType.Intro;
        if (index == count - 1 &&
            duration <= 8 &&
            energy < 0.45 &&
            slope < -0.05)
            return MusicSectionType.Outro;
        bool dropEvidence =
            slope >= 0.12 &&
            bass >= 0.55 &&
            onset >= 0.35 &&
            scores["drop"] >= 0.58;
        if (dropEvidence)
            return MusicSectionType.Drop;
        if (scores["buildUp"] >= 0.50 && slope >= 0.08)
            return scores["drop"] >= 0.50
                ? MusicSectionType.PreDrop
                : MusicSectionType.BuildUp;
        if (scores["highEnergy"] >= 0.62 && energy >= 0.62)
            return MusicSectionType.HighEnergy;
        if (scores["breakdown"] >= 0.52 && slope <= -0.08)
            return MusicSectionType.Breakdown;
        if (scores["calm"] >= 0.62 && energy <= 0.45)
            return MusicSectionType.Calm;
        return energy >= 0.55
            ? MusicSectionType.Chorus
            : MusicSectionType.Verse;
    }

    private static double Average(
        IEnumerable<MusicFrame> frames,
        Func<MusicFrame, double> selector,
        double fallback)
    {
        MusicFrame[] values = frames.ToArray();
        return values.Length == 0 ? fallback : values.Average(selector);
    }

    private static double Clamp(double value) => Math.Clamp(value, 0, 1);
}

public interface IMusicalPeakDetector
{
    IReadOnlyList<MusicalPeak> Detect(
        MusicAnalysis analysis,
        IReadOnlyList<MusicSection> sections);
}

public sealed class MusicalPeakDetector : IMusicalPeakDetector
{
    public IReadOnlyList<MusicalPeak> Detect(
        MusicAnalysis analysis,
        IReadOnlyList<MusicSection> sections)
    {
        List<MusicalPeak> values = [];
        foreach (MusicSection section in sections)
        {
            values.Add(new MusicalPeak
            {
                Id = $"peak-section-{section.Index:D3}",
                Type = section.Type switch
                {
                    MusicSectionType.Drop => MusicalPeakType.DropStart,
                    MusicSectionType.Chorus => MusicalPeakType.ChorusStart,
                    _ => MusicalPeakType.SectionStart
                },
                TimeSeconds = section.StartSeconds,
                Strength = SectionStrength(section),
                Confidence = section.Confidence,
                SectionId = section.Id
            });
            foreach (MusicalAnchor anchor in section.Anchors)
            {
                MusicalPeakType type = anchor.Type switch
                {
                    MusicalAnchorType.Drop => MusicalPeakType.DropStart,
                    MusicalAnchorType.Downbeat => MusicalPeakType.Downbeat,
                    MusicalAnchorType.StrongBeat => MusicalPeakType.StrongBeat,
                    MusicalAnchorType.SectionBoundary => MusicalPeakType.PhraseStart,
                    _ => MusicalPeakType.Beat
                };
                values.Add(new MusicalPeak
                {
                    Id = $"peak-{anchor.Id}",
                    Type = type,
                    TimeSeconds = anchor.TimeSeconds,
                    Strength = Clamp(
                        anchor.Strength *
                        (IsHighEnergy(section.Type) ? 1 : 0.72)),
                    Confidence = Clamp(anchor.Confidence * section.Confidence),
                    SectionId = section.Id
                });
            }
        }
        for (int index = 1; index < analysis.Frames.Count - 1; index++)
        {
            MusicFrame previous = analysis.Frames[index - 1];
            MusicFrame frame = analysis.Frames[index];
            MusicFrame next = analysis.Frames[index + 1];
            MusicSection? section = sections.FirstOrDefault(value =>
                frame.TimeSeconds >= value.StartSeconds &&
                frame.TimeSeconds < value.EndSeconds);
            if (section is null)
                continue;
            if (frame.BassEnergy >= 0.72 &&
                frame.BassEnergy >= previous.BassEnergy &&
                frame.BassEnergy > next.BassEnergy)
            {
                values.Add(FramePeak(
                    $"peak-bass-{index:D5}",
                    MusicalPeakType.BassImpact,
                    frame,
                    section,
                    frame.BassEnergy));
            }
            if (frame.Energy >= 0.78 &&
                frame.Energy >= previous.Energy &&
                frame.Energy > next.Energy)
            {
                values.Add(FramePeak(
                    $"peak-energy-{index:D5}",
                    MusicalPeakType.EnergyPeak,
                    frame,
                    section,
                    frame.Energy));
            }
        }
        return values
            .Where(value => value.TimeSeconds >= 0 &&
                value.TimeSeconds <= analysis.Audio.DurationSeconds)
            .GroupBy(value => Math.Round(value.TimeSeconds / 0.03))
            .Select(group => group
                .OrderByDescending(value => Rank(value.Type))
                .ThenByDescending(value => value.Strength * value.Confidence)
                .ThenBy(value => value.Id, StringComparer.Ordinal)
                .First())
            .OrderBy(value => value.TimeSeconds)
            .ThenByDescending(value => Rank(value.Type))
            .ToArray();
    }

    public static bool IsAllowedPrimaryKillSection(MusicSectionType type) =>
        type is MusicSectionType.Drop or
            MusicSectionType.Chorus or
            MusicSectionType.HighEnergy;

    public static bool IsAllowedPrimaryKillSection(
        MusicSectionType type,
        bool relaxedEnergy) =>
        IsAllowedPrimaryKillSection(type) ||
        (relaxedEnergy &&
         type is not MusicSectionType.Intro and not MusicSectionType.Outro);

    private static MusicalPeak FramePeak(
        string id,
        MusicalPeakType type,
        MusicFrame frame,
        MusicSection section,
        double strength) =>
        new()
        {
            Id = id,
            Type = type,
            TimeSeconds = frame.TimeSeconds,
            Strength = Clamp(strength),
            Confidence = Clamp(
                0.55 +
                0.25 * frame.OnsetStrength +
                0.20 * frame.Novelty),
            SectionId = section.Id
        };

    private static double SectionStrength(MusicSection section) =>
        Clamp(
            0.35 * section.Energy +
            0.25 * section.BassEnergy +
            0.20 * section.RhythmicDensity +
            0.20 * section.DynamicContrast);

    private static int Rank(MusicalPeakType type) => type switch
    {
        MusicalPeakType.DropStart => 9,
        MusicalPeakType.ChorusStart => 8,
        MusicalPeakType.BassImpact => 7,
        MusicalPeakType.Downbeat => 6,
        MusicalPeakType.EnergyPeak => 5,
        MusicalPeakType.StrongBeat => 4,
        MusicalPeakType.PhraseStart => 3,
        MusicalPeakType.SectionStart => 2,
        _ => 1
    };

    private static bool IsHighEnergy(MusicSectionType type) =>
        IsAllowedPrimaryKillSection(type);

    private static double Clamp(double value) => Math.Clamp(value, 0, 1);
}

public interface IMusicNarrativeAnalyzer
{
    MusicNarrative Analyze(MusicAnalysis analysis);
}

public sealed class MusicNarrativeAnalyzer(
    IMusicSectionClassifier classifier,
    IMusicalPeakDetector peakDetector) : IMusicNarrativeAnalyzer
{
    public MusicNarrative Analyze(MusicAnalysis analysis)
    {
        IReadOnlyList<MusicSection> sections = classifier.Classify(analysis);
        IReadOnlyList<MusicalPeak> peaks = peakDetector.Detect(analysis, sections);
        List<string> warnings = [.. analysis.Warnings];
        if (!sections.Any(value => value.Type == MusicSectionType.BuildUp))
            warnings.Add("MUSIC_BUILDUP_NOT_DETECTED");
        if (!sections.Any(value =>
                MusicalPeakDetector.IsAllowedPrimaryKillSection(value.Type)))
            warnings.Add("MUSIC_HIGH_ENERGY_SECTION_NOT_DETECTED");
        if (analysis.Frames.Count == 0)
            warnings.Add("MUSIC_FRAME_TIMELINE_UNAVAILABLE");
        if (sections.Any(value => value.Confidence < 0.5))
            warnings.Add("MUSIC_SECTION_CONFIDENCE_LOW");
        return new MusicNarrative
        {
            DurationSeconds = analysis.Audio.DurationSeconds,
            Sections = sections,
            Peaks = peaks,
            Frames = analysis.Frames,
            Warnings = warnings.Distinct(StringComparer.Ordinal).ToArray()
        };
    }
}

public interface ICinematicDurationPolicy
{
    MovieDurationBudget Calculate(
        IReadOnlyList<SelectedHighlight> highlights,
        MovieDurationOptions options);
}

public sealed class CinematicDurationPolicy : ICinematicDurationPolicy
{
    public MovieDurationBudget Calculate(
        IReadOnlyList<SelectedHighlight> highlights,
        MovieDurationOptions options)
    {
        double highlightDuration = highlights.Sum(value => Math.Max(
            0,
            value.Bounds.PlannedEndSeconds -
            value.Bounds.PlannedStartSeconds));
        if (options.Selection != MovieDurationSelection.Auto)
        {
            double requested = Math.Min(
                options.MaximumMovieDurationSeconds,
                SelectionLimit(options.Selection));
            double fixedTarget = Math.Max(highlightDuration, requested);
            return new MovieDurationBudget(
                highlightDuration,
                Math.Max(0, fixedTarget - highlightDuration),
                fixedTarget,
                fixedTarget);
        }
        double broll = highlightDuration * options.MaximumBrollToHighlightRatio;
        double maximum = options.MaximumMovieDurationSeconds;
        if (highlightDuration < options.ShortHighlightThresholdSeconds)
        {
            maximum = Math.Min(
                options.MaximumShortMovieDurationSeconds,
                highlightDuration + broll);
        }
        double target = Math.Min(
            maximum,
            highlightDuration + Math.Min(broll, 10));
        target = Math.Max(highlightDuration, target);
        return new MovieDurationBudget(
            highlightDuration,
            Math.Min(broll, Math.Max(0, maximum - highlightDuration)),
            maximum,
            target);
    }

    private static double SelectionLimit(MovieDurationSelection selection) =>
        selection switch
        {
            MovieDurationSelection.Seconds15 => 15,
            MovieDurationSelection.Seconds30 => 30,
            MovieDurationSelection.Seconds45 => 45,
            MovieDurationSelection.Seconds60 => 60,
            _ => double.MaxValue
        };
}

public interface IMusicExcerptSelector
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Naming",
        "CA1716",
        Justification = "The Stage 8 public contract intentionally uses Select.")]
    MusicExcerptPlan Select(
        MusicNarrative narrative,
        IReadOnlyList<SelectedHighlight> highlights,
        MovieDurationOptions durationOptions);
}

public sealed class MusicExcerptSelector(
    ICinematicDurationPolicy durationPolicy) : IMusicExcerptSelector
{
    public const string RelaxedEnergyFallbackWarning =
        "MUSIC_EXCERPT_RELAXED_ENERGY_FALLBACK";

    public MusicExcerptPlan Select(
        MusicNarrative narrative,
        IReadOnlyList<SelectedHighlight> highlights,
        MovieDurationOptions durationOptions)
    {
        MovieDurationBudget budget = durationPolicy.Calculate(
            highlights,
            durationOptions);
        int requiredPeaks = highlights.Count;
        Candidate[] candidates = BuildCandidates(
                narrative,
                budget,
                requiredPeaks,
                relaxedEnergy: false)
            .OrderByDescending(value => value.Score)
            .ThenBy(value => value.Start)
            .ThenBy(value => value.End)
            .ToArray();
        bool FixedDuration(Candidate value) =>
            durationOptions.Selection == MovieDurationSelection.Auto ||
            Math.Abs(value.End - value.Start - budget.TargetSeconds) <= 0.05;
        Candidate? selected = candidates.FirstOrDefault(value =>
            value.Valid && FixedDuration(value));
        if (selected is null)
        {
            selected = BuildCandidates(
                    narrative,
                    budget,
                    requiredPeaks,
                    relaxedEnergy: true)
                .Concat(BuildCroppedCandidates(
                    narrative,
                    budget,
                    requiredPeaks))
                .Where(value => value.Valid && FixedDuration(value))
                .OrderByDescending(value => value.Score)
                .ThenBy(value => value.Start)
                .ThenBy(value => value.End)
                .FirstOrDefault();
        }
        if (durationOptions.Selection == MovieDurationSelection.Auto)
            selected ??= candidates.FirstOrDefault();
        if (selected is null)
        {
            double end = Math.Min(
                narrative.DurationSeconds,
                budget.MaximumTotalSeconds);
            return new MusicExcerptPlan
            {
                StartSeconds = 0,
                EndSeconds = end,
                SectionIds = [],
                Peaks = [],
                RequiredPeakCount = requiredPeaks,
                UsablePeakCount = 0,
                Score = 0,
                IsValid = false,
                ScoreBreakdown = new Dictionary<string, double>(),
                Warnings = ["NO_COMPATIBLE_MUSIC_EXCERPT"]
            };
        }
        List<string> warnings = [];
        if (!selected.HasBuildUp)
            warnings.Add("MUSIC_EXCERPT_BUILDUP_MISSING");
        if (!selected.HasHighEnergy)
            warnings.Add("MUSIC_EXCERPT_HIGH_ENERGY_MISSING");
        if (selected.RelaxedEnergy)
            warnings.Add(RelaxedEnergyFallbackWarning);
        if (selected.Peaks.Length < requiredPeaks)
            warnings.Add("MUSIC_EXCERPT_INSUFFICIENT_PEAKS");
        if (Math.Abs(selected.End - narrative.DurationSeconds) < 0.001 &&
            selected.Start <= 0.001 &&
            narrative.DurationSeconds > budget.TargetSeconds + 1)
            warnings.Add("FULL_SONG_EXPANSION_REJECTED");
        return new MusicExcerptPlan
        {
            StartSeconds = selected.Start,
            EndSeconds = selected.End,
            SectionIds = selected.Sections.Select(value => value.Id).ToArray(),
            Peaks = selected.Peaks,
            RequiredPeakCount = requiredPeaks,
            UsablePeakCount = selected.Peaks.Length,
            Score = selected.Score,
            IsValid = selected.Valid,
            ScoreBreakdown = selected.Breakdown,
            Warnings = warnings
        };
    }

    private static IEnumerable<Candidate> BuildCandidates(
        MusicNarrative narrative,
        MovieDurationBudget budget,
        int requiredPeaks,
        bool relaxedEnergy)
    {
        MusicSection[] sections = narrative.Sections
            .OrderBy(value => value.StartSeconds)
            .ToArray();
        for (int startIndex = 0; startIndex < sections.Length; startIndex++)
        {
            for (int endIndex = startIndex; endIndex < sections.Length; endIndex++)
            {
                MusicSection[] window = sections[startIndex..(endIndex + 1)];
                double start = window[0].StartSeconds;
                double end = window[^1].EndSeconds;
                double duration = end - start;
                if (duration + 0.001 < budget.HighlightDurationSeconds ||
                    duration > budget.MaximumTotalSeconds + 0.001)
                    continue;
                bool build = window.Any(value =>
                    value.Type is MusicSectionType.BuildUp or MusicSectionType.PreDrop);
                bool high = window.Any(value =>
                    MusicalPeakDetector.IsAllowedPrimaryKillSection(value.Type));
                MusicalPeak[] peaks = narrative.Peaks
                    .Where(value =>
                        value.TimeSeconds >= start &&
                        value.TimeSeconds <= end &&
                        window.Any(section =>
                            section.Id == value.SectionId &&
                            MusicalPeakDetector.IsAllowedPrimaryKillSection(
                                section.Type,
                                relaxedEnergy)))
                    .OrderByDescending(value =>
                        value.Strength * value.Confidence)
                    .ThenBy(value => value.TimeSeconds)
                    .OrderBy(value => value.TimeSeconds)
                    .ToArray();
                double peakCapacity = requiredPeaks == 0
                    ? 1
                    : Math.Min(1, peaks.Length / (double)requiredPeaks);
                double boundary = BoundaryQuality(window[0]) +
                    BoundaryQuality(window[^1]);
                double durationFit = 1 - Math.Min(
                    1,
                    Math.Abs(duration - budget.TargetSeconds) /
                    Math.Max(1, budget.TargetSeconds));
                double brollDemand = Math.Max(
                    0,
                    duration - budget.HighlightDurationSeconds) /
                    Math.Max(0.001, budget.MaximumBrollSeconds);
                Dictionary<string, double> breakdown = new(StringComparer.Ordinal)
                {
                    ["peakCapacity"] = peakCapacity * 45,
                    ["buildUp"] = build ? 18 : -30,
                    ["highEnergy"] = high ? 22 : -60,
                    ["boundary"] = boundary * 5,
                    ["durationFit"] = durationFit * 15,
                    ["brollPenalty"] = -Math.Max(0, brollDemand - 1) * 40,
                    ["fullSongPenalty"] =
                        start <= 0.001 &&
                        Math.Abs(end - narrative.DurationSeconds) <= 0.001 &&
                        duration > budget.TargetSeconds + 1
                            ? -35
                            : 0
                };
                bool valid =
                    (relaxedEnergy || (build && high)) &&
                    peaks.Length >= requiredPeaks &&
                    brollDemand <= 1.000001;
                yield return new Candidate(
                    start,
                    end,
                    window,
                    peaks,
                    build,
                    high,
                    relaxedEnergy,
                    valid,
                    breakdown.Values.Sum(),
                    breakdown);
            }
        }
    }

    private static IEnumerable<Candidate> BuildCroppedCandidates(
        MusicNarrative narrative,
        MovieDurationBudget budget,
        int requiredPeaks)
    {
        double duration = Math.Clamp(
            budget.TargetSeconds,
            budget.HighlightDurationSeconds,
            budget.MaximumTotalSeconds);
        duration = Math.Min(duration, narrative.DurationSeconds);
        if (duration <= 0)
            yield break;

        MusicalPeak[] anchors = narrative.Peaks
            .Where(value =>
                narrative.Sections.Any(section =>
                    section.Id == value.SectionId &&
                    MusicalPeakDetector.IsAllowedPrimaryKillSection(
                        section.Type,
                        relaxedEnergy: true)))
            .OrderBy(value => value.TimeSeconds)
            .ThenBy(value => value.Id, StringComparer.Ordinal)
            .ToArray();
        foreach (MusicalPeak anchor in anchors)
        {
            double start = Math.Clamp(
                anchor.TimeSeconds - duration / 2,
                0,
                Math.Max(0, narrative.DurationSeconds - duration));
            double end = start + duration;
            MusicSection[] window = narrative.Sections
                .Where(value =>
                    value.EndSeconds > start &&
                    value.StartSeconds < end)
                .OrderBy(value => value.StartSeconds)
                .ToArray();
            MusicalPeak[] peaks = anchors
                .Where(value =>
                    value.TimeSeconds >= start &&
                    value.TimeSeconds <= end)
                .OrderByDescending(value =>
                    value.Strength * value.Confidence)
                .ThenBy(value => value.TimeSeconds)
                .OrderBy(value => value.TimeSeconds)
                .ToArray();
            bool build = window.Any(value =>
                value.Type is MusicSectionType.BuildUp or
                    MusicSectionType.PreDrop);
            bool high = window.Any(value =>
                MusicalPeakDetector.IsAllowedPrimaryKillSection(value.Type));
            double brollDemand = Math.Max(
                0,
                duration - budget.HighlightDurationSeconds) /
                Math.Max(0.001, budget.MaximumBrollSeconds);
            double peakCapacity = requiredPeaks == 0
                ? 1
                : Math.Min(1, peaks.Length / (double)requiredPeaks);
            Dictionary<string, double> breakdown = new(StringComparer.Ordinal)
            {
                ["peakCapacity"] = peakCapacity * 45,
                ["croppedWindow"] = 20,
                ["buildUp"] = build ? 18 : 0,
                ["highEnergy"] = high ? 22 : 0,
                ["durationFit"] = 15,
                ["brollPenalty"] = -Math.Max(0, brollDemand - 1) * 40
            };
            yield return new Candidate(
                start,
                end,
                window,
                peaks,
                build,
                high,
                RelaxedEnergy: true,
                Valid:
                    window.Length > 0 &&
                    peaks.Length >= requiredPeaks &&
                    brollDemand <= 1.000001,
                breakdown.Values.Sum(),
                breakdown);
        }
    }

    private static double BoundaryQuality(MusicSection section) =>
        section.Anchors.Any(value =>
            value.Type is MusicalAnchorType.Downbeat or
                MusicalAnchorType.SectionBoundary or
                MusicalAnchorType.Drop)
            ? 1
            : 0.5;

    private sealed record Candidate(
        double Start,
        double End,
        MusicSection[] Sections,
        MusicalPeak[] Peaks,
        bool HasBuildUp,
        bool HasHighEnergy,
        bool RelaxedEnergy,
        bool Valid,
        double Score,
        IReadOnlyDictionary<string, double> Breakdown);
}
