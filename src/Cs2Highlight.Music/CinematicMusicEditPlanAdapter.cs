using Cs2Highlight.Analysis;

namespace Cs2Highlight.Music;

public interface ICinematicMusicEditPlanAdapter
{
    MusicEditPlan Create(
        string generationId,
        string musicFile,
        CinematicMoviePlan cinematic,
        IReadOnlyList<SelectedHighlight> highlights);
}

public sealed class CinematicMusicEditPlanAdapter :
    ICinematicMusicEditPlanAdapter
{
    public MusicEditPlan Create(
        string generationId,
        string musicFile,
        CinematicMoviePlan cinematic,
        IReadOnlyList<SelectedHighlight> highlights)
    {
        Dictionary<string, SelectedHighlight> byId =
            highlights.ToDictionary(value => value.Id, StringComparer.Ordinal);
        Dictionary<string, HighlightPeakMatch> matches =
            cinematic.HighlightMatches.ToDictionary(
                value => value.HighlightId,
                StringComparer.Ordinal);
        List<MusicEditSegment> segments = [];
        foreach (CinematicSequenceSegment cinematicSegment in cinematic.Segments
                     .Where(value => value.HighlightId is not null)
                     .OrderBy(value => value.OutputStartSeconds)
                     .ThenBy(value => value.Id, StringComparer.Ordinal))
        {
            SelectedHighlight highlight = byId[cinematicSegment.HighlightId!];
            HighlightPeakMatch match = matches[highlight.Id];
            MusicalAnchor anchor = new(
                match.Peak.Id,
                AnchorType(match.Peak.Type),
                match.PlannedPeakSeconds,
                match.Peak.Strength,
                match.Peak.Confidence);
            segments.Add(new MusicEditSegment(
                segments.Count + 1,
                highlight.Id,
                highlight.Highlight.Type,
                match.HighlightImportance,
                highlight.Bounds.SafeStartSeconds,
                Math.Max(
                    highlight.Bounds.SafeEndSeconds,
                    highlight.Bounds.PlannedEndSeconds),
                highlight.Bounds.PrimaryKillSeconds,
                anchor,
                cinematicSegment.OutputStartSeconds,
                match.PlannedPeakSeconds,
                cinematicSegment.TimeWarp,
                "Cut",
                "Cut",
                new MusicEditScoreBreakdown(
                    match.Score,
                    0,
                    0,
                    0,
                    0,
                    match.Score),
                match.Warnings));
        }
        if (segments.Count != highlights.Count)
            throw new InvalidOperationException(
                "CINEMATIC_HIGHLIGHT_ADAPTER_INCOMPLETE");
        return new MusicEditPlan(
            "2.0",
            generationId,
            musicFile,
            cinematic.MusicExcerpt.DurationSeconds,
            MovieStyle.CinematicDirector,
            MusicSyncIntensity.Expressive,
            segments,
            cinematic.Warnings)
        {
            MusicStartSeconds = cinematic.MusicExcerpt.StartSeconds
        };
    }

    private static MusicalAnchorType AnchorType(MusicalPeakType type) =>
        type switch
        {
            MusicalPeakType.DropStart => MusicalAnchorType.Drop,
            MusicalPeakType.Downbeat => MusicalAnchorType.Downbeat,
            MusicalPeakType.StrongBeat or MusicalPeakType.BassImpact or
                MusicalPeakType.EnergyPeak => MusicalAnchorType.StrongBeat,
            MusicalPeakType.PhraseStart or MusicalPeakType.SectionStart or
                MusicalPeakType.ChorusStart =>
                MusicalAnchorType.SectionBoundary,
            _ => MusicalAnchorType.Beat
        };
}
