using System.Text;
using System.Globalization;

namespace Cs2Highlight.Analysis;

public sealed record BatchExecutionResult(BatchRenderState State, BatchRenderReport Report, int ExitCode);

public sealed class BatchRenderOrchestrator(
    IRenderAgentClient renderAgent,
    IBatchStateStore stateStore,
    TimeProvider timeProvider)
{
    public async Task<BatchExecutionResult> RunAsync(
        BatchRenderPlan plan,
        string rootDirectory,
        BatchRenderState? existingState,
        CancellationToken cancellationToken)
    {
        ValidatePlan(plan);
        string root = Path.GetFullPath(rootDirectory);
        string statePath = Path.Combine(root, "batch-state.json");
        string reportPath = Path.Combine(root, "batch-report.json");
        string summaryPath = Path.Combine(root, "batch-summary.txt");
        DateTimeOffset now = timeProvider.GetUtcNow();
        BatchRenderState state = existingState is null
            ? new BatchRenderState(
                "1.0", plan.BatchId, BatchRenderStatus.Ready, null, now, null,
                plan.Items.Select(item =>
                    new BatchRenderItemState(item.ItemId, BatchRenderItemStatus.Pending, 0)).ToArray())
            : await ReconcileAsync(plan, existingState, cancellationToken);
        if (state.Status == BatchRenderStatus.Completed &&
            state.Items.All(item => item.Status == BatchRenderItemStatus.Succeeded))
        {
            BatchRenderReport completed = BuildReport(plan, state);
            await SaveReportAsync(reportPath, summaryPath, plan, completed, cancellationToken);
            return new BatchExecutionResult(state, completed, 0);
        }
        if (state.Status is BatchRenderStatus.CompletedWithErrors or BatchRenderStatus.Failed)
        {
            BatchRenderReport terminal = BuildReport(plan, state);
            await SaveReportAsync(reportPath, summaryPath, plan, terminal, cancellationToken);
            return new BatchExecutionResult(
                state,
                terminal,
                state.Status == BatchRenderStatus.CompletedWithErrors ? 41 : 42);
        }

        DateTimeOffset startedAt = state.StartedAt ?? now;
        state = state with
        {
            Status = BatchStateMachine.Transition(state.Status, BatchRenderStatus.Running),
            StartedAt = startedAt,
            UpdatedAt = now
        };
        await stateStore.SaveAsync(statePath, state, cancellationToken);

        try
        {
            foreach (BatchRenderItem item in plan.Items)
            {
                int stateIndex = FindStateIndex(state, item.ItemId);
                BatchRenderItemState itemState = state.Items[stateIndex];
                if (itemState.Status == BatchRenderItemStatus.Succeeded ||
                    itemState.Status == BatchRenderItemStatus.Skipped ||
                    (itemState.Status == BatchRenderItemStatus.Failed &&
                     (itemState.Error?.Retryable != true ||
                      itemState.Attempts >= 1 + plan.Options.MaxRetries)))
                {
                    continue;
                }

                RenderInvocationResult? invocation = null;
                while (itemState.Attempts < 1 + plan.Options.MaxRetries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int attempt = itemState.Attempts + 1;
                    itemState = itemState with
                    {
                        Status = BatchRenderItemStatus.Running,
                        Attempts = attempt,
                        StartedAt = itemState.StartedAt ?? timeProvider.GetUtcNow(),
                        CompletedAt = null,
                        Error = null
                    };
                    state = Replace(state, stateIndex, itemState) with
                    {
                        CurrentItemIndex = item.Index,
                        UpdatedAt = timeProvider.GetUtcNow()
                    };
                    await stateStore.SaveAsync(statePath, state, cancellationToken);
                    invocation = await renderAgent.RenderAsync(item.RenderJobPath, attempt, cancellationToken);
                    if (invocation.Error is null && invocation.Result?.Success == true)
                    {
                        itemState = itemState with
                        {
                            Status = BatchRenderItemStatus.Succeeded,
                            CompletedAt = timeProvider.GetUtcNow(),
                            RenderResultPath = invocation.RenderResultPath,
                            OutputFile = invocation.Result.OutputFile,
                            OutputSizeBytes = invocation.Result.OutputSizeBytes,
                            DurationMilliseconds = invocation.Result.DurationMilliseconds
                        };
                        break;
                    }
                    BatchItemError error = invocation.Error ??
                        new BatchItemError("RENDER_FAILED", "Render failed without an error.", false);
                    itemState = itemState with
                    {
                        Status = BatchRenderItemStatus.Failed,
                        CompletedAt = timeProvider.GetUtcNow(),
                        RenderResultPath = invocation.RenderResultPath,
                        DurationMilliseconds = invocation.Result?.DurationMilliseconds,
                        Error = error
                    };
                    if (!error.Retryable || attempt >= 1 + plan.Options.MaxRetries)
                    {
                        break;
                    }
                }

                state = Replace(state, stateIndex, itemState) with
                {
                    CurrentItemIndex = null,
                    UpdatedAt = timeProvider.GetUtcNow()
                };
                await stateStore.SaveAsync(statePath, state, cancellationToken);
                if (itemState.Status == BatchRenderItemStatus.Failed && !plan.Options.ContinueOnError)
                {
                    state = MarkRemainingSkipped(state) with
                    {
                        Status = BatchStateMachine.Transition(
                            state.Status,
                            BatchRenderStatus.Failed),
                        UpdatedAt = timeProvider.GetUtcNow()
                    };
                    await stateStore.SaveAsync(statePath, state, cancellationToken);
                    BatchRenderReport failed = BuildReport(plan, state);
                    await SaveReportAsync(reportPath, summaryPath, plan, failed, cancellationToken);
                    return new BatchExecutionResult(state, failed, 42);
                }
            }
        }
        catch (OperationCanceledException)
        {
            state = state with
            {
                Status = BatchStateMachine.Transition(
                    state.Status,
                    BatchRenderStatus.Cancelling),
                UpdatedAt = timeProvider.GetUtcNow()
            };
            await stateStore.SaveAsync(statePath, state, CancellationToken.None);
            state = CancelCurrent(state) with
            {
                Status = BatchStateMachine.Transition(
                    state.Status,
                    BatchRenderStatus.Cancelled),
                CurrentItemIndex = null,
                UpdatedAt = timeProvider.GetUtcNow()
            };
            await stateStore.SaveAsync(statePath, state, CancellationToken.None);
            BatchRenderReport cancelled = BuildReport(plan, state);
            await SaveReportAsync(reportPath, summaryPath, plan, cancelled, CancellationToken.None);
            return new BatchExecutionResult(state, cancelled, 70);
        }

        bool hasErrors = state.Items.Any(item => item.Status == BatchRenderItemStatus.Failed);
        state = state with
        {
            Status = BatchStateMachine.Transition(
                state.Status,
                hasErrors ? BatchRenderStatus.CompletedWithErrors : BatchRenderStatus.Completed),
            CurrentItemIndex = null,
            UpdatedAt = timeProvider.GetUtcNow()
        };
        await stateStore.SaveAsync(statePath, state, cancellationToken);
        BatchRenderReport report = BuildReport(plan, state);
        await SaveReportAsync(reportPath, summaryPath, plan, report, cancellationToken);
        return new BatchExecutionResult(state, report, hasErrors ? 41 : 0);
    }

    public async Task<BatchRenderState> ReconcileAsync(
        BatchRenderPlan plan,
        BatchRenderState state,
        CancellationToken cancellationToken)
    {
        if (state.SchemaVersion != "1.0" || state.BatchId != plan.BatchId ||
            state.Items.Count != plan.Items.Count)
        {
            throw new InvalidDataException("Batch plan/state are incompatible.");
        }
        BatchRenderItemState[] items = state.Items.ToArray();
        for (int index = 0; index < items.Length; index++)
        {
            if (items[index].Status != BatchRenderItemStatus.Running)
            {
                continue;
            }
            BatchRenderItem planItem = plan.Items[index];
            string resultPath = Path.Combine(planItem.OutputDirectory, "render-result.json");
            if (File.Exists(resultPath))
            {
                try
                {
                    var result = await stateStore.LoadAsync<
                        Cs2Highlight.RenderAgent.Application.RenderResult>(
                        resultPath,
                        cancellationToken);
                    if (result.Success && result.JobId is not null &&
                        !string.IsNullOrWhiteSpace(result.OutputFile) &&
                        File.Exists(result.OutputFile) &&
                        new FileInfo(result.OutputFile).Length > 0)
                    {
                        items[index] = items[index] with
                        {
                            Status = BatchRenderItemStatus.Succeeded,
                            CompletedAt = result.CompletedAt,
                            RenderResultPath = resultPath,
                            OutputFile = result.OutputFile,
                            OutputSizeBytes = result.OutputSizeBytes,
                            DurationMilliseconds = result.DurationMilliseconds
                        };
                        continue;
                    }
                }
                catch (Exception exception) when (
                    exception is IOException or InvalidDataException or System.Text.Json.JsonException)
                {
                    // A partial result is treated as an interrupted attempt and retried below.
                }
            }
            items[index] = items[index] with
            {
                Status = BatchRenderItemStatus.Pending,
                Error = new BatchItemError(
                    "INTERRUPTED_ATTEMPT",
                    "A previously running item had no valid completed result.",
                    true)
            };
        }
        if (state.Status == BatchRenderStatus.Cancelled)
        {
            items = items.Select(item =>
                item.Status == BatchRenderItemStatus.Cancelled
                    ? item with { Status = BatchRenderItemStatus.Pending }
                    : item).ToArray();
        }
        return state with
        {
            Status = state.Status == BatchRenderStatus.Cancelled
                ? BatchRenderStatus.Ready
                : state.Status,
            Items = items,
            CurrentItemIndex = null,
            UpdatedAt = timeProvider.GetUtcNow()
        };
    }

    private BatchRenderReport BuildReport(BatchRenderPlan plan, BatchRenderState state)
    {
        DateTimeOffset completedAt = timeProvider.GetUtcNow();
        long[] durations = state.Items
            .Where(item => item.DurationMilliseconds.HasValue)
            .Select(item => item.DurationMilliseconds!.Value)
            .ToArray();
        BatchReportSummary summary = new(
            state.Items.Count,
            state.Items.Count(item => item.Status == BatchRenderItemStatus.Succeeded),
            state.Items.Count(item => item.Status == BatchRenderItemStatus.Failed),
            state.Items.Count(item => item.Status == BatchRenderItemStatus.Skipped),
            state.Items.Count(item => item.Status == BatchRenderItemStatus.Cancelled),
            state.Items.Sum(item => Math.Max(0, item.Attempts - 1)),
            state.Items.Sum(item => item.OutputSizeBytes ?? 0),
            durations.Length == 0 ? null : durations.Average(),
            durations.Length == 0 ? null : durations.Min(),
            durations.Length == 0 ? null : durations.Max());
        long elapsed = state.StartedAt is null
            ? 0
            : Math.Max(0, (long)(completedAt - state.StartedAt.Value).TotalMilliseconds);
        return new BatchRenderReport(
            "1.0",
            plan.BatchId,
            state.Status == BatchRenderStatus.Completed,
            state.Status,
            plan.DemoPath,
            plan.Player,
            state.StartedAt,
            completedAt,
            elapsed,
            plan.Metrics,
            summary,
            plan.Items.Zip(state.Items, (item, itemState) =>
                new BatchRenderReportItem(
                    item.Index,
                    item.ItemId,
                    item.Type,
                    item.RoundNumber,
                    item.StartTick,
                    item.EndTick,
                    item.Score,
                    itemState.Status,
                    itemState.Attempts,
                    itemState.OutputFile,
                    itemState.RenderResultPath,
                    itemState.DurationMilliseconds,
                    itemState.Error)).ToArray(),
            []);
    }

    private async Task SaveReportAsync(
        string reportPath,
        string summaryPath,
        BatchRenderPlan plan,
        BatchRenderReport report,
        CancellationToken cancellationToken)
    {
        await stateStore.SaveAsync(reportPath, report, cancellationToken);
        StringBuilder text = new();
        text.AppendLine(CultureInfo.InvariantCulture, $"Batch: {report.BatchId}");
        text.AppendLine(
            CultureInfo.InvariantCulture,
            $"Player: {plan.Player.Name} ({plan.Player.SteamId})");
        text.AppendLine(CultureInfo.InvariantCulture, $"Total highlights: {report.Summary.Total}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Succeeded: {report.Summary.Succeeded}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Failed: {report.Summary.Failed}");
        text.AppendLine(
            CultureInfo.InvariantCulture,
            $"Duration: {TimeSpan.FromMilliseconds(report.DurationMilliseconds):g}");
        text.AppendLine();
        foreach (BatchRenderReportItem item in report.Items)
        {
            text.AppendLine(
                CultureInfo.InvariantCulture,
                $"[{item.Status.ToString().ToUpperInvariant()}] {item.Index:D2} Round {item.RoundNumber} {item.HighlightType} {item.StartTick}-{item.EndTick} {item.Error?.Code}");
        }
        await File.WriteAllTextAsync(summaryPath, text.ToString(), Encoding.UTF8, cancellationToken);
    }

    private static void ValidatePlan(BatchRenderPlan plan)
    {
        if (plan.SchemaVersion != "1.0" || plan.Items.Count == 0 ||
            plan.Items.Select(item => item.ItemId).Distinct(StringComparer.Ordinal).Count() != plan.Items.Count)
        {
            throw new InvalidDataException("Batch plan is invalid or unsupported.");
        }
    }

    private static int FindStateIndex(BatchRenderState state, string itemId)
    {
        for (int index = 0; index < state.Items.Count; index++)
        {
            if (string.Equals(state.Items[index].ItemId, itemId, StringComparison.Ordinal)) return index;
        }
        throw new InvalidDataException($"State does not contain item {itemId}.");
    }

    private static BatchRenderState Replace(
        BatchRenderState state,
        int index,
        BatchRenderItemState item)
    {
        BatchRenderItemState[] items = state.Items.ToArray();
        items[index] = item;
        return state with { Items = items };
    }

    private static BatchRenderState MarkRemainingSkipped(BatchRenderState state) =>
        state with
        {
            Items = state.Items.Select(item =>
                item.Status == BatchRenderItemStatus.Pending
                    ? item with { Status = BatchRenderItemStatus.Skipped }
                    : item).ToArray()
        };

    private BatchRenderState CancelCurrent(BatchRenderState state) =>
        state with
        {
            Items = state.Items.Select(item =>
                item.Status == BatchRenderItemStatus.Running
                    ? item with
                    {
                        Status = BatchRenderItemStatus.Cancelled,
                        CompletedAt = timeProvider.GetUtcNow(),
                        Error = new BatchItemError("CANCELLED", "Batch was cancelled.", false)
                    }
                    : item).ToArray()
        };
}
