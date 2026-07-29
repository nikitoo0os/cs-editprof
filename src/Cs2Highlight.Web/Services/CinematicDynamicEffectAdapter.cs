using Cs2Highlight.Music;
using Cs2Highlight.Web.Domain;

namespace Cs2Highlight.Web.Services;

public interface ICinematicDynamicEffectAdapter
{
    DynamicEffectPlan Create(
        string generationId,
        GenerationHighlight highlight,
        CinematicMoviePlan cinematic,
        EffectIntensity intensity);
}

public sealed class CinematicDynamicEffectAdapter(
    IEffectSeedProvider seeds) : ICinematicDynamicEffectAdapter
{
    public const string PlannerVersion = "8.0";

    public DynamicEffectPlan Create(
        string generationId,
        GenerationHighlight highlight,
        CinematicMoviePlan cinematic,
        EffectIntensity intensity)
    {
        CinematicSequenceSegment segment = cinematic.Segments
            .Single(value => string.Equals(
                value.HighlightId,
                highlight.HighlightId,
                StringComparison.Ordinal));
        HighlightPeakMatch match = cinematic.HighlightMatches
            .Single(value => string.Equals(
                value.HighlightId,
                highlight.HighlightId,
                StringComparison.Ordinal));
        int seed = seeds.CreateSeed(
            generationId,
            highlight.HighlightId,
            -1,
            PlannerVersion);
        EffectCue[] effects = segment.Effects
            .Take(1)
            .Select((value, index) => Cue(
                value,
                match,
                seed + index))
            .ToArray();
        return new DynamicEffectPlan
        {
            SchemaVersion = "1.0",
            PlannerVersion = PlannerVersion,
            GenerationId = generationId,
            HighlightId = highlight.HighlightId,
            ClipId = highlight.HighlightId,
            Style = MovieStyle.CinematicDirector,
            Intensity = intensity,
            DeterministicSeed = seed,
            Effects = effects,
            RejectedEffects = [],
            Warnings = effects.Length == 0
                ? ["CINEMATIC_EFFECT_NOT_MOTIVATED"]
                : [],
            Scores = []
        };
    }

    private static EffectCue Cue(
        MotivatedEffectDirective directive,
        HighlightPeakMatch match,
        int seed)
    {
        VideoEffectType type = directive.EffectType switch
        {
            "SmoothZoom" => VideoEffectType.SmoothZoom,
            "OffsetZoom" => VideoEffectType.OffsetZoom,
            "HitStop" => VideoEffectType.HitStop,
            _ => throw new InvalidOperationException(
                $"CINEMATIC_EFFECT_UNSUPPORTED:{directive.EffectType}")
        };
        return new EffectCue
        {
            Id = $"cinematic-{match.HighlightId}-{type}",
            Type = type,
            Category = type == VideoEffectType.HitStop
                ? VideoEffectCategory.Temporal
                : VideoEffectCategory.Zoom,
            Role = EffectRole.Primary,
            StartSeconds = directive.StartSeconds,
            EndSeconds = directive.EndSeconds,
            Intensity = directive.Intensity,
            Priority = 100,
            Seed = seed,
            Parameters = new Dictionary<string, double>(),
            SourceMusicalAnchorId = match.Peak.Id,
            Reason = directive.Reason.ToString(),
            RenderCost = EffectRenderCost.Low
        };
    }
}
