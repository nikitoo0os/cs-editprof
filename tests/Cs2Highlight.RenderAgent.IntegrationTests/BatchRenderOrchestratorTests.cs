using Cs2Highlight.Analysis;
using Cs2Highlight.RenderAgent.Application;

namespace Cs2Highlight.RenderAgent.IntegrationTests;

public sealed class BatchRenderOrchestratorTests : IDisposable
{
    private const string SteamId = "76561198000000001";
    private readonly string root = Path.Combine(Path.GetTempPath(), $"batch-orchestrator-{Guid.NewGuid():N}");

    [Fact]
    public async Task RunsSequentiallyAndRetriesRetryableFailure()
    {
        Directory.CreateDirectory(root);
        BatchRenderPlan plan = Plan(2, continueOnError: true, maxRetries: 1);
        FakeRenderAgent client = new(
        [
            Failure("CS2_START_TIMEOUT", true),
            Success("job-1"),
            Success("job-2")
        ]);
        BatchRenderOrchestrator orchestrator = new(client, new JsonBatchStateStore(), TimeProvider.System);

        BatchExecutionResult result = await orchestrator.RunAsync(
            plan, root, null, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(BatchRenderStatus.Completed, result.State.Status);
        Assert.Equal([1, 1, 2], client.Calls.Select(call => call.ItemIndex));
        Assert.Equal(1, result.Report.Summary.Retries);
        Assert.True(File.Exists(Path.Combine(root, "batch-state.json")));
        Assert.True(File.Exists(Path.Combine(root, "batch-report.json")));
        Assert.True(File.Exists(Path.Combine(root, "batch-summary.txt")));
    }

    [Fact]
    public async Task ContinueOnErrorIsolatesFailedItem()
    {
        Directory.CreateDirectory(root);
        BatchRenderPlan plan = Plan(3, continueOnError: true, maxRetries: 0);
        FakeRenderAgent client = new(
        [
            Success("job-1"),
            Failure("INVALID_RENDER_JOB", false),
            Success("job-3")
        ]);
        BatchRenderOrchestrator orchestrator = new(client, new JsonBatchStateStore(), TimeProvider.System);

        BatchExecutionResult result = await orchestrator.RunAsync(
            plan, root, null, CancellationToken.None);

        Assert.Equal(41, result.ExitCode);
        Assert.Equal(BatchRenderStatus.CompletedWithErrors, result.State.Status);
        Assert.Equal(
            [BatchRenderItemStatus.Succeeded, BatchRenderItemStatus.Failed, BatchRenderItemStatus.Succeeded],
            result.State.Items.Select(item => item.Status));
    }

    [Fact]
    public async Task FailFastSkipsRemainingItems()
    {
        Directory.CreateDirectory(root);
        BatchRenderPlan plan = Plan(3, continueOnError: false, maxRetries: 0);
        FakeRenderAgent client = new([Failure("INVALID_RENDER_JOB", false)]);
        BatchRenderOrchestrator orchestrator = new(client, new JsonBatchStateStore(), TimeProvider.System);

        BatchExecutionResult result = await orchestrator.RunAsync(
            plan, root, null, CancellationToken.None);

        Assert.Equal(42, result.ExitCode);
        Assert.Equal(
            [BatchRenderItemStatus.Failed, BatchRenderItemStatus.Skipped, BatchRenderItemStatus.Skipped],
            result.State.Items.Select(item => item.Status));
    }

    [Fact]
    public async Task ResumeSkipsSucceededAndRequeuesOrphanRunning()
    {
        Directory.CreateDirectory(root);
        BatchRenderPlan plan = Plan(2, continueOnError: true, maxRetries: 1);
        BatchRenderState existing = new(
            "1.0",
            plan.BatchId,
            BatchRenderStatus.Running,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow,
            2,
            [
                new BatchRenderItemState(
                    plan.Items[0].ItemId, BatchRenderItemStatus.Succeeded, 1,
                    OutputFile: "existing.mp4"),
                new BatchRenderItemState(
                    plan.Items[1].ItemId, BatchRenderItemStatus.Running, 0)
            ]);
        FakeRenderAgent client = new([Success("job-2")]);
        BatchRenderOrchestrator orchestrator = new(client, new JsonBatchStateStore(), TimeProvider.System);

        BatchExecutionResult result = await orchestrator.RunAsync(
            plan, root, existing, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Single(client.Calls);
        Assert.Equal(2, client.Calls[0].ItemIndex);
    }

    private BatchRenderPlan Plan(int count, bool continueOnError, int maxRetries)
    {
        BatchRenderItem[] items = Enumerable.Range(1, count)
            .Select(index =>
            {
                string directory = Path.Combine(root, "jobs", $"highlight-{index:D3}");
                return new BatchRenderItem(
                    index,
                    $"item-{index}",
                    $"highlight-{index}",
                    HighlightType.DoubleKill,
                    SteamId,
                    "Player",
                    index,
                    index * 100,
                    index * 100 + 50,
                    2,
                    50,
                    Path.Combine(directory, "render-job.json"),
                    directory);
            })
            .ToArray();
        return new BatchRenderPlan(
            "1.0",
            "batch-test",
            Path.Combine(root, "match.dem"),
            new PlayerSelector(SteamId, "Player"),
            DateTimeOffset.UtcNow,
            new BatchRenderOptions
            {
                ContinueOnError = continueOnError,
                MaxRetries = maxRetries
            },
            new BatchPlanMetrics(count, count, count, count),
            items);
    }

    private static RenderInvocationResult Success(string jobId)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        RenderResult result = new(
            jobId, true, RenderState.Completed, $"{jobId}.mp4", 100, 10,
            now, now, new ProcessIdentifiers(), [], null);
        return new RenderInvocationResult(1, 0, result, $"{jobId}-result.json", null);
    }

    private static RenderInvocationResult Failure(string code, bool retryable) =>
        new(1, 1, null, string.Empty, new BatchItemError(code, code, retryable));

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed class FakeRenderAgent : IRenderAgentClient
    {
        private readonly Queue<RenderInvocationResult> results;

        public FakeRenderAgent(IEnumerable<RenderInvocationResult> results) =>
            this.results = new Queue<RenderInvocationResult>(results);

        public List<(int ItemIndex, int Attempt)> Calls { get; } = [];

        public Task<RenderInvocationResult> RenderAsync(
            string renderJobPath,
            int attempt,
            CancellationToken cancellationToken)
        {
            string? directory = Path.GetFileName(Path.GetDirectoryName(renderJobPath));
            int itemIndex = int.Parse(directory!.Split('-')[1], System.Globalization.CultureInfo.InvariantCulture);
            Calls.Add((itemIndex, attempt));
            RenderInvocationResult next = results.Dequeue();
            if (next.Result is not null)
            {
                next = next with { Result = next.Result with { JobId = $"job-{itemIndex}" } };
            }
            return Task.FromResult(next);
        }
    }
}
