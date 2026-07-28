using Cs2Highlight.Analysis;
using Cs2Highlight.Web.Domain;
using Cs2Highlight.Web.Services;

namespace Cs2Highlight.Web.Tests;

public sealed class DomainTests
{
    [Fact]
    public void PriceUsesMinorUnitsAndUsd()
    {
        Generation generation = new();
        Assert.Equal(100, generation.PriceAmountMinor);
        Assert.IsType<long>(generation.PriceAmountMinor);
        Assert.Equal("USD", generation.PriceCurrency);
    }

    [Fact]
    public void StateMachineAcceptsKnownFlowAndRejectsMutationAfterPayment()
    {
        Generation generation = new();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        GenerationStateMachine.Transition(generation, GenerationStatus.Uploading, now);
        GenerationStateMachine.Transition(generation, GenerationStatus.Uploaded, now);
        GenerationStateMachine.Transition(generation, GenerationStatus.QueuedForAnalysis, now);
        GenerationStateMachine.Transition(generation, GenerationStatus.Analyzing, now);
        GenerationStateMachine.Transition(
            generation, GenerationStatus.BuildingHighlightCatalog, now);
        GenerationStateMachine.Transition(generation, GenerationStatus.AwaitingPlayerSelection, now);
        GenerationStateMachine.Transition(
            generation, GenerationStatus.AwaitingHighlightSelection, now);
        GenerationStateMachine.Transition(generation, GenerationStatus.AwaitingMusicUpload, now);
        GenerationStateMachine.Transition(generation, GenerationStatus.AnalyzingMusic, now);
        GenerationStateMachine.Transition(generation, GenerationStatus.AwaitingMovieConfiguration, now);
        GenerationStateMachine.Transition(generation, GenerationStatus.ValidatingMoviePlan, now);
        GenerationStateMachine.Transition(generation, GenerationStatus.AwaitingPayment, now);
        GenerationStateMachine.Transition(generation, GenerationStatus.PaymentProcessing, now);
        GenerationStateMachine.Transition(generation, GenerationStatus.Paid, now);
        GenerationStateMachine.Transition(generation, GenerationStatus.QueuedForGeneration, now);
        GenerationStateMachine.Transition(generation, GenerationStatus.PreparingRenderPlan, now);
        GenerationStateMachine.Transition(generation, GenerationStatus.RenderingClips, now);
        GenerationStateMachine.Transition(generation, GenerationStatus.VerifyingClips, now);
        GenerationStateMachine.Transition(generation, GenerationStatus.PlanningMusicEdit, now);
        GenerationStateMachine.Transition(generation, GenerationStatus.ApplyingTimeWarp, now);
        GenerationStateMachine.Transition(generation, GenerationStatus.ApplyingEffects, now);
        GenerationStateMachine.Transition(generation, GenerationStatus.ComposingVideo, now);
        GenerationStateMachine.Transition(generation, GenerationStatus.MixingAudio, now);
        GenerationStateMachine.Transition(generation, GenerationStatus.ApplyingColorGrade, now);
        GenerationStateMachine.Transition(generation, GenerationStatus.VerifyingOutput, now);
        GenerationStateMachine.Transition(generation, GenerationStatus.Completed, now);

        Assert.Throws<InvalidOperationException>(() =>
            GenerationStateMachine.Transition(generation, GenerationStatus.AwaitingPlayerSelection, now));
    }

    [Fact]
    public void PaidFailedGenerationCanBeQueuedForRetry()
    {
        Generation generation = new()
        {
            Status = GenerationStatus.Failed,
            PaymentStatus = PaymentStatus.Succeeded
        };

        GenerationStateMachine.Transition(
            generation, GenerationStatus.QueuedForGeneration, DateTimeOffset.UtcNow);

        Assert.Equal(GenerationStatus.QueuedForGeneration, generation.Status);
    }

    [Fact]
    public void FailedMusicAnalysisCanBeRetriedBeforePayment()
    {
        Generation generation = new()
        {
            Status = GenerationStatus.Failed,
            PaymentStatus = PaymentStatus.NotCreated,
            ErrorCode = "MUSIC_ANALYZER_NOT_FOUND"
        };

        GenerationStateMachine.Transition(
            generation,
            GenerationStatus.AnalyzingMusic,
            DateTimeOffset.UtcNow);

        Assert.Equal(GenerationStatus.AnalyzingMusic, generation.Status);
    }

    [Fact]
    public void PipelinePathResolverFindsExecutableBesideApplication()
    {
        string name = $"resolver-{Guid.NewGuid():N}.exe";
        string path = Path.Combine(AppContext.BaseDirectory, name);
        File.WriteAllText(path, string.Empty);
        try
        {
            Assert.Equal(
                Path.GetFullPath(path),
                PipelinePathResolver.Resolve(name));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void GlobalSelectionUsesStableTopNThenRequestedOutputOrder()
    {
        GlobalHighlightSelector selector = new();
        GlobalHighlightCandidate[] candidates =
        [
            Global(1, 2, "a", 90, 3, 1, 500),
            Global(2, 1, "b", 90, 3, 2, 300),
            Global(1, 1, "c", 80, 2, 0, 100),
            Global(1, 1, "other", 100, 5, 5, 50, "76561198000000002")
        ];

        IReadOnlyList<GlobalHighlightCandidate> result = selector.Select(
            candidates, "76561198000000001", 3, 0, OutputOrder.Chronological);

        Assert.Equal(["c", "b", "a"], result.Select(value => value.Highlight.Id));
        Assert.DoesNotContain(result, value => value.Highlight.Id == "other");
    }

    private static GlobalHighlightCandidate Global(
        long demoId,
        int demoOrder,
        string id,
        double score,
        int kills,
        int headshots,
        long tick,
        string steamId = "76561198000000001") =>
        new(
            demoId,
            $"{demoId}.dem",
            demoOrder,
            new HighlightCandidate(
                id, HighlightType.DoubleKill, steamId, "Player", 1,
                tick, tick + 10, tick - 10, tick + 20, kills, headshots, score,
                new ScoreBreakdown(score, 0, 0, 0, 0, 0, score), [], []));
}
