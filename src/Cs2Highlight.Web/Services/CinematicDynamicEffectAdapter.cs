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
    public const string PlannerVersion = "10.1";

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
        List<EffectCue> effects = segment.Effects
            .Select((value, index) => Cue(
                value,
                match,
                seed + index,
                index))
            .ToList();
        foreach (EffectCue impact in KillImpactCues(
                     highlight,
                     segment,
                     match,
                     intensity,
                     seed + effects.Count))
        {
            if (effects.All(value => value.Type != impact.Type))
                effects.Add(impact);
        }
        EffectCue? transition = TransitionCue(
            highlight,
            segment,
            cinematic,
            match,
            seed + effects.Count,
            intensity);
        if (transition is not null)
            effects.Add(transition);
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
            Effects = effects
                .OrderBy(value => value.StartSeconds)
                .ThenByDescending(value => value.Priority)
                .ThenBy(value => value.Id, StringComparer.Ordinal)
                .ToArray(),
            RejectedEffects = [],
            Warnings = effects.Count == 0
                ? ["CINEMATIC_EFFECT_NOT_MOTIVATED"]
                : [],
            Scores = []
        };
    }

    private static EffectCue Cue(
        MotivatedEffectDirective directive,
        HighlightPeakMatch match,
        int seed,
        int index)
    {
        VideoEffectType type = directive.EffectType switch
        {
            "SmoothZoom" => VideoEffectType.SmoothZoom,
            "PunchZoom" => VideoEffectType.PunchZoom,
            "CrashZoom" => VideoEffectType.CrashZoom,
            "OffsetZoom" => VideoEffectType.OffsetZoom,
            "MicroShake" => VideoEffectType.MicroShake,
            "RecoilShake" => VideoEffectType.RecoilShake,
            "DirectionalMotionBlur" =>
                VideoEffectType.DirectionalMotionBlur,
            "ZoomBlur" => VideoEffectType.ZoomBlur,
            "FrameEcho" => VideoEffectType.FrameEcho,
            "RgbSplit" => VideoEffectType.RgbSplit,
            "HitStop" => VideoEffectType.HitStop,
            "LensWarpPulse" => VideoEffectType.LensWarpPulse,
            "RollBurst" => VideoEffectType.RollBurst,
            "FlashAccent" => VideoEffectType.FlashAccent,
            "VignettePulse" => VideoEffectType.VignettePulse,
            _ => throw new InvalidOperationException(
                $"CINEMATIC_EFFECT_UNSUPPORTED:{directive.EffectType}")
        };
        return new EffectCue
        {
            Id = $"cinematic-{match.HighlightId}-{index:D2}-{type}",
            Type = type,
            Category = Category(type),
            Role = index == 0
                ? EffectRole.Primary
                : EffectRole.Accent,
            StartSeconds = directive.StartSeconds,
            EndSeconds = directive.EndSeconds,
            Intensity = directive.Intensity,
            Priority = 100,
            Seed = seed,
            Parameters = Parameters(type, directive.Intensity, seed),
            SourceKillEventId = match.HighlightId,
            SourceMusicalAnchorId = match.Peak.Id,
            Reason = directive.Reason.ToString(),
            RenderCost = EffectRenderCost.Low
        };
    }

    private static EffectCue[] KillImpactCues(
        GenerationHighlight highlight,
        CinematicSequenceSegment segment,
        HighlightPeakMatch match,
        EffectIntensity intensity,
        int seed)
    {
        if (intensity == EffectIntensity.Minimal)
            return [];
        double duration = Math.Max(
            0.001,
            segment.OutputEndSeconds - segment.OutputStartSeconds);
        double kill = Math.Clamp(
            match.PlannedKillSeconds - segment.OutputStartSeconds,
            0,
            duration);
        bool strong = intensity == EffectIntensity.Strong;
        List<EffectCue> cues = [];
        cues.Add(ImpactCue(
            highlight,
            match,
            VideoEffectType.PunchZoom,
            EffectRole.Primary,
            Window(kill, 0.10, 0.16, duration),
            strong ? 0.72 : 0.54,
            seed));
        if (strong)
        {
            VideoEffectType blur = Math.Abs(seed) % 2 == 0
                ? VideoEffectType.DirectionalMotionBlur
                : VideoEffectType.ZoomBlur;
            cues.Add(ImpactCue(
                highlight,
                match,
                blur,
                EffectRole.Accent,
                Window(kill, 0.14, 0.05, duration),
                0.48,
                seed + 1));
        }
        cues.Add(ImpactCue(
            highlight,
            match,
            VideoEffectType.FlashAccent,
            EffectRole.Accent,
            Window(kill, 0.015, 0.09, duration),
            strong ? 0.48 : 0.32,
            seed + 2));
        if (strong && segment.Role == CinematicSequenceRole.PeakHighlight)
        {
            cues.Add(ImpactCue(
                highlight,
                match,
                VideoEffectType.HitStop,
                EffectRole.Primary,
                Window(kill, 0.012, 0.08, duration),
                0.62,
                seed + 3));
        }
        return cues
            .Where(value => value.EndSeconds - value.StartSeconds >= 0.04)
            .ToArray();
    }

    private static EffectCue ImpactCue(
        GenerationHighlight highlight,
        HighlightPeakMatch match,
        VideoEffectType type,
        EffectRole role,
        (double Start, double End) window,
        double intensity,
        int seed) => new()
        {
            Id = $"cinematic-{highlight.HighlightId}-kill-{type}",
            Type = type,
            Category = Category(type),
            Role = role,
            StartSeconds = window.Start,
            EndSeconds = window.End,
            Intensity = intensity,
            Priority = role == EffectRole.Primary ? 110 : 90,
            Seed = seed,
            Parameters = Parameters(type, intensity, seed),
            SourceKillEventId = highlight.HighlightId,
            SourceMusicalAnchorId = match.Peak.Id,
            Reason = type == VideoEffectType.HitStop
                ? MotivatedEffectReason.FinalKill.ToString()
                : MotivatedEffectReason.MusicPeak.ToString(),
            RenderCost = type is VideoEffectType.DirectionalMotionBlur or
                VideoEffectType.ZoomBlur
                    ? EffectRenderCost.Medium
                    : EffectRenderCost.Low
        };

    private static (double Start, double End) Window(
        double center,
        double before,
        double after,
        double duration)
    {
        double start = Math.Clamp(center - before, 0, duration);
        double end = Math.Clamp(center + after, 0, duration);
        if (end - start >= 0.04)
            return (start, end);
        return (Math.Max(0, end - 0.04), end);
    }

    private static VideoEffectCategory Category(VideoEffectType type) =>
        type switch
        {
            VideoEffectType.SmoothZoom or
            VideoEffectType.PunchZoom or
            VideoEffectType.CrashZoom or
            VideoEffectType.OffsetZoom => VideoEffectCategory.Zoom,
            VideoEffectType.MicroShake or
            VideoEffectType.RecoilShake or
            VideoEffectType.RollBurst => VideoEffectCategory.Motion,
            VideoEffectType.DirectionalMotionBlur or
            VideoEffectType.ZoomBlur =>
                VideoEffectCategory.Blur,
            VideoEffectType.FrameEcho => VideoEffectCategory.Temporal,
            VideoEffectType.RgbSplit => VideoEffectCategory.Color,
            VideoEffectType.HitStop => VideoEffectCategory.Time,
            VideoEffectType.LensWarpPulse =>
                VideoEffectCategory.Distortion,
            VideoEffectType.FlashAccent or
            VideoEffectType.VignettePulse =>
                VideoEffectCategory.Accent,
            VideoEffectType.FadeTransition or
            VideoEffectType.HardCut or
            VideoEffectType.FlashCut or
            VideoEffectType.WhipPan or
            VideoEffectType.WhipZoom =>
                VideoEffectCategory.Transition,
            _ => throw new InvalidOperationException(
                $"CINEMATIC_EFFECT_CATEGORY_UNSUPPORTED:{type}")
        };

    private static Dictionary<string, double> Parameters(
        VideoEffectType type,
        double intensity,
        int seed)
    {
        double clamped = Math.Clamp(intensity, 0, 1);
        return type switch
        {
            VideoEffectType.SmoothZoom =>
                new Dictionary<string, double>
                {
                    ["scale"] = 1.025 + 0.035 * clamped,
                    ["centerX"] = seed % 2 == 0 ? 0.47 : 0.53,
                    ["centerY"] = 0.48
                },
            VideoEffectType.OffsetZoom =>
                new Dictionary<string, double>
                {
                    ["scale"] = 1.045 + 0.055 * clamped,
                    ["centerX"] = seed % 2 == 0 ? 0.43 : 0.57,
                    ["centerY"] = seed % 3 == 0 ? 0.45 : 0.52
                },
            VideoEffectType.PunchZoom =>
                new Dictionary<string, double>
                {
                    ["scale"] = 1.055 + 0.075 * clamped,
                    ["centerX"] = 0.5,
                    ["centerY"] = 0.48
                },
            VideoEffectType.CrashZoom =>
                new Dictionary<string, double>
                {
                    ["scale"] = 1.09 + 0.06 * clamped,
                    ["centerX"] = 0.5,
                    ["centerY"] = 0.47
                },
            VideoEffectType.MicroShake =>
                new Dictionary<string, double>
                {
                    ["amplitudePixels"] = 2 + 6 * clamped,
                    ["impulses"] = 3 + seed % 3
                },
            VideoEffectType.RecoilShake =>
                new Dictionary<string, double>
                {
                    ["amplitudePixels"] = 5 + 8 * clamped,
                    ["impulses"] = 2 + seed % 2
                },
            VideoEffectType.DirectionalMotionBlur =>
                new Dictionary<string, double>
                {
                    ["frames"] = 3 + (int)Math.Round(3 * clamped)
                },
            VideoEffectType.ZoomBlur =>
                new Dictionary<string, double>
                {
                    ["sigma"] = 3.5 + 7 * clamped
                },
            VideoEffectType.RgbSplit =>
                new Dictionary<string, double>
                {
                    ["redOffsetX"] = 1 + (int)Math.Round(3 * clamped),
                    ["blueOffsetX"] = -1 - (int)Math.Round(3 * clamped)
                },
            VideoEffectType.FrameEcho =>
                new Dictionary<string, double>
                {
                    ["frames"] = 2 + (int)Math.Round(2 * clamped)
                },
            VideoEffectType.LensWarpPulse =>
                new Dictionary<string, double>
                {
                    ["k1"] = -0.025 - 0.045 * clamped
                },
            VideoEffectType.RollBurst =>
                new Dictionary<string, double>
                {
                    ["angleDegrees"] =
                        (seed % 2 == 0 ? -1 : 1) *
                        (0.45 + 0.85 * clamped)
                },
            VideoEffectType.FlashAccent =>
                new Dictionary<string, double>
                {
                    ["opacity"] = 0.14 + 0.28 * clamped
                },
            _ => new Dictionary<string, double>()
        };
    }

    private static EffectCue? TransitionCue(
        GenerationHighlight highlight,
        CinematicSequenceSegment segment,
        CinematicMoviePlan cinematic,
        HighlightPeakMatch match,
        int seed,
        EffectIntensity intensity)
    {
        if (intensity == EffectIntensity.Minimal ||
            segment.OutputEndSeconds >=
                cinematic.TargetDurationSeconds - 0.02)
        {
            return null;
        }

        bool sniper = highlight.WeaponSequenceJson.Contains(
                "awp",
                StringComparison.OrdinalIgnoreCase) ||
            highlight.WeaponSequenceJson.Contains(
                "ssg08",
                StringComparison.OrdinalIgnoreCase);
        bool flash = ContainsTag(highlight, "flash") ||
            ContainsTag(highlight, "flashbang");
        bool smoke = ContainsTag(highlight, "smoke");
        bool fastMovement = ContainsTag(highlight, "fast_movement") ||
            ContainsTag(highlight, "weapon_swap");
        int variant = Math.Abs(seed) % 6;
        VideoEffectType type = segment.Role ==
                CinematicSequenceRole.PeakHighlight
            ? VideoEffectType.FadeTransition
            : smoke
                ? VideoEffectType.FadeTransition
                : flash ||
                    sniper ||
                    highlight.HeadshotCount > 0 && seed % 3 == 0
                ? VideoEffectType.FlashCut
                : fastMovement
                    ? VideoEffectType.WhipPan
                    : highlight.KillCount > 1
                        ? VideoEffectType.WhipZoom
                        : variant switch
                        {
                            0 or 5 => VideoEffectType.HardCut,
                            1 => VideoEffectType.WhipPan,
                            2 => VideoEffectType.FlashCut,
                            3 => VideoEffectType.FadeTransition,
                            _ => VideoEffectType.WhipZoom
                        };
        double duration = type switch
        {
            VideoEffectType.HardCut => 0.01,
            VideoEffectType.FlashCut => 0.09,
            VideoEffectType.FadeTransition => 0.20,
            _ => 0.14
        };
        double localDuration =
            segment.OutputEndSeconds - segment.OutputStartSeconds;
        double start = Math.Max(0, localDuration - duration);
        return new EffectCue
        {
            Id = $"cinematic-{match.HighlightId}-transition-{type}",
            Type = type,
            Category = VideoEffectCategory.Transition,
            Role = EffectRole.Transition,
            StartSeconds = start,
            EndSeconds = localDuration,
            Intensity = intensity == EffectIntensity.Strong ? 0.56 : 0.38,
            Priority = 40,
            Seed = seed,
            Parameters = new Dictionary<string, double>
            {
                ["direction"] = seed % 2 == 0 ? -1 : 1
            },
            SourceMusicalAnchorId = match.Peak.Id,
            Reason = MotivatedEffectReason.CameraTransition.ToString(),
            RenderCost = type is
                VideoEffectType.WhipPan or
                VideoEffectType.WhipZoom
                    ? EffectRenderCost.Medium
                    : EffectRenderCost.Low
        };
    }

    private static bool ContainsTag(
        GenerationHighlight highlight,
        string tag) =>
        highlight.TagsJson.Contains(
            tag,
            StringComparison.OrdinalIgnoreCase);
}
