namespace Cs2Highlight.Music;

public static class EffectRarityPolicy
{
    public const string Version = "1.0";
    public const double MinimumLensWarpSeconds = 0.08;
    public const double MaximumLensWarpSeconds = 0.15;
    public const int MaximumLensWarpCount = 2;

    public static IReadOnlyList<CinematicSequenceSegment> Apply(
        IReadOnlyList<CinematicSequenceSegment> segments,
        out EffectRarityReport report)
    {
        List<CinematicSequenceSegment> result = [];
        List<EffectRarityEntry> entries = [];
        List<string> violations = [];
        int lensWarpCount = 0;
        bool previousHighlightUsedRare = false;
        foreach (CinematicSequenceSegment segment in segments)
        {
            bool highlight = segment.HighlightId is not null;
            List<MotivatedEffectDirective> accepted = [];
            bool currentUsedRare = false;
            foreach (MotivatedEffectDirective source in segment.Effects)
            {
                EffectRarityTier tier = Tier(source.EffectType);
                MotivatedEffectDirective effect = source;
                string decision = "accepted";
                bool keep = true;
                if (tier == EffectRarityTier.Rare)
                {
                    bool editoriallyMotivated =
                        segment.Role == CinematicSequenceRole.PeakHighlight ||
                        source.Reason is MotivatedEffectReason.FinalKill or
                            MotivatedEffectReason.BassImpact;
                    if (!editoriallyMotivated)
                    {
                        keep = false;
                        decision = "rare effect lacks climax/interval motivation";
                    }
                    else if (previousHighlightUsedRare)
                    {
                        keep = false;
                        decision = "rare effect rejected on adjacent highlight";
                    }
                    else if (accepted.Any(value =>
                        Tier(value.EffectType) == EffectRarityTier.Rare))
                    {
                        keep = false;
                        decision = "one rare effect maximum per highlight";
                    }
                }
                if (string.Equals(
                        effect.EffectType,
                        "LensWarpPulse",
                        StringComparison.Ordinal))
                {
                    if (lensWarpCount >= MaximumLensWarpCount)
                    {
                        keep = false;
                        decision = "lens warp film budget exhausted";
                    }
                    else
                    {
                        double center =
                            (effect.StartSeconds + effect.EndSeconds) / 2;
                        double duration = Math.Clamp(
                            effect.EndSeconds - effect.StartSeconds,
                            MinimumLensWarpSeconds,
                            MaximumLensWarpSeconds);
                        effect = effect with
                        {
                            StartSeconds = Math.Max(0, center - duration / 2),
                            EndSeconds = Math.Min(
                                segment.OutputEndSeconds -
                                segment.OutputStartSeconds,
                                center + duration / 2),
                            Intensity = Math.Clamp(effect.Intensity, 0.10, 0.50)
                        };
                    }
                }
                if (keep && ConflictsWithAccepted(effect, accepted))
                {
                    keep = false;
                    decision = "conflicts with stronger accepted treatment";
                }
                if (keep)
                {
                    accepted.Add(effect);
                    currentUsedRare |= tier == EffectRarityTier.Rare;
                    if (effect.EffectType == "LensWarpPulse")
                        lensWarpCount++;
                }
                entries.Add(new EffectRarityEntry(
                    segment.Id,
                    effect.EffectType,
                    tier,
                    Math.Round(
                        Math.Max(
                            0,
                            effect.EndSeconds - effect.StartSeconds) * 1000,
                        3),
                    keep,
                    decision));
            }
            result.Add(segment with { Effects = accepted });
            if (highlight)
                previousHighlightUsedRare = currentUsedRare;
        }
        if (lensWarpCount > MaximumLensWarpCount)
            violations.Add("LENS_WARP_COUNT_EXCEEDED");
        if (entries.Any(value =>
                value.Accepted &&
                value.EffectType == "LensWarpPulse" &&
                value.DurationMilliseconds >
                MaximumLensWarpSeconds * 1000 + 0.001))
            violations.Add("LENS_WARP_DURATION_EXCEEDED");
        report = new EffectRarityReport(
            "1.0",
            entries.Count(value =>
                value.Accepted && value.Tier == EffectRarityTier.Rare),
            lensWarpCount,
            entries,
            violations);
        return result;
    }

    public static EffectRarityTier Tier(string effectType) =>
        effectType switch
        {
            "LensWarpPulse" or "FishEye" or "BulletPath" or
            "RollBurst" or "RgbSplit" or "CrashZoom" =>
                EffectRarityTier.Rare,
            "PunchZoom" or "DirectionalMotionBlur" or "ZoomBlur" or
            "FrameEcho" or "FlashAccent" or "HitStop" =>
                EffectRarityTier.Occasional,
            _ => EffectRarityTier.Common
        };

    private static bool ConflictsWithAccepted(
        MotivatedEffectDirective effect,
        IReadOnlyList<MotivatedEffectDirective> accepted) =>
        effect.EffectType == "LensWarpPulse" && accepted.Any(value =>
            value.EffectType is "RollBurst" or "CrashZoom") ||
        effect.EffectType is "RollBurst" or "CrashZoom" && accepted.Any(
            value => value.EffectType == "LensWarpPulse");
}
