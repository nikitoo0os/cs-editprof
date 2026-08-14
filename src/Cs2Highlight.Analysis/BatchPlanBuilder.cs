using Cs2Highlight.RenderAgent.Application;
using System.Security.Cryptography;
using System.Text;

namespace Cs2Highlight.Analysis;

public sealed record BatchPlanBuildResult(
    BatchRenderPlan Plan,
    IReadOnlyDictionary<string, RenderJob> RenderJobs);

public sealed class BatchPlanBuilder(IRenderJobBuilder renderJobBuilder, TimeProvider timeProvider)
{
    public BatchPlanBuildResult Build(
        string demoPath,
        string outputDirectory,
        string steamId,
        IReadOnlyList<HighlightCandidate> candidates,
        BatchRenderOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(demoPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        if (steamId.Length != 17 || !steamId.All(char.IsAsciiDigit))
        {
            throw new ArgumentException("steamId must be a 17-digit SteamID64.", nameof(steamId));
        }
        if (options.MaximumClips is <= 0 || options.MaxRetries < 0 ||
            options.OverlapThreshold is < 0 or > 1)
        {
            throw new ArgumentException("Batch limits are invalid.", nameof(options));
        }
        if (options.OverlapPolicy == OverlapResolutionPolicy.Merge)
        {
            throw new NotSupportedException("Overlap policy Merge is not implemented.");
        }

        string demo = Path.GetFullPath(demoPath);
        string root = Path.GetFullPath(outputDirectory);
        HighlightCandidate[] player = candidates
            .Where(candidate => string.Equals(candidate.PlayerId, steamId, StringComparison.Ordinal))
            .ToArray();
        HighlightCandidate[] filtered = player
            .Where(IsValid)
            .Where(candidate => options.Types.Contains(candidate.Type))
            .Where(candidate => candidate.Score >= options.MinimumScore)
            .GroupBy(candidate => candidate.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        IReadOnlyList<HighlightCandidate> resolved = ResolveOverlaps(filtered, options);
        IEnumerable<HighlightCandidate> ordered = Sort(resolved, options);
        if (options.MaximumClips is int maximum)
        {
            ordered = ordered.Take(maximum);
        }
        HighlightCandidate[] selected = ordered.ToArray();
        if (selected.Length == 0)
        {
            throw new InvalidOperationException($"No highlights found for player {steamId}.");
        }

        string demoSlug = RenderJobBuilder.SafeSlug(Path.GetFileNameWithoutExtension(demo));
        string batchId = $"{demoSlug}-player-{steamId}";
        string playerName = selected[0].PlayerName;
        List<BatchRenderItem> items = [];
        Dictionary<string, RenderJob> jobs = new(StringComparer.Ordinal);
        for (int offset = 0; offset < selected.Length; offset++)
        {
            HighlightCandidate highlight = selected[offset];
            int index = offset + 1;
            string type = highlight.Type.ToString().ToLowerInvariant();
            string sourceKey = Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes(highlight.Id)))[..12]
                .ToLowerInvariant();
            string itemId =
                $"r{highlight.RoundNumber:D2}-{type}-{highlight.FirstKillTick}-{highlight.LastKillTick}-{sourceKey}";
            string itemDirectory = Path.Combine(
                root,
                "jobs",
                $"highlight-{index:D3}-r{highlight.RoundNumber:D2}-{type}-{sourceKey}");
            string jobPath = Path.Combine(itemDirectory, "render-job.json");
            RenderJob baseJob = renderJobBuilder.Build(
                demo,
                highlight,
                new RenderJobBuildOptions
                {
                    OutputRoot = itemDirectory,
                    Width = options.Width,
                    Height = options.Height,
                    Fps = options.Fps,
                    Fov = options.Fov,
                    TimeoutSeconds = options.TimeoutSeconds
                });
            RenderJob job = baseJob with
            {
                JobId = $"{demoSlug}-r{highlight.RoundNumber:D2}-{type}-{index:D3}",
                OutputDirectory = itemDirectory
            };
            BatchRenderItem item = new(
                index, itemId, highlight.Id, highlight.Type, steamId, highlight.PlayerName,
                highlight.RoundNumber, highlight.StartTick, highlight.EndTick,
                highlight.KillCount, highlight.Score, jobPath, itemDirectory);
            items.Add(item);
            jobs.Add(itemId, job);
        }
        BatchRenderPlan plan = new(
            "1.0",
            batchId,
            demo,
            new PlayerSelector(steamId, playerName),
            timeProvider.GetUtcNow(),
            options,
            new BatchPlanMetrics(candidates.Count, player.Length, filtered.Length, resolved.Count),
            items);
        return new BatchPlanBuildResult(plan, jobs);
    }

    private static bool IsValid(HighlightCandidate candidate) =>
        candidate.StartTick >= 0 &&
        candidate.EndTick > candidate.StartTick &&
        candidate.RoundNumber > 0 &&
        !string.IsNullOrWhiteSpace(candidate.Id);

    private static IReadOnlyList<HighlightCandidate> ResolveOverlaps(
        IReadOnlyList<HighlightCandidate> candidates,
        BatchRenderOptions options)
    {
        if (options.OverlapPolicy == OverlapResolutionPolicy.KeepAll)
        {
            return candidates;
        }
        List<HighlightCandidate> kept = [];
        foreach (HighlightCandidate candidate in candidates
                     .OrderBy(item => item.StartTick)
                     .ThenBy(item => item.Id, StringComparer.Ordinal))
        {
            int overlapIndex = kept.FindIndex(existing =>
                IsStrongOverlap(existing, candidate, options.OverlapThreshold));
            if (overlapIndex < 0)
            {
                kept.Add(candidate);
                continue;
            }
            if (ComparePreferred(candidate, kept[overlapIndex]) < 0)
            {
                kept[overlapIndex] = candidate;
            }
        }
        return kept;
    }

    private static bool IsStrongOverlap(
        HighlightCandidate left,
        HighlightCandidate right,
        double threshold)
    {
        if (!string.Equals(left.PlayerId, right.PlayerId, StringComparison.Ordinal))
        {
            return false;
        }
        long intersection = Math.Max(
            0,
            Math.Min(left.EndTick, right.EndTick) - Math.Max(left.StartTick, right.StartTick));
        long shorter = Math.Min(left.EndTick - left.StartTick, right.EndTick - right.StartTick);
        return shorter > 0 && (double)intersection / shorter >= threshold;
    }

    private static int ComparePreferred(HighlightCandidate left, HighlightCandidate right)
    {
        int result = right.Score.CompareTo(left.Score);
        if (result != 0) return result;
        result = right.KillCount.CompareTo(left.KillCount);
        if (result != 0) return result;
        result = (left.EndTick - left.StartTick).CompareTo(right.EndTick - right.StartTick);
        if (result != 0) return result;
        result = left.StartTick.CompareTo(right.StartTick);
        return result != 0 ? result : string.CompareOrdinal(left.Id, right.Id);
    }

    private static IEnumerable<HighlightCandidate> Sort(
        IEnumerable<HighlightCandidate> candidates,
        BatchRenderOptions options)
    {
        Func<HighlightCandidate, IComparable> key = options.SortBy switch
        {
            BatchSortBy.Score => item => item.Score,
            BatchSortBy.Round => item => item.RoundNumber,
            _ => item => item.StartTick
        };
        IOrderedEnumerable<HighlightCandidate> ordered = options.SortOrder == BatchSortOrder.Ascending
            ? candidates.OrderBy(key)
            : candidates.OrderByDescending(key);
        return ordered.ThenBy(item => item.StartTick).ThenBy(item => item.Id, StringComparer.Ordinal);
    }
}

public static class BatchPlanReconciler
{
    public static bool IsEquivalent(
        BatchRenderPlan current,
        BatchRenderPlan desired) =>
        current.Items.Count == desired.Items.Count &&
        current.Items.Zip(desired.Items).All(pair =>
            string.Equals(
                pair.First.HighlightId,
                pair.Second.HighlightId,
                StringComparison.Ordinal) &&
            string.Equals(
                pair.First.ItemId,
                pair.Second.ItemId,
                StringComparison.Ordinal));

    public static BatchRenderState ReconcileExpandedPlan(
        BatchRenderPlan currentPlan,
        BatchRenderState currentState,
        BatchRenderPlan desiredPlan,
        DateTimeOffset now)
    {
        Dictionary<string, BatchRenderItemState> completedByHighlight =
            currentPlan.Items.Zip(currentState.Items)
                .Where(pair =>
                    pair.Second.Status == BatchRenderItemStatus.Succeeded &&
                    !string.IsNullOrWhiteSpace(pair.Second.OutputFile) &&
                    File.Exists(pair.Second.OutputFile) &&
                    new FileInfo(pair.Second.OutputFile).Length > 0)
                .GroupBy(pair => pair.First.HighlightId, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.First().Second,
                    StringComparer.Ordinal);
        BatchRenderItemState[] items = desiredPlan.Items.Select(item =>
            completedByHighlight.TryGetValue(
                item.HighlightId,
                out BatchRenderItemState? completed)
                ? completed with { ItemId = item.ItemId }
                : new BatchRenderItemState(
                    item.ItemId,
                    BatchRenderItemStatus.Pending,
                    0)).ToArray();
        return currentState with
        {
            BatchId = desiredPlan.BatchId,
            Status = items.All(value =>
                    value.Status == BatchRenderItemStatus.Succeeded)
                ? BatchRenderStatus.Completed
                : BatchRenderStatus.Ready,
            Items = items,
            CurrentItemIndex = null,
            UpdatedAt = now
        };
    }
}
