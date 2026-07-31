using System.Text.Json.Serialization;
using Cs2Highlight.RenderAgent.Application;

namespace Cs2Highlight.Analysis;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BatchSortBy { Score, Tick, Round }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BatchSortOrder { Ascending, Descending }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OverlapResolutionPolicy { KeepAll, KeepHighestScore, Merge }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BatchRenderStatus
{
    Created, Planning, Ready, Running, Cancelling, Completed,
    CompletedWithErrors, Failed, Cancelled
}
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BatchRenderItemStatus { Pending, Running, Succeeded, Failed, Skipped, Cancelled }

public sealed record BatchRenderOptions
{
    public double MinimumScore { get; init; }
    public IReadOnlyList<HighlightType> Types { get; init; } =
        Enum.GetValues<HighlightType>();
    public int? MaximumClips { get; init; }
    public BatchSortBy SortBy { get; init; } = BatchSortBy.Tick;
    public BatchSortOrder SortOrder { get; init; } = BatchSortOrder.Ascending;
    public bool ContinueOnError { get; init; } = true;
    public int MaxRetries { get; init; } = 1;
    public bool UseSharedCs2Session { get; init; }
    public OverlapResolutionPolicy OverlapPolicy { get; init; } =
        OverlapResolutionPolicy.KeepHighestScore;
    public double OverlapThreshold { get; init; } = 0.70;
    public int Width { get; init; } = 1920;
    public int Height { get; init; } = 1080;
    public int Fps { get; init; } = 60;
    public double Fov { get; init; } = 90;
    public int TimeoutSeconds { get; init; } = 600;
}

public sealed record BatchRenderItem(
    int Index,
    string ItemId,
    string HighlightId,
    HighlightType Type,
    string PlayerSteamId,
    string PlayerName,
    int RoundNumber,
    long StartTick,
    long EndTick,
    int KillCount,
    double Score,
    string RenderJobPath,
    string OutputDirectory);

public sealed record BatchPlanMetrics(
    int InputCandidates,
    int PlayerCandidates,
    int FilteredCandidates,
    int ResolvedCandidates);

public sealed record BatchRenderPlan(
    string SchemaVersion,
    string BatchId,
    string DemoPath,
    PlayerSelector Player,
    DateTimeOffset CreatedAt,
    BatchRenderOptions Options,
    BatchPlanMetrics Metrics,
    IReadOnlyList<BatchRenderItem> Items);

public sealed record BatchItemError(string Code, string Message, bool Retryable);

public sealed record BatchRenderItemState(
    string ItemId,
    BatchRenderItemStatus Status,
    int Attempts,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? CompletedAt = null,
    string? RenderResultPath = null,
    string? OutputFile = null,
    long? OutputSizeBytes = null,
    long? DurationMilliseconds = null,
    BatchItemError? Error = null);

public sealed record BatchRenderState(
    string SchemaVersion,
    string BatchId,
    BatchRenderStatus Status,
    DateTimeOffset? StartedAt,
    DateTimeOffset UpdatedAt,
    int? CurrentItemIndex,
    IReadOnlyList<BatchRenderItemState> Items);

public sealed record BatchReportSummary(
    int Total,
    int Succeeded,
    int Failed,
    int Skipped,
    int Cancelled,
    int Retries,
    long TotalOutputSizeBytes,
    double? AverageRenderMilliseconds,
    long? MinimumRenderMilliseconds,
    long? MaximumRenderMilliseconds);

public sealed record BatchRenderReportItem(
    int Index,
    string ItemId,
    HighlightType HighlightType,
    int RoundNumber,
    long StartTick,
    long EndTick,
    double Score,
    BatchRenderItemStatus Status,
    int Attempts,
    string? OutputFile,
    string? RenderResultPath,
    long? DurationMilliseconds,
    BatchItemError? Error);

public sealed record BatchRenderReport(
    string SchemaVersion,
    string BatchId,
    bool Success,
    BatchRenderStatus Status,
    string DemoPath,
    PlayerSelector Player,
    DateTimeOffset? StartedAt,
    DateTimeOffset CompletedAt,
    long DurationMilliseconds,
    BatchPlanMetrics Metrics,
    BatchReportSummary Summary,
    IReadOnlyList<BatchRenderReportItem> Items,
    IReadOnlyList<string> Warnings);

public sealed record RenderInvocationResult(
    int? ProcessId,
    int ExitCode,
    RenderResult? Result,
    string RenderResultPath,
    BatchItemError? Error);

public interface IRenderAgentClient
{
    Task<RenderInvocationResult> RenderAsync(
        string renderJobPath,
        int attempt,
        CancellationToken cancellationToken);
}

public sealed record RenderBatchItemRequest(string RenderJobPath, int Attempt);

public interface ISessionRenderAgentClient : IRenderAgentClient
{
    Task<IReadOnlyList<RenderInvocationResult>> RenderBatchAsync(
        IReadOnlyList<RenderBatchItemRequest> items,
        CancellationToken cancellationToken);
}

public interface IBatchStateStore
{
    Task SaveAsync<T>(string path, T value, CancellationToken cancellationToken);
    Task<T> LoadAsync<T>(string path, CancellationToken cancellationToken);
}

public static class BatchStateMachine
{
    public static BatchRenderStatus Transition(
        BatchRenderStatus current,
        BatchRenderStatus next)
    {
        bool allowed = (current, next) switch
        {
            (BatchRenderStatus.Created, BatchRenderStatus.Planning) => true,
            (BatchRenderStatus.Planning, BatchRenderStatus.Ready) => true,
            (BatchRenderStatus.Ready, BatchRenderStatus.Running) => true,
            (BatchRenderStatus.Running, BatchRenderStatus.Running) => true,
            (BatchRenderStatus.Running, BatchRenderStatus.Completed) => true,
            (BatchRenderStatus.Running, BatchRenderStatus.CompletedWithErrors) => true,
            (BatchRenderStatus.Running, BatchRenderStatus.Failed) => true,
            (BatchRenderStatus.Running, BatchRenderStatus.Cancelling) => true,
            (BatchRenderStatus.Cancelling, BatchRenderStatus.Cancelled) => true,
            (BatchRenderStatus.Completed, BatchRenderStatus.Completed) => true,
            _ => false
        };
        return allowed
            ? next
            : throw new InvalidOperationException($"Invalid batch state transition: {current} -> {next}.");
    }
}
