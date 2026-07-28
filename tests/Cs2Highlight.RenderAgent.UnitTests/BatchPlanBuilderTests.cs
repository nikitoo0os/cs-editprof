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
