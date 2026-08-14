using Cs2Highlight.Music;
using Cs2Highlight.Web.Domain;
using Cs2Highlight.Web.Services;

namespace Cs2Highlight.Web.Tests;

public sealed class MeaningfulGapPolicyTests
{
    [Theory]
    [InlineData(3.938, 6.64, true)]
    [InlineData(3.0, 6.0, true)]
    [InlineData(3.0, 6.01, false)]
    public void FixedDurationFreeCameraExtensionHasSafeSpeedFloor(
        double sourceDuration,
        double outputDuration,
        bool expected)
    {
        Assert.Equal(
            expected,
            MeaningfulGapPolicy.CanExtendFreeCamera(
                sourceDuration,
                outputDuration));
    }

    [Theory]
    [InlineData(4.577, true)]
    [InlineData(5.0, true)]
    [InlineData(5.001, false)]
    public void ExplicitDurationCanMoveByAtMostFiveSeconds(
        double missingDuration,
        bool expected)
    {
        Assert.Equal(
            expected,
            MeaningfulGapPolicy.CanShortenExplicitDuration(
                missingDuration));
    }

    [Fact]
    public void PlayerTrajectoryIntoNextHighlightOutranksGenericMaterial()
    {
        GapHighlightContext next = new(
            "next",
            1,
            3,
            1_000,
            1_100,
            1_200,
            64);
        GapMaterialCandidate approach = Candidate(
            "approach",
            BrollCandidateType.PlayerApproach,
            700,
            950);
        GapMaterialCandidate establishing = Candidate(
            "establishing",
            BrollCandidateType.EstablishingShot,
            100,
            500) with
        {
            CinematicScore = 0.99
        };

        GapMaterialDecision decision = MeaningfulGapPolicy.Select(
            [establishing, approach],
            null,
            next,
            TimelineGapRole.BetweenHighlights,
            2,
            new HashSet<string>(StringComparer.Ordinal));

        Assert.Same(approach, decision.Candidate);
        Assert.Equal(1, decision.NarrativePriority);
        Assert.Equal(LocalRegionOutcome.Natural, decision.Outcome);
    }

    [Fact]
    public void UsedSourceIntervalIsNeverSelectedAgain()
    {
        GapMaterialCandidate used = Candidate(
            "used",
            BrollCandidateType.TeamMovement,
            100,
            300);
        GapMaterialCandidate unique = Candidate(
            "unique",
            BrollCandidateType.TeamSetup,
            400,
            650);

        GapMaterialDecision decision = MeaningfulGapPolicy.Select(
            [used, unique],
            null,
            null,
            TimelineGapRole.Intro,
            2,
            new HashSet<string>([used.SourceInterval], StringComparer.Ordinal));

        Assert.Same(unique, decision.Candidate);
        Assert.NotEqual(used.SourceInterval, decision.Candidate!.SourceInterval);
    }

    [Fact]
    public void VictimReactionIsUsableWithoutMovementAfterDeath()
    {
        GapMaterialCandidate reaction = Candidate(
            "victim-reaction",
            BrollCandidateType.VictimReaction,
            1_000,
            1_112) with
        {
            MovementScore = 0,
            ActionDensity = 0,
            CinematicScore = 0.92
        };

        GapMaterialDecision decision = MeaningfulGapPolicy.Select(
            [reaction],
            new GapHighlightContext(
                "previous",
                1,
                3,
                900,
                1_050,
                1_080,
                64),
            null,
            TimelineGapRole.BetweenHighlights,
            reaction.DurationSeconds,
            new HashSet<string>(StringComparer.Ordinal));

        Assert.Same(reaction, decision.Candidate);
        Assert.Equal(2, decision.NarrativePriority);
    }

    [Fact]
    public void VictimReactionIsNotUsedAfterAnUnrelatedHighlight()
    {
        GapMaterialCandidate reaction = Candidate(
            "victim-reaction",
            BrollCandidateType.VictimReaction,
            1_000,
            1_140) with
        {
            MovementScore = 0,
            ActionDensity = 0,
            CinematicScore = 0.92
        };

        GapMaterialDecision decision = MeaningfulGapPolicy.Select(
            [reaction],
            new GapHighlightContext(
                "unrelated",
                1,
                3,
                2_000,
                2_050,
                2_080,
                64),
            null,
            TimelineGapRole.BetweenHighlights,
            reaction.DurationSeconds,
            new HashSet<string>(StringComparer.Ordinal));

        Assert.Null(decision.Candidate);
    }

    [Fact]
    public void OverlappingSourceIntervalCanProvideADifferentComposition()
    {
        GapMaterialCandidate overlap = Candidate(
            "overlap",
            BrollCandidateType.TeamMovement,
            150,
            350);
        GapMaterialCandidate unique = Candidate(
            "unique",
            BrollCandidateType.TeamSetup,
            400,
            650);

        GapMaterialDecision decision = MeaningfulGapPolicy.Select(
            [overlap, unique],
            null,
            null,
            TimelineGapRole.Intro,
            2,
            new HashSet<string>(["1:100-200"], StringComparer.Ordinal));

        Assert.Same(overlap, decision.Candidate);
    }

    [Fact]
    public void ShortGapUsesRetimingInsteadOfMeaninglessPadding()
    {
        GapMaterialDecision decision = MeaningfulGapPolicy.Select(
            [],
            null,
            null,
            TimelineGapRole.BetweenHighlights,
            0.42,
            new HashSet<string>(StringComparer.Ordinal));

        Assert.Equal(LocalRegionOutcome.Retiming, decision.Outcome);
        Assert.Null(decision.Candidate);
        Assert.False(decision.ShortenExcerpt);
    }

    [Fact]
    public void UnfillableOutroShortensTheExcerptTail()
    {
        GapMaterialDecision decision = MeaningfulGapPolicy.Select(
            [],
            null,
            null,
            TimelineGapRole.Outro,
            3,
            new HashSet<string>(StringComparer.Ordinal));

        Assert.Equal(LocalRegionOutcome.ShortenedExcerpt, decision.Outcome);
        Assert.True(decision.ShortenExcerpt);
        Assert.Contains(
            "EXCERPT_SHORTENED_INSTEAD_OF_PADDING",
            decision.Warnings);
    }

    private static GapMaterialCandidate Candidate(
        string id,
        BrollCandidateType type,
        long startTick,
        long endTick) => new(
            id,
            1,
            3,
            type,
            startTick,
            endTick,
            64,
            0.72,
            0.55,
            0.20);
}
