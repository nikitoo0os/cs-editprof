using Cs2Highlight.Analysis;

namespace Cs2Highlight.RenderAgent.UnitTests;

public sealed class BatchPlanBuilderTests
{
    private const string SteamId = "76561198000000001";
    private readonly BatchPlanBuilder builder = new(new RenderJobBuilder(), TimeProvider.System);

    [Fact]
    public void FiltersSortsAndLimitsCandidatesDeterministically()
    {
        HighlightCandidate[] candidates =
        [
            Candidate("wrong", "76561198000000002", 1, 100, 150, 100),
            Candidate("low", SteamId, 2, 200, 250, 10),
            Candidate("best", SteamId, 3, 300, 350, 90),
            Candidate("middle", SteamId, 4, 400, 450, 70)
        ];
        BatchRenderOptions options = new()
        {
            MinimumScore = 50,
            MaximumClips = 2,
            SortBy = BatchSortBy.Score,
            SortOrder = BatchSortOrder.Descending
        };

        BatchPlanBuildResult first = Build(candidates, options);
        BatchPlanBuildResult second = Build(candidates, options);

        Assert.Equal(["best", "middle"], first.Plan.Items.Select(item => item.HighlightId));
        Assert.Equal(
            first.Plan.Items.Select(item => item.ItemId),
            second.Plan.Items.Select(item => item.ItemId));
        Assert.All(first.RenderJobs.Values, job => Assert.Equal(SteamId, job.Player.SteamId));
    }

    [Fact]
    public void StrongOverlapKeepsPreferredCandidate()
    {
        HighlightCandidate lower = Candidate("lower", SteamId, 1, 100, 200, 50, kills: 2);
        HighlightCandidate higher = Candidate("higher", SteamId, 1, 110, 190, 80, kills: 3);

        BatchPlanBuildResult result = Build(
            [lower, higher],
            new BatchRenderOptions { OverlapThreshold = 0.70 });

        Assert.Equal("higher", Assert.Single(result.Plan.Items).HighlightId);
        Assert.Equal(1, result.Plan.Metrics.ResolvedCandidates);
    }

    [Fact]
    public void BoundaryOverlapAndKeepAllAreExplicit()
    {
        HighlightCandidate first = Candidate("a", SteamId, 1, 0, 100, 50);
        HighlightCandidate second = Candidate("b", SteamId, 1, 30, 130, 60);

        Assert.Single(Build(
            [first, second],
            new BatchRenderOptions { OverlapThreshold = 0.70 }).Plan.Items);
        Assert.Equal(2, Build(
            [first, second],
            new BatchRenderOptions { OverlapPolicy = OverlapResolutionPolicy.KeepAll }).Plan.Items.Count);
    }

    [Fact]
    public void KeepAllAssignsUniqueItemIdsToSourcesAtTheSameTick()
    {
        HighlightCandidate pov = Candidate(
            "demo-round-20-player-1-125540-125540",
            SteamId,
            20,
            125476,
            125604,
            80,
            kills: 1);
        HighlightCandidate reaction = Candidate(
            "broll-68-20-125540-VictimReaction-148",
            SteamId,
            20,
            125476,
            125604,
            90,
            kills: 1);

        BatchRenderPlan plan = Build(
            [pov, reaction],
            new BatchRenderOptions
            {
                OverlapPolicy = OverlapResolutionPolicy.KeepAll
            }).Plan;

        Assert.Equal(2, plan.Items.Count);
        Assert.Equal(2, plan.Items.Select(value => value.ItemId)
            .Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ExpandedPlanPreservesRenderedSourcesAndQueuesOnlyMissingOnes()
    {
        string rendered = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(rendered, [1]);
            BatchRenderPlan current = Build(
                [Candidate("reaction", SteamId, 20, 100, 200, 90, kills: 1)],
                new BatchRenderOptions
                {
                    OverlapPolicy = OverlapResolutionPolicy.KeepAll
                }).Plan;
            BatchRenderState state = new(
                "1.0",
                current.BatchId,
                BatchRenderStatus.Completed,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                null,
                [new BatchRenderItemState(
                    current.Items[0].ItemId,
                    BatchRenderItemStatus.Succeeded,
                    1,
                    OutputFile: rendered)]);
            BatchRenderPlan expanded = Build(
                [
                    Candidate("pov", SteamId, 20, 100, 200, 80, kills: 1),
                    Candidate("reaction", SteamId, 20, 100, 200, 90, kills: 1)
                ],
                new BatchRenderOptions
                {
                    OverlapPolicy = OverlapResolutionPolicy.KeepAll
                }).Plan;

            BatchRenderState reconciled =
                BatchPlanReconciler.ReconcileExpandedPlan(
                    current,
                    state,
                    expanded,
                    DateTimeOffset.UtcNow);

            Assert.Equal(BatchRenderStatus.Ready, reconciled.Status);
            Assert.Equal(2, reconciled.Items.Count);
            Assert.Single(reconciled.Items.Where(value =>
                value.Status == BatchRenderItemStatus.Succeeded));
            Assert.Single(reconciled.Items.Where(value =>
                value.Status == BatchRenderItemStatus.Pending));
        }
        finally
        {
            File.Delete(rendered);
        }
    }

    [Fact]
    public void UnsafeNicknameNeverEntersPaths()
    {
        HighlightCandidate candidate = Candidate("safe", SteamId, 8, 100, 200, 50) with
        {
            PlayerName = @"..\CON:C:\bad"
        };

        BatchPlanBuildResult result = Build([candidate], new BatchRenderOptions());
        BatchRenderItem item = Assert.Single(result.Plan.Items);

        Assert.DoesNotContain("CON", item.OutputDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("..", Path.GetRelativePath(Path.GetTempPath(), item.OutputDirectory));
        Assert.Equal(@"..\CON:C:\bad", item.PlayerName);
    }

    [Fact]
    public void NoCandidatesForPlayerIsRejected()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            Build(
                [Candidate("other", "76561198000000002", 1, 100, 200, 50)],
                new BatchRenderOptions()));

        Assert.Contains(SteamId, error.Message);
    }

    [Fact]
    public void BatchStateMachineRejectsInvalidTransition()
    {
        Assert.Equal(
            BatchRenderStatus.Running,
            BatchStateMachine.Transition(BatchRenderStatus.Ready, BatchRenderStatus.Running));
        Assert.Throws<InvalidOperationException>(() =>
            BatchStateMachine.Transition(BatchRenderStatus.Completed, BatchRenderStatus.Running));
    }

    private BatchPlanBuildResult Build(
        IReadOnlyList<HighlightCandidate> candidates,
        BatchRenderOptions options) =>
        builder.Build(
            Path.Combine(Path.GetTempPath(), "match.dem"),
            Path.Combine(Path.GetTempPath(), "batch-plan-test"),
            SteamId,
            candidates,
            options);

    private static HighlightCandidate Candidate(
        string id,
        string player,
        int round,
        long start,
        long end,
        double score,
        int kills = 2) =>
        new(
            id,
            kills >= 3 ? HighlightType.TripleKill : HighlightType.DoubleKill,
            player,
            "Player",
            round,
            start + 10,
            end - 10,
            start,
            end,
            kills,
            0,
            score,
            new ScoreBreakdown(score, 0, 0, 0, 0, 0, score),
            [1, 2],
            []);
}
