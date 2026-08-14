using Cs2Highlight.Music;
using Cs2Highlight.Web.Domain;

namespace Cs2Highlight.Web.Services;

public sealed record GapMaterialCandidate(
    string Id,
    long DemoId,
    int RoundNumber,
    BrollCandidateType Type,
    long StartTick,
    long EndTick,
    int TickRate,
    double CinematicScore,
    double MovementScore,
    double ActionDensity)
{
    public string SourceInterval => $"{DemoId}:{StartTick}-{EndTick}";
    public double DurationSeconds =>
        Math.Max(0, EndTick - StartTick) / (double)Math.Max(1, TickRate);
}

public sealed record GapHighlightContext(
    string HighlightId,
    long DemoId,
    int RoundNumber,
    long StartTick,
    long PrimaryKillTick,
    long SafeEndTick,
    int TickRate);

public sealed record GapMaterialDecision(
    GapMaterialCandidate? Candidate,
    int NarrativePriority,
    string Rationale,
    LocalRegionOutcome Outcome,
    bool UsePovContinuity,
    bool ShortenExcerpt,
    IReadOnlyList<string> Warnings);

public static class MeaningfulGapPolicy
{
    public const string Version = "1.1";
    public const double MinimumOrdinaryShotSeconds = 1.5;
    public const double MinimumFreeCameraShotSeconds = 1.5;
    public const double MinimumExtendedFreeCameraSpeed = 0.50;
    public const double MaximumExplicitDurationAdjustmentSeconds = 5.0;
    public const double MaximumPovContinuitySeconds = 1.5;

    public static bool CanExtendFreeCamera(
        double sourceDurationSeconds,
        double outputDurationSeconds) =>
        sourceDurationSeconds > 0 &&
        outputDurationSeconds >= sourceDurationSeconds &&
        sourceDurationSeconds / outputDurationSeconds >=
            MinimumExtendedFreeCameraSpeed;

    public static bool CanShortenExplicitDuration(
        double missingDurationSeconds) =>
        missingDurationSeconds >= 0 &&
        missingDurationSeconds <=
            MaximumExplicitDurationAdjustmentSeconds;

    public static GapMaterialDecision Select(
        IReadOnlyList<GapMaterialCandidate> candidates,
        GapHighlightContext? previous,
        GapHighlightContext? next,
        TimelineGapRole role,
        double availableDurationSeconds,
        IReadOnlySet<string> usedSourceIntervals)
    {
        if (availableDurationSeconds < 0.18)
        {
            return new GapMaterialDecision(
                null,
                0,
                "micro-gap closed by adjacent shot boundaries",
                LocalRegionOutcome.Natural,
                false,
                false,
                ["MICRO_GAP_BOUNDARY_ADJUSTMENT"]);
        }

        GapMaterialCandidate? selected = candidates
            .Where(value =>
                !usedSourceIntervals.Contains(value.SourceInterval) &&
                value.DurationSeconds >= MinimumOrdinaryShotSeconds &&
                IsEditoriallyUsable(value) &&
                IsContextuallyUsable(value, previous))
            .Select(value => new
            {
                Candidate = value,
                Priority = Priority(value, previous, next),
                Score = EditorialScore(value, previous, next)
            })
            .OrderBy(value => value.Priority)
            .ThenByDescending(value => value.Score)
            .ThenBy(value => value.Candidate.SourceInterval,
                StringComparer.Ordinal)
            .ThenBy(value => value.Candidate.Id, StringComparer.Ordinal)
            .Select(value => value.Candidate)
            .FirstOrDefault();
        if (selected is not null)
        {
            int priority = Priority(selected, previous, next);
            return new GapMaterialDecision(
                selected,
                priority,
                Rationale(priority),
                LocalRegionOutcome.Natural,
                false,
                false,
                []);
        }

        if (availableDurationSeconds < 0.50)
        {
            return new GapMaterialDecision(
                null,
                0,
                "no meaningful insert; close the short gap with retiming",
                LocalRegionOutcome.Retiming,
                false,
                false,
                ["SHORT_GAP_RETIMING_REQUIRED"]);
        }
        if (role == TimelineGapRole.Outro)
        {
            return new GapMaterialDecision(
                null,
                12,
                "meaningful material exhausted; trim the excerpt tail",
                LocalRegionOutcome.ShortenedExcerpt,
                false,
                true,
                ["EXCERPT_SHORTENED_INSTEAD_OF_PADDING"]);
        }
        if (availableDurationSeconds <= MaximumPovContinuitySeconds &&
            (previous is not null || next is not null))
        {
            return new GapMaterialDecision(
                null,
                11,
                "bounded POV continuity fallback",
                LocalRegionOutcome.CameraFallback,
                true,
                false,
                ["POV_CONTINUITY_FALLBACK"]);
        }
        return new GapMaterialDecision(
            null,
            12,
            "locked anchors leave more time than meaningful material can cover",
            LocalRegionOutcome.Invalid,
            false,
            false,
            ["INSUFFICIENT_MEANINGFUL_MATERIAL"]);
    }

    public static int Priority(
        GapMaterialCandidate candidate,
        GapHighlightContext? previous,
        GapHighlightContext? next)
    {
        long continuityWindow = Math.Max(1, candidate.TickRate * 6L);
        bool moving = candidate.Type is
            BrollCandidateType.PlayerApproach or
            BrollCandidateType.PlayerRotation or
            BrollCandidateType.SideMovement or
            BrollCandidateType.RearMovement or
            BrollCandidateType.PlayerJump or
            BrollCandidateType.TeamMovement;
        if (moving && next is not null &&
            candidate.DemoId == next.DemoId &&
            candidate.RoundNumber == next.RoundNumber &&
            candidate.EndTick <= next.StartTick &&
            next.StartTick - candidate.EndTick <= continuityWindow)
            return 1;
        if (moving && previous is not null &&
            candidate.DemoId == previous.DemoId &&
            candidate.RoundNumber == previous.RoundNumber &&
            candidate.StartTick >= previous.SafeEndTick &&
            candidate.StartTick - previous.SafeEndTick <= continuityWindow)
            return 2;
        return candidate.Type switch
        {
            BrollCandidateType.PlayerApproach => 3,
            BrollCandidateType.PostFightExit or
            BrollCandidateType.PlayerRotation or
            BrollCandidateType.PlayerJump or
            BrollCandidateType.RearMovement => 4,
            BrollCandidateType.TeamMovement => 5,
            BrollCandidateType.TeamSetup or
            BrollCandidateType.PreFightSetup => 6,
            BrollCandidateType.UtilityPreparation or
            BrollCandidateType.UtilityThrow => 7,
            BrollCandidateType.WeaponReload or
            BrollCandidateType.WeaponSwitch or
            BrollCandidateType.WeaponDraw => 8,
            BrollCandidateType.BombPlant or
            BrollCandidateType.BombDefuse or
            BrollCandidateType.BombApproach => 9,
            BrollCandidateType.EstablishingShot or
            BrollCandidateType.EnvironmentShot => 10,
            BrollCandidateType.VictimReaction => 2,
            BrollCandidateType.PovContinuity => 11,
            _ => 6
        };
    }

    private static bool IsEditoriallyUsable(GapMaterialCandidate value) =>
        value.CinematicScore >= 0.35 &&
        value.ActionDensity <= 0.72 &&
        (value.MovementScore >= 0.10 ||
         value.Type is BrollCandidateType.WeaponReload or
             BrollCandidateType.WeaponSwitch or
             BrollCandidateType.WeaponDraw or
             BrollCandidateType.UtilityPreparation or
             BrollCandidateType.UtilityThrow or
             BrollCandidateType.BombPlant or
             BrollCandidateType.BombDefuse or
             BrollCandidateType.TeamSetup or
             BrollCandidateType.EstablishingShot or
             BrollCandidateType.VictimReaction);

    private static bool IsContextuallyUsable(
        GapMaterialCandidate value,
        GapHighlightContext? previous)
    {
        if (value.Type != BrollCandidateType.VictimReaction)
            return true;
        if (previous is null ||
            value.DemoId != previous.DemoId ||
            value.RoundNumber != previous.RoundNumber)
        {
            return false;
        }
        long tolerance = Math.Max(1, value.TickRate / 3);
        return value.StartTick <= previous.PrimaryKillTick + tolerance &&
               value.EndTick >= previous.PrimaryKillTick - tolerance;
    }

    private static double EditorialScore(
        GapMaterialCandidate value,
        GapHighlightContext? previous,
        GapHighlightContext? next)
    {
        double continuity =
            (previous is not null && value.DemoId == previous.DemoId ? 0.15 : 0) +
            (next is not null && value.DemoId == next.DemoId ? 0.20 : 0);
        return value.CinematicScore * 0.58 +
               value.MovementScore * 0.22 +
               (1 - Math.Clamp(value.ActionDensity, 0, 1)) * 0.20 +
               continuity;
    }

    private static string Rationale(int priority) => priority switch
    {
        1 => "player trajectory continues into the next highlight",
        2 => "player trajectory continues after the previous highlight",
        3 => "player approaches the future fight",
        4 => "player exits or changes position after contact",
        5 => "stable same-team group movement",
        6 => "team setup before contact",
        7 => "utility preparation",
        8 => "weapon reload or switch",
        9 => "bomb objective action",
        10 => "verified narrative environment establishing shot",
        11 => "bounded POV continuity",
        _ => "narrative gameplay continuity"
    };
}
