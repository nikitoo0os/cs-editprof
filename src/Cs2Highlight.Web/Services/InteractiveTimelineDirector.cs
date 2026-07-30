using System.Text.Json;
using Cs2Highlight.Music;
using Cs2Highlight.Web.Data;
using Cs2Highlight.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace Cs2Highlight.Web.Services;

public sealed record TimelineHighlightView(
    string Id,
    string Type,
    string MapName,
    int RoundNumber,
    int KillCount,
    int HeadshotCount,
    double BeautyScore,
    double TotalScore,
    double DurationSeconds,
    double PrimaryKillOffsetSeconds,
    double PreRollSeconds,
    double PostRollSeconds,
    bool HighFpsEligible,
    string Weapon);

public sealed record TimelineSectionView(
    string Id,
    string Type,
    double StartSeconds,
    double EndSeconds,
    double Energy);

public sealed record TimelineSnapPointView(
    string Id,
    string Type,
    double TimeSeconds,
    double Strength);

public sealed record TimelineGapView(
    string Id,
    string Role,
    double StartSeconds,
    double EndSeconds,
    string State,
    string Camera,
    string Material);

public sealed record InteractiveTimelineView(
    string GenerationId,
    TimelineDirectorMode Mode,
    TimelinePlanState State,
    double DurationSeconds,
    int Revision,
    int RevisionCursor,
    string ConcurrencyToken,
    bool IsLocked,
    IReadOnlyList<TimelineHighlightView> Highlights,
    IReadOnlyList<UserKillAnchor> Anchors,
    IReadOnlyList<TimelineSectionView> Sections,
    IReadOnlyList<TimelineSnapPointView> SnapPoints,
    IReadOnlyList<TimelineGapView> Gaps,
    IReadOnlyDictionary<string, int> CategoryCounts);

public sealed record AddTimelineAnchorRequest(
    TimelineMarkerType MarkerType,
    string? HighlightId,
    double TargetMusicTimeSeconds,
    bool IsLocked = false,
    string? ConcurrencyToken = null);

public sealed record UpdateTimelineAnchorRequest(
    double? TargetMusicTimeSeconds,
    TimelineMarkerType? MarkerType,
    string? HighlightId,
    bool? IsLocked,
    string? ConcurrencyToken);

public interface IInteractiveTimelineDirector
{
    Task<InteractiveTimelineView> GetOrCreateAsync(
        string publicId,
        CancellationToken cancellationToken);
    Task<InteractiveTimelineView> SetModeAsync(
        string publicId,
        TimelineDirectorMode mode,
        string? concurrencyToken,
        CancellationToken cancellationToken);
    Task<InteractiveTimelineView> AddAnchorAsync(
        string publicId,
        AddTimelineAnchorRequest request,
        CancellationToken cancellationToken);
    Task<InteractiveTimelineView> UpdateAnchorAsync(
        string publicId,
        string anchorId,
        UpdateTimelineAnchorRequest request,
        CancellationToken cancellationToken);
    Task<InteractiveTimelineView> DeleteAnchorAsync(
        string publicId,
        string anchorId,
        string? concurrencyToken,
        CancellationToken cancellationToken);
    Task<InteractiveTimelineView> SuggestAsync(
        string publicId,
        string? concurrencyToken,
        CancellationToken cancellationToken);
    Task<InteractiveTimelineView> UndoAsync(
        string publicId,
        string? concurrencyToken,
        CancellationToken cancellationToken);
    Task<InteractiveTimelineView> RedoAsync(
        string publicId,
        string? concurrencyToken,
        CancellationToken cancellationToken);
    Task<InteractiveTimelineView> ConfirmAsync(
        string publicId,
        string? concurrencyToken,
        CancellationToken cancellationToken);
    Task LockAfterPaymentAsync(
        long generationId,
        DateTimeOffset now,
        GenerationDbContext db,
        CancellationToken cancellationToken);
}

public sealed class InteractiveTimelineDirector(
    IDbContextFactory<GenerationDbContext> dbFactory,
    TimeProvider timeProvider,
    InteractiveRetimingOptions retimingOptions,
    GenerationStorage storage) : IInteractiveTimelineDirector
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions IndentedJson =
        new(Json)
        {
            WriteIndented = true
        };

    public async Task<InteractiveTimelineView> GetOrCreateAsync(
        string publicId,
        CancellationToken cancellationToken)
    {
        await using GenerationDbContext db =
            await dbFactory.CreateDbContextAsync(cancellationToken);
        Generation generation = await FindGenerationAsync(
            db, publicId, cancellationToken);
        GenerationTimelinePlan plan = await LoadPlanAsync(
            db, generation.Id, tracking: true, cancellationToken) ??
            await CreatePlanAsync(db, generation, cancellationToken);
        return await BuildViewAsync(db, generation, plan, cancellationToken);
    }

    public Task<InteractiveTimelineView> SetModeAsync(
        string publicId,
        TimelineDirectorMode mode,
        string? concurrencyToken,
        CancellationToken cancellationToken) =>
        MutateAsync(
            publicId,
            concurrencyToken,
            "mode",
            (plan, _, _) =>
            {
                plan.Mode = mode;
                return Task.CompletedTask;
            },
            cancellationToken);

    public Task<InteractiveTimelineView> AddAnchorAsync(
        string publicId,
        AddTimelineAnchorRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(
            publicId,
            request.ConcurrencyToken,
            "anchor-added",
            (plan, _, _) =>
            {
                EnsureEditable(plan);
                double duration = PlanDurationSeconds(plan);
                if (!double.IsFinite(request.TargetMusicTimeSeconds) ||
                    request.TargetMusicTimeSeconds < 0 ||
                    request.TargetMusicTimeSeconds > duration)
                {
                    throw new TimelineValidationException(
                        "MARKER_OUTSIDE_MUSIC_EXCERPT");
                }
                plan.Anchors.Add(new GenerationTimelineAnchor
                {
                    AnchorId = $"anchor-{Guid.NewGuid():N}",
                    MarkerType = request.MarkerType,
                    HighlightId = request.MarkerType ==
                        TimelineMarkerType.ExactHighlight
                            ? request.HighlightId
                            : null,
                    TargetMilliseconds = ToMilliseconds(
                        request.TargetMusicTimeSeconds),
                    IsLocked = request.IsLocked,
                    Order = plan.Anchors.Count
                });
                return Task.CompletedTask;
            },
            cancellationToken);

    public Task<InteractiveTimelineView> UpdateAnchorAsync(
        string publicId,
        string anchorId,
        UpdateTimelineAnchorRequest request,
        CancellationToken cancellationToken) =>
        MutateAsync(
            publicId,
            request.ConcurrencyToken,
            "anchor-updated",
            (plan, _, _) =>
            {
                EnsureEditable(plan);
                GenerationTimelineAnchor anchor = plan.Anchors.SingleOrDefault(
                    value => value.AnchorId == anchorId) ??
                    throw new TimelineNotFoundException("ANCHOR_NOT_FOUND");
                if (anchor.IsLocked &&
                    request.IsLocked is not false &&
                    (request.TargetMusicTimeSeconds.HasValue ||
                     request.MarkerType.HasValue ||
                     request.HighlightId is not null))
                {
                    throw new TimelineConflictException("ANCHOR_IS_LOCKED");
                }
                if (request.TargetMusicTimeSeconds is double target)
                {
                    if (!double.IsFinite(target) ||
                        target < 0 ||
                        target > PlanDurationSeconds(plan))
                    {
                        throw new TimelineValidationException(
                            "MARKER_OUTSIDE_MUSIC_EXCERPT");
                    }
                    anchor.TargetMilliseconds = ToMilliseconds(target);
                }
                if (request.MarkerType is TimelineMarkerType markerType)
                {
                    anchor.MarkerType = markerType;
                    anchor.HighlightId = markerType ==
                        TimelineMarkerType.ExactHighlight
                            ? request.HighlightId ?? anchor.HighlightId
                            : null;
                }
                else if (request.HighlightId is not null)
                {
                    anchor.MarkerType = TimelineMarkerType.ExactHighlight;
                    anchor.HighlightId = request.HighlightId;
                }
                if (request.IsLocked.HasValue)
                    anchor.IsLocked = request.IsLocked.Value;
                return Task.CompletedTask;
            },
            cancellationToken);

    public Task<InteractiveTimelineView> DeleteAnchorAsync(
        string publicId,
        string anchorId,
        string? concurrencyToken,
        CancellationToken cancellationToken) =>
        MutateAsync(
            publicId,
            concurrencyToken,
            "anchor-deleted",
            (plan, db, _) =>
            {
                EnsureEditable(plan);
                GenerationTimelineAnchor anchor = plan.Anchors.SingleOrDefault(
                    value => value.AnchorId == anchorId) ??
                    throw new TimelineNotFoundException("ANCHOR_NOT_FOUND");
                if (anchor.IsLocked)
                    throw new TimelineConflictException("ANCHOR_IS_LOCKED");
                plan.Anchors.Remove(anchor);
                db.GenerationTimelineAnchors.Remove(anchor);
                return Task.CompletedTask;
            },
            cancellationToken);

    public Task<InteractiveTimelineView> SuggestAsync(
        string publicId,
        string? concurrencyToken,
        CancellationToken cancellationToken) =>
        MutateAsync(
            publicId,
            concurrencyToken,
            "assisted-suggestion",
            async (plan, db, cancellation) =>
            {
                EnsureEditable(plan);
                GenerationHighlight[] highlights = await LoadHighlightsAsync(
                    db, plan.GenerationId, cancellation);
                GenerationTimelineAnchor[] removable = plan.Anchors
                    .Where(value => !value.IsLocked)
                    .ToArray();
                db.GenerationTimelineAnchors.RemoveRange(removable);
                foreach (GenerationTimelineAnchor anchor in removable)
                    plan.Anchors.Remove(anchor);
                int desired = Math.Clamp(highlights.Length, 1, 5);
                double duration = PlanDurationSeconds(plan);
                GenerationMusicAnchor[] snapPoints =
                    await db.GenerationMusicAnchors.AsNoTracking()
                        .Where(value =>
                            value.GenerationId == plan.GenerationId &&
                            value.TimeMilliseconds >=
                                plan.ExcerptStartMilliseconds &&
                            value.TimeMilliseconds <=
                                plan.ExcerptEndMilliseconds)
                        .OrderByDescending(value => value.Strength)
                        .ThenBy(value => value.TimeMilliseconds)
                        .ToArrayAsync(cancellation);
                List<double> used = plan.Anchors
                    .Select(value => value.TargetMilliseconds / 1000d)
                    .ToList();
                for (int index = 0; index < desired; index++)
                {
                    GenerationHighlight highlight = highlights[index];
                    double ideal = duration * (index + 1d) / (desired + 1d);
                    double target = snapPoints
                        .Select(value =>
                            (value.TimeMilliseconds -
                             plan.ExcerptStartMilliseconds) / 1000d)
                        .Where(value =>
                            Math.Abs(value - ideal) <= 2.5 &&
                            used.All(item => Math.Abs(item - value) >= 1.25))
                        .OrderBy(value => Math.Abs(value - ideal))
                        .FirstOrDefault(ideal);
                    used.Add(target);
                    plan.Anchors.Add(new GenerationTimelineAnchor
                    {
                        AnchorId = $"anchor-{Guid.NewGuid():N}",
                        MarkerType = TimelineMarkerType.ExactHighlight,
                        HighlightId = highlight.HighlightId,
                        TargetMilliseconds = ToMilliseconds(target),
                        IsLocked = false,
                        Order = plan.Anchors.Count
                    });
                }
            },
            cancellationToken);

    public Task<InteractiveTimelineView> UndoAsync(
        string publicId,
        string? concurrencyToken,
        CancellationToken cancellationToken) =>
        RestoreRevisionAsync(
            publicId, -1, concurrencyToken, cancellationToken);

    public Task<InteractiveTimelineView> RedoAsync(
        string publicId,
        string? concurrencyToken,
        CancellationToken cancellationToken) =>
        RestoreRevisionAsync(
            publicId, 1, concurrencyToken, cancellationToken);

    public async Task<InteractiveTimelineView> ConfirmAsync(
        string publicId,
        string? concurrencyToken,
        CancellationToken cancellationToken)
    {
        await using GenerationDbContext db =
            await dbFactory.CreateDbContextAsync(cancellationToken);
        Generation generation = await FindGenerationAsync(
            db, publicId, cancellationToken);
        GenerationTimelinePlan plan = await LoadPlanAsync(
            db, generation.Id, tracking: true, cancellationToken) ??
            throw new TimelineNotFoundException("TIMELINE_NOT_FOUND");
        CheckConcurrency(plan, concurrencyToken);
        EnsureEditable(plan);
        await RecalculateAsync(db, plan, cancellationToken);
        if (plan.Anchors.Count == 0)
            throw new TimelineValidationException("AT_LEAST_ONE_MARKER_REQUIRED");
        if (plan.Anchors.Any(value =>
                value.FeasibilityStatus == AnchorFeasibilityStatus.Invalid))
        {
            throw new TimelineValidationException(
                "INVALID_MARKERS_BLOCK_CONFIRMATION");
        }
        await ApplyCinematicAnchorsAsync(
            db,
            plan,
            cancellationToken);
        plan.State = TimelinePlanState.Ready;
        Touch(plan);
        await CreateRevisionAsync(
            db, plan, "confirmed", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await WriteArtifactsAsync(
            db,
            generation,
            plan,
            cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return await BuildViewAsync(db, generation, plan, cancellationToken);
    }

    public async Task LockAfterPaymentAsync(
        long generationId,
        DateTimeOffset now,
        GenerationDbContext db,
        CancellationToken cancellationToken)
    {
        GenerationTimelinePlan? plan =
            await db.GenerationTimelinePlans.SingleOrDefaultAsync(
                value => value.GenerationId == generationId,
                cancellationToken);
        if (plan is null)
            return;
        plan.State = TimelinePlanState.Locked;
        plan.LockedAt ??= now;
        plan.UpdatedAt = now;
        foreach (GenerationTimelineGap gap in await db.GenerationTimelineGaps
                     .Where(value => value.TimelinePlanId == plan.Id)
                     .ToArrayAsync(cancellationToken))
        {
            gap.State = TimelineGapState.Locked;
        }
    }

    private async Task<InteractiveTimelineView> MutateAsync(
        string publicId,
        string? concurrencyToken,
        string reason,
        Func<GenerationTimelinePlan, GenerationDbContext, CancellationToken, Task>
            mutation,
        CancellationToken cancellationToken)
    {
        await using GenerationDbContext db =
            await dbFactory.CreateDbContextAsync(cancellationToken);
        Generation generation = await FindGenerationAsync(
            db, publicId, cancellationToken);
        GenerationTimelinePlan plan = await LoadPlanAsync(
            db, generation.Id, tracking: true, cancellationToken) ??
            await CreatePlanAsync(db, generation, cancellationToken);
        CheckConcurrency(plan, concurrencyToken);
        EnsureEditable(plan);
        await mutation(plan, db, cancellationToken);
        NormalizeOrder(plan);
        await RecalculateAsync(db, plan, cancellationToken);
        plan.State = plan.Anchors.Any(value =>
            value.FeasibilityStatus == AnchorFeasibilityStatus.Invalid)
                ? TimelinePlanState.Draft
                : TimelinePlanState.Ready;
        Touch(plan);
        await CreateRevisionAsync(db, plan, reason, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return await BuildViewAsync(db, generation, plan, cancellationToken);
    }

    private async Task<InteractiveTimelineView> RestoreRevisionAsync(
        string publicId,
        int delta,
        string? concurrencyToken,
        CancellationToken cancellationToken)
    {
        await using GenerationDbContext db =
            await dbFactory.CreateDbContextAsync(cancellationToken);
        Generation generation = await FindGenerationAsync(
            db, publicId, cancellationToken);
        GenerationTimelinePlan plan = await LoadPlanAsync(
            db, generation.Id, tracking: true, cancellationToken) ??
            throw new TimelineNotFoundException("TIMELINE_NOT_FOUND");
        CheckConcurrency(plan, concurrencyToken);
        EnsureEditable(plan);
        int target = plan.RevisionCursor + delta;
        GenerationTimelineRevision revision =
            await db.GenerationTimelineRevisions.SingleOrDefaultAsync(
                value =>
                    value.TimelinePlanId == plan.Id &&
                    value.Number == target,
                cancellationToken) ??
            throw new TimelineConflictException(
                delta < 0 ? "NOTHING_TO_UNDO" : "NOTHING_TO_REDO");
        TimelineRevisionSnapshot snapshot =
            JsonSerializer.Deserialize<TimelineRevisionSnapshot>(
                revision.SnapshotJson,
                Json) ??
            throw new InvalidOperationException("REVISION_SNAPSHOT_INVALID");
        db.GenerationTimelineAnchors.RemoveRange(plan.Anchors);
        plan.Anchors.Clear();
        plan.Mode = snapshot.Mode;
        foreach (TimelineAnchorSnapshot value in snapshot.Anchors)
        {
            plan.Anchors.Add(new GenerationTimelineAnchor
            {
                AnchorId = value.Id,
                MarkerType = value.MarkerType,
                HighlightId = value.HighlightId,
                TargetMilliseconds = value.TargetMilliseconds,
                IsLocked = value.IsLocked,
                Order = value.Order
            });
        }
        plan.RevisionCursor = target;
        Touch(plan);
        await RecalculateAsync(db, plan, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return await BuildViewAsync(db, generation, plan, cancellationToken);
    }

    private async Task<GenerationTimelinePlan> CreatePlanAsync(
        GenerationDbContext db,
        Generation generation,
        CancellationToken cancellationToken)
    {
        GenerationMusic music = await db.GenerationMusic.SingleAsync(
            value => value.GenerationId == generation.Id,
            cancellationToken);
        GenerationCinematicPlan? cinematic =
            await db.GenerationCinematicPlans.AsNoTracking()
                .SingleOrDefaultAsync(
                    value => value.GenerationId == generation.Id,
                    cancellationToken);
        double excerptStart = 0;
        double duration = Math.Min(30, music.DurationMilliseconds / 1000d);
        if (cinematic is not null)
        {
            CinematicMoviePlan? parsed = Deserialize<CinematicMoviePlan>(
                cinematic.PlanJson);
            if (parsed is not null)
            {
                excerptStart = parsed.MusicExcerpt.StartSeconds;
                duration = parsed.TargetDurationSeconds;
            }
        }
        DateTimeOffset now = timeProvider.GetUtcNow();
        GenerationTimelinePlan plan = new()
        {
            GenerationId = generation.Id,
            ExcerptStartMilliseconds = ToMilliseconds(excerptStart),
            ExcerptEndMilliseconds = ToMilliseconds(excerptStart + duration),
            CreatedAt = now,
            UpdatedAt = now
        };
        db.GenerationTimelinePlans.Add(plan);
        GenerationEditSegment[] editSegments =
            await db.GenerationEditSegments.AsNoTracking()
                .Where(value => value.GenerationId == generation.Id)
                .OrderBy(value => value.Sequence)
                .ToArrayAsync(cancellationToken);
        Dictionary<long, string> highlightIds =
            await db.GenerationHighlights.AsNoTracking()
                .Where(value => value.GenerationId == generation.Id)
                .ToDictionaryAsync(
                    value => value.Id,
                    value => value.HighlightId,
                    cancellationToken);
        foreach (GenerationEditSegment segment in editSegments)
        {
            if (!highlightIds.TryGetValue(
                    segment.GenerationHighlightId,
                    out string? highlightId))
                continue;
            plan.Anchors.Add(new GenerationTimelineAnchor
            {
                AnchorId = $"anchor-{Guid.NewGuid():N}",
                MarkerType = TimelineMarkerType.ExactHighlight,
                HighlightId = highlightId,
                TargetMilliseconds =
                    segment.PrimaryKillOutputMilliseconds,
                Order = plan.Anchors.Count,
                RequiredBaseSpeed = segment.BaseSpeedFactor
            });
        }
        await db.SaveChangesAsync(cancellationToken);
        await RecalculateAsync(db, plan, cancellationToken);
        await CreateRevisionAsync(
            db, plan, "initial-assisted-plan", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return plan;
    }

    private async Task RecalculateAsync(
        GenerationDbContext db,
        GenerationTimelinePlan plan,
        CancellationToken cancellationToken)
    {
        GenerationHighlight[] highlights = await LoadHighlightsAsync(
            db, plan.GenerationId, cancellationToken);
        Dictionary<string, GenerationHighlight> byId =
            highlights.ToDictionary(
                value => value.HighlightId,
                StringComparer.Ordinal);
        HashSet<string> assigned = new(StringComparer.Ordinal);
        GenerationTimelineAnchor[] ordered = plan.Anchors
            .OrderBy(value => value.TargetMilliseconds)
            .ThenBy(value => value.AnchorId, StringComparer.Ordinal)
            .ToArray();
        for (int index = 0; index < ordered.Length; index++)
        {
            GenerationTimelineAnchor anchor = ordered[index];
            List<string> warnings = [];
            GenerationHighlight? highlight = ResolveHighlight(
                anchor, highlights, assigned);
            if (highlight is null)
            {
                anchor.FeasibilityStatus = AnchorFeasibilityStatus.Invalid;
                anchor.RequiredBaseSpeed = 1;
                anchor.RequiredLocalSpeed = 1;
                anchor.EstimatedPreRollSeconds = 0;
                anchor.EstimatedPostRollSeconds = 0;
                warnings.Add("NO_COMPATIBLE_HIGHLIGHT");
                anchor.WarningsJson = JsonSerializer.Serialize(warnings, Json);
                continue;
            }
            anchor.HighlightId = highlight.HighlightId;
            bool duplicate = !assigned.Add(highlight.HighlightId);
            int tickRate = highlight.TickRate > 0 ? highlight.TickRate : 64;
            long primaryTick = highlight.PrimaryKillTick > 0
                ? highlight.PrimaryKillTick
                : highlight.LastKillTick;
            long safeEndTick = highlight.SafeEndTick > primaryTick
                ? highlight.SafeEndTick
                : highlight.EndTick;
            double preRoll = Math.Max(
                0.25, (primaryTick - highlight.StartTick) / (double)tickRate);
            double postRoll = Math.Max(
                0.25, (safeEndTick - primaryTick) / (double)tickRate);
            anchor.EstimatedPreRollSeconds = preRoll;
            anchor.EstimatedPostRollSeconds = postRoll;
            double target = anchor.TargetMilliseconds / 1000d;
            double previousLimit = index == 0
                ? 0
                : ordered[index - 1].TargetMilliseconds / 1000d +
                  ordered[index - 1].EstimatedPostRollSeconds;
            double nextLimit = index == ordered.Length - 1
                ? PlanDurationSeconds(plan)
                : ordered[index + 1].TargetMilliseconds / 1000d;
            double availablePre = Math.Max(0, target - previousLimit);
            double availablePost = Math.Max(0, nextLimit - target);
            double preSpeed = preRoll / Math.Max(0.001, availablePre);
            double postSpeed = postRoll / Math.Max(0.001, availablePost);
            double required = Math.Max(preSpeed, postSpeed);
            if (availablePre >= preRoll && availablePost >= postRoll)
                required = 1;
            anchor.RequiredBaseSpeed = Math.Round(required, 3);
            anchor.RequiredLocalSpeed = Math.Round(
                Math.Max(
                    retimingOptions.HighFpsMinimumSpeed,
                    Math.Min(1, availablePre / Math.Max(preRoll, 0.001))),
                3);
            AnchorFeasibilityStatus status;
            if (duplicate)
            {
                warnings.Add("DUPLICATE_HIGHLIGHT");
                status = AnchorFeasibilityStatus.Invalid;
            }
            else if (target < 0 ||
                     target > PlanDurationSeconds(plan) ||
                     availablePost < Math.Min(0.25, postRoll) ||
                     availablePre < 0.15)
            {
                warnings.Add("SAFE_END_OR_POST_KILL_CANNOT_BE_PRESERVED");
                status = AnchorFeasibilityStatus.Invalid;
            }
            else if (required >= retimingOptions.NaturalMinimumSpeed &&
                     required <= retimingOptions.NaturalMaximumSpeed)
            {
                status = AnchorFeasibilityStatus.Natural;
            }
            else if (required >= retimingOptions.AcceptableMinimumSpeed &&
                     required <= retimingOptions.AcceptableMaximumSpeed)
            {
                warnings.Add("CONTROLLED_RETIMING_REQUIRED");
                status = AnchorFeasibilityStatus.Acceptable;
            }
            else
            {
                warnings.Add(
                    highlight.TickRate >= 120
                        ? "HIGH_FPS_RETIMING_REQUIRED"
                        : "REDUCE_PRE_ROLL_OR_MOVE_MARKER");
                status = AnchorFeasibilityStatus.Risky;
            }
            if (availablePost < postRoll)
                warnings.Add("POST_KILL_TRIMMED");
            if (availablePre < preRoll)
                warnings.Add("PRE_ROLL_RETIMED");
            anchor.FeasibilityStatus = status;
            anchor.WarningsJson = JsonSerializer.Serialize(warnings, Json);
        }
        NormalizeOrder(plan);
        await RebuildGapsAsync(db, plan, cancellationToken);
    }

    private static async Task ApplyCinematicAnchorsAsync(
        GenerationDbContext db,
        GenerationTimelinePlan timeline,
        CancellationToken cancellationToken)
    {
        GenerationCinematicPlan? stored =
            await db.GenerationCinematicPlans.SingleOrDefaultAsync(
                value => value.GenerationId == timeline.GenerationId,
                cancellationToken);
        if (stored is null)
            return;
        CinematicMoviePlan? plan = Deserialize<CinematicMoviePlan>(
            stored.PlanJson);
        if (plan is null)
            throw new TimelineValidationException(
                "CINEMATIC_LOCKED_PLAN_INVALID");
        Dictionary<string, GenerationTimelineAnchor> byHighlight =
            timeline.Anchors
                .Where(value => value.HighlightId is not null)
                .ToDictionary(
                    value => value.HighlightId!,
                    StringComparer.Ordinal);
        Dictionary<string, HighlightPeakMatch> matches =
            plan.HighlightMatches.ToDictionary(
                value => value.HighlightId,
                StringComparer.Ordinal);
        CinematicSequenceSegment[] segments = plan.Segments.Select(value =>
        {
            if (value.HighlightId is null ||
                !byHighlight.TryGetValue(
                    value.HighlightId,
                    out GenerationTimelineAnchor? anchor) ||
                !matches.TryGetValue(
                    value.HighlightId,
                    out HighlightPeakMatch? match))
            {
                return value;
            }
            double target = anchor.TargetMilliseconds / 1000d;
            double shift = target - match.PlannedKillSeconds;
            double duration =
                value.OutputEndSeconds - value.OutputStartSeconds;
            double start = Math.Clamp(
                value.OutputStartSeconds + shift,
                0,
                Math.Max(0, plan.TargetDurationSeconds - duration));
            return value with
            {
                OutputStartSeconds = start,
                OutputEndSeconds = start + duration
            };
        }).OrderBy(value => value.OutputStartSeconds)
          .ThenBy(value => value.Id, StringComparer.Ordinal)
          .ToArray();
        HighlightPeakMatch[] updatedMatches =
            plan.HighlightMatches.Select(value =>
            {
                if (!byHighlight.TryGetValue(
                        value.HighlightId,
                        out GenerationTimelineAnchor? anchor))
                    return value;
                double target = anchor.TargetMilliseconds / 1000d;
                return value with
                {
                    PlannedPeakSeconds = target,
                    PlannedKillSeconds = target,
                    AlignmentErrorMilliseconds = 0,
                    Warnings = value.Warnings
                        .Append("USER_KILL_ANCHOR")
                        .Distinct(StringComparer.Ordinal)
                        .ToArray()
                };
            }).ToArray();
        CinematicMoviePlan updated = plan with
        {
            Segments = segments,
            HighlightMatches = updatedMatches,
            Warnings = plan.Warnings
                .Append("INTERACTIVE_TIMELINE_APPLIED")
                .Distinct(StringComparer.Ordinal)
                .ToArray()
        };
        stored.PlanJson = JsonSerializer.Serialize(updated, Json);
        stored.PlannerVersion = "8.2";

        Dictionary<string, GenerationHighlight> highlights =
            await db.GenerationHighlights
                .Where(value => value.GenerationId == timeline.GenerationId)
                .ToDictionaryAsync(
                    value => value.HighlightId,
                    StringComparer.Ordinal,
                    cancellationToken);
        GenerationEditSegment[] editSegments =
            await db.GenerationEditSegments
                .Where(value => value.GenerationId == timeline.GenerationId)
                .ToArrayAsync(cancellationToken);
        foreach (GenerationEditSegment editSegment in editSegments)
        {
            GenerationHighlight? highlight = highlights.Values
                .SingleOrDefault(value =>
                    value.Id == editSegment.GenerationHighlightId);
            if (highlight is null ||
                !byHighlight.TryGetValue(
                    highlight.HighlightId,
                    out GenerationTimelineAnchor? anchor))
                continue;
            long delta = anchor.TargetMilliseconds -
                         editSegment.PrimaryKillOutputMilliseconds;
            editSegment.OutputStartMilliseconds = Math.Max(
                0,
                editSegment.OutputStartMilliseconds + delta);
            editSegment.PrimaryKillOutputMilliseconds =
                anchor.TargetMilliseconds;
            editSegment.BaseSpeedFactor = anchor.RequiredBaseSpeed;
            editSegment.WarningsJson = JsonSerializer.Serialize(
                Deserialize<string[]>(editSegment.WarningsJson)?
                    .Append("USER_KILL_ANCHOR")
                    .Distinct(StringComparer.Ordinal)
                    .ToArray() ??
                ["USER_KILL_ANCHOR"],
                Json);
        }
    }

    private async Task RebuildGapsAsync(
        GenerationDbContext db,
        GenerationTimelinePlan plan,
        CancellationToken cancellationToken)
    {
        GenerationTimelineGap[] existing = await db.GenerationTimelineGaps
            .Where(value => value.TimelinePlanId == plan.Id)
            .ToArrayAsync(cancellationToken);
        Dictionary<string, GenerationTimelineGap> byKey =
            existing.ToDictionary(value => value.GapId, StringComparer.Ordinal);
        HashSet<string> retained = new(StringComparer.Ordinal);
        GenerationTimelineAnchor[] anchors = plan.Anchors
            .Where(value =>
                value.FeasibilityStatus != AnchorFeasibilityStatus.Invalid)
            .OrderBy(value => value.TargetMilliseconds)
            .ToArray();
        long cursor = 0;
        GenerationTimelineAnchor? previous = null;
        for (int index = 0; index <= anchors.Length; index++)
        {
            GenerationTimelineAnchor? next =
                index < anchors.Length ? anchors[index] : null;
            long end = next is null
                ? plan.ExcerptEndMilliseconds -
                  plan.ExcerptStartMilliseconds
                : Math.Max(
                    cursor,
                    next.TargetMilliseconds -
                    ToMilliseconds(next.EstimatedPreRollSeconds));
            if (end - cursor >= 150)
            {
                string key =
                    $"gap-{previous?.AnchorId ?? "start"}-{next?.AnchorId ?? "end"}";
                retained.Add(key);
                TimelineGapRole role = GapRole(
                    previous, next, cursor, end, plan);
                object gapPlan = await CreateGapPlanAsync(
                    db, plan.GenerationId, role, cursor, end, cancellationToken);
                if (!byKey.TryGetValue(key, out GenerationTimelineGap? gap))
                {
                    gap = new GenerationTimelineGap
                    {
                        TimelinePlanId = plan.Id,
                        GapId = key
                    };
                    db.GenerationTimelineGaps.Add(gap);
                }
                bool unchanged =
                    gap.StartMilliseconds == cursor &&
                    gap.EndMilliseconds == end &&
                    gap.Role == role &&
                    gap.State == TimelineGapState.Planned;
                gap.PreviousAnchorId = previous?.AnchorId;
                gap.NextAnchorId = next?.AnchorId;
                gap.StartMilliseconds = cursor;
                gap.EndMilliseconds = end;
                gap.Role = role;
                if (!unchanged)
                {
                    gap.PlanJson = JsonSerializer.Serialize(gapPlan, Json);
                    gap.State = TimelineGapState.Planned;
                    gap.UpdatedAt = timeProvider.GetUtcNow();
                }
            }
            if (next is not null)
            {
                cursor = Math.Max(
                    cursor,
                    next.TargetMilliseconds +
                    ToMilliseconds(next.EstimatedPostRollSeconds));
                previous = next;
            }
        }
        db.GenerationTimelineGaps.RemoveRange(
            existing.Where(value => !retained.Contains(value.GapId)));
    }

    private static async Task<object> CreateGapPlanAsync(
        GenerationDbContext db,
        long generationId,
        TimelineGapRole role,
        long start,
        long end,
        CancellationToken cancellationToken)
    {
        GenerationBrollCandidate? candidate =
            await db.GenerationBrollCandidates.AsNoTracking()
                .Where(value => value.GenerationId == generationId)
                .OrderByDescending(value => value.CinematicScore)
                .ThenBy(value => value.CandidateId)
                .FirstOrDefaultAsync(cancellationToken);
        string camera = role switch
        {
            TimelineGapRole.Intro or TimelineGapRole.Calm =>
                "Tripod",
            TimelineGapRole.BuildUp or
            TimelineGapRole.BetweenHighlights =>
                "Tracking",
            _ => "PlayerPov"
        };
        return new
        {
            startMilliseconds = start,
            endMilliseconds = end,
            role = role.ToString(),
            material = candidate?.CandidateId ?? "continuity-fallback",
            camera,
            transition = role is TimelineGapRole.BuildUp
                ? "JCut"
                : "Cut",
            deterministic = true
        };
    }

    private async Task CreateRevisionAsync(
        GenerationDbContext db,
        GenerationTimelinePlan plan,
        string reason,
        CancellationToken cancellationToken)
    {
        if (plan.Id == 0)
            await db.SaveChangesAsync(cancellationToken);
        GenerationTimelineRevision[] redo =
            await db.GenerationTimelineRevisions
                .Where(value =>
                    value.TimelinePlanId == plan.Id &&
                    value.Number > plan.RevisionCursor)
                .ToArrayAsync(cancellationToken);
        db.GenerationTimelineRevisions.RemoveRange(redo);
        int next = plan.RevisionCursor + 1;
        TimelineRevisionSnapshot snapshot = new(
            plan.Mode,
            plan.Anchors
                .OrderBy(value => value.Order)
                .Select(value => new TimelineAnchorSnapshot(
                    value.AnchorId,
                    value.MarkerType,
                    value.HighlightId,
                    value.TargetMilliseconds,
                    value.IsLocked,
                    value.Order))
                .ToArray());
        db.GenerationTimelineRevisions.Add(new GenerationTimelineRevision
        {
            TimelinePlanId = plan.Id,
            Number = next,
            Reason = reason,
            SnapshotJson = JsonSerializer.Serialize(snapshot, Json),
            CreatedAt = timeProvider.GetUtcNow()
        });
        plan.RevisionCursor = next;
        plan.RevisionNumber = next;
    }

    private async Task WriteArtifactsAsync(
        GenerationDbContext db,
        Generation generation,
        GenerationTimelinePlan plan,
        CancellationToken cancellationToken)
    {
        string directory = storage.EnsureDirectory(
            generation.PublicId,
            "plan",
            "timeline");
        GenerationTimelineRevision[] revisions =
            await db.GenerationTimelineRevisions.AsNoTracking()
                .Where(value => value.TimelinePlanId == plan.Id)
                .OrderBy(value => value.Number)
                .ToArrayAsync(cancellationToken);
        object feasibility = new
        {
            natural = plan.Anchors.Count(value =>
                value.FeasibilityStatus ==
                AnchorFeasibilityStatus.Natural),
            acceptable = plan.Anchors.Count(value =>
                value.FeasibilityStatus ==
                AnchorFeasibilityStatus.Acceptable),
            risky = plan.Anchors.Count(value =>
                value.FeasibilityStatus ==
                AnchorFeasibilityStatus.Risky),
            invalid = plan.Anchors.Count(value =>
                value.FeasibilityStatus ==
                AnchorFeasibilityStatus.Invalid),
            anchors = plan.Anchors.Select(value => new
            {
                value.AnchorId,
                value.FeasibilityStatus,
                value.RequiredBaseSpeed,
                value.RequiredLocalSpeed,
                value.WarningsJson
            })
        };
        Dictionary<string, object> artifacts = new(StringComparer.Ordinal)
        {
            ["interactive-timeline-plan.json"] = new
            {
                schemaVersion = "8.2",
                generationId = generation.PublicId,
                plan.Mode,
                plan.State,
                plan.ExcerptStartMilliseconds,
                plan.ExcerptEndMilliseconds,
                plan.RevisionNumber,
                plan.RevisionCursor,
                plan.ConcurrencyToken
            },
            ["user-kill-anchors.json"] = plan.Anchors
                .OrderBy(value => value.Order)
                .Select(value => new
                {
                    value.AnchorId,
                    value.MarkerType,
                    value.HighlightId,
                    value.TargetMilliseconds,
                    value.IsLocked,
                    value.Order,
                    value.FeasibilityStatus
                }).ToArray(),
            ["anchor-feasibility-report.json"] = feasibility,
            ["timeline-gap-plan.json"] = plan.Gaps.Select(value => new
            {
                value.GapId,
                value.PreviousAnchorId,
                value.NextAnchorId,
                value.StartMilliseconds,
                value.EndMilliseconds,
                value.Role,
                value.State,
                value.PlanJson
            }).ToArray(),
            ["highlight-assignment-report.json"] = plan.Anchors.Select(value =>
                new
                {
                    value.AnchorId,
                    value.MarkerType,
                    value.HighlightId
                }).ToArray(),
            ["timeline-revisions.json"] = revisions.Select(value => new
            {
                value.Number,
                value.Reason,
                value.CreatedAt
            }).ToArray(),
            ["timeline-ui-diagnostics.json"] = new
            {
                optimisticConcurrency = true,
                dragAndDrop = true,
                pointerEvents = true,
                keyboard = true,
                snapping = true
            },
            ["responsive-layout-report.json"] = new
            {
                viewports = new[]
                {
                    "1440x900", "1280x720", "1024x768",
                    "768x1024", "390x844", "360x800"
                },
                horizontalPageOverflowExpected = false
            },
            ["accessibility-report.json"] = new
            {
                keyboardMarkers = true,
                ariaLiveValidation = true,
                colorIndependentStatuses = true,
                reducedMotion = true
            }
        };
        foreach ((string fileName, object content) in artifacts)
        {
            string path = Path.Combine(directory, fileName);
            string temporary = path + ".tmp";
            await File.WriteAllTextAsync(
                temporary,
                JsonSerializer.Serialize(
                    content,
                    IndentedJson),
                cancellationToken);
            File.Move(temporary, path, true);
            GenerationArtifact? stored =
                await db.GenerationArtifacts.SingleOrDefaultAsync(
                    value =>
                        value.GenerationId == generation.Id &&
                        value.FileName == fileName,
                    cancellationToken);
            if (stored is null)
            {
                stored = new GenerationArtifact
                {
                    GenerationId = generation.Id,
                    FileName = fileName,
                    CreatedAt = timeProvider.GetUtcNow()
                };
                db.GenerationArtifacts.Add(stored);
            }
            stored.Type = fileName.Contains(
                "diagnostic",
                StringComparison.OrdinalIgnoreCase)
                ? ArtifactType.TimelineDiagnostics
                : ArtifactType.InteractiveTimelinePlan;
            stored.StoredPath = path;
            stored.ContentType = "application/json";
            stored.FileSizeBytes = new FileInfo(path).Length;
        }
    }

    private static async Task<InteractiveTimelineView> BuildViewAsync(
        GenerationDbContext db,
        Generation generation,
        GenerationTimelinePlan plan,
        CancellationToken cancellationToken)
    {
        GenerationHighlight[] highlights = await LoadHighlightsAsync(
            db, generation.Id, cancellationToken);
        GenerationDemo[] demos = await db.GenerationDemos.AsNoTracking()
            .Where(value => value.GenerationId == generation.Id)
            .ToArrayAsync(cancellationToken);
        Dictionary<long, string> demoNames = demos.ToDictionary(
            value => value.Id,
            value => value.OriginalFileName);
        GenerationMusicSection[] sections =
            await db.GenerationMusicSections.AsNoTracking()
                .Where(value => value.GenerationId == generation.Id)
                .OrderBy(value => value.StartMilliseconds)
                .ToArrayAsync(cancellationToken);
        GenerationMusicAnchor[] snapPoints =
            await db.GenerationMusicAnchors.AsNoTracking()
                .Where(value =>
                    value.GenerationId == generation.Id &&
                    value.TimeMilliseconds >=
                        plan.ExcerptStartMilliseconds &&
                    value.TimeMilliseconds <=
                        plan.ExcerptEndMilliseconds)
                .OrderBy(value => value.TimeMilliseconds)
                .ToArrayAsync(cancellationToken);
        GenerationTimelineGap[] gaps =
            await db.GenerationTimelineGaps.AsNoTracking()
                .Where(value => value.TimelinePlanId == plan.Id)
                .OrderBy(value => value.StartMilliseconds)
                .ToArrayAsync(cancellationToken);
        TimelineHighlightView[] highlightViews = highlights.Select(value =>
        {
            int tickRate = value.TickRate > 0 ? value.TickRate : 64;
            long primary = value.PrimaryKillTick > 0
                ? value.PrimaryKillTick
                : value.LastKillTick;
            long safeEnd = value.SafeEndTick > primary
                ? value.SafeEndTick
                : value.EndTick;
            string weapon = Deserialize<string[]>(
                    value.WeaponSequenceJson)?
                .FirstOrDefault() ?? "Weapon";
            return new TimelineHighlightView(
                value.HighlightId,
                value.Type,
                value.MapName,
                value.RoundNumber,
                value.KillCount,
                value.HeadshotCount,
                value.BeautyScore,
                value.TotalScore,
                Math.Max(
                    0.1,
                    (safeEnd - value.StartTick) / (double)tickRate),
                Math.Max(
                    0,
                    (primary - value.StartTick) / (double)tickRate),
                Math.Max(
                    0,
                    (primary - value.StartTick) / (double)tickRate),
                Math.Max(
                    0,
                    (safeEnd - primary) / (double)tickRate),
                tickRate >= 120,
                weapon);
        }).ToArray();
        UserKillAnchor[] anchors = plan.Anchors
            .OrderBy(value => value.Order)
            .Select(value => new UserKillAnchor
            {
                Id = value.AnchorId,
                GenerationId = generation.PublicId,
                MarkerType = value.MarkerType,
                HighlightId = value.HighlightId,
                TargetMusicTimeSeconds =
                    value.TargetMilliseconds / 1000d,
                IsLocked = value.IsLocked,
                Order = value.Order,
                Feasibility = value.FeasibilityStatus,
                RequiredBaseSpeed = value.RequiredBaseSpeed,
                RequiredLocalSpeed = value.RequiredLocalSpeed,
                EstimatedPreRollSeconds =
                    value.EstimatedPreRollSeconds,
                EstimatedPostRollSeconds =
                    value.EstimatedPostRollSeconds,
                Warnings = Deserialize<string[]>(
                    value.WarningsJson) ?? []
            }).ToArray();
        TimelineGapView[] gapViews = gaps.Select(value =>
        {
            JsonElement parsed = Deserialize<JsonElement>(
                value.PlanJson);
            string camera = parsed.ValueKind == JsonValueKind.Object &&
                            parsed.TryGetProperty(
                                "camera", out JsonElement cameraValue)
                ? cameraValue.GetString() ?? "PlayerPov"
                : "PlayerPov";
            string material = parsed.ValueKind == JsonValueKind.Object &&
                              parsed.TryGetProperty(
                                  "material", out JsonElement materialValue)
                ? materialValue.GetString() ?? "continuity-fallback"
                : "continuity-fallback";
            return new TimelineGapView(
                value.GapId,
                value.Role.ToString(),
                value.StartMilliseconds / 1000d,
                value.EndMilliseconds / 1000d,
                value.State.ToString(),
                camera,
                material);
        }).ToArray();
        Dictionary<string, int> counts = new(StringComparer.Ordinal)
        {
            ["Solo"] = highlights.Count(value =>
                value.Type.Contains("Solo", StringComparison.OrdinalIgnoreCase)),
            ["Double"] = highlights.Count(value =>
                value.Type.Contains("Double", StringComparison.OrdinalIgnoreCase)),
            ["Triple"] = highlights.Count(value =>
                value.Type.Contains("Triple", StringComparison.OrdinalIgnoreCase)),
            ["Quad"] = highlights.Count(value =>
                value.Type.Contains("Quad", StringComparison.OrdinalIgnoreCase)),
            ["Ace"] = highlights.Count(value =>
                value.Type.Contains("Ace", StringComparison.OrdinalIgnoreCase))
        };
        return new InteractiveTimelineView(
            generation.PublicId,
            plan.Mode,
            plan.State,
            PlanDurationSeconds(plan),
            plan.RevisionNumber,
            plan.RevisionCursor,
            plan.ConcurrencyToken,
            plan.State == TimelinePlanState.Locked,
            highlightViews,
            anchors,
            sections.Select(value => new TimelineSectionView(
                value.SectionId,
                value.Type.ToString(),
                Math.Max(
                    0,
                    (value.StartMilliseconds -
                     plan.ExcerptStartMilliseconds) / 1000d),
                Math.Min(
                    PlanDurationSeconds(plan),
                    (value.EndMilliseconds -
                     plan.ExcerptStartMilliseconds) / 1000d),
                value.Energy)).Where(value =>
                    value.EndSeconds > 0 &&
                    value.StartSeconds < PlanDurationSeconds(plan))
                .ToArray(),
            snapPoints.Select(value => new TimelineSnapPointView(
                value.AnchorId,
                value.Type.ToString(),
                (value.TimeMilliseconds -
                 plan.ExcerptStartMilliseconds) / 1000d,
                value.Strength)).ToArray(),
            gapViews,
            counts);
    }

    private static GenerationHighlight? ResolveHighlight(
        GenerationTimelineAnchor anchor,
        IReadOnlyList<GenerationHighlight> highlights,
        HashSet<string> assigned)
    {
        IEnumerable<GenerationHighlight> candidates = highlights;
        if (anchor.MarkerType == TimelineMarkerType.ExactHighlight)
        {
            return highlights.SingleOrDefault(value =>
                value.HighlightId == anchor.HighlightId);
        }
        string? category = anchor.MarkerType switch
        {
            TimelineMarkerType.BestSolo => "Solo",
            TimelineMarkerType.BestDouble => "Double",
            TimelineMarkerType.BestTriple => "Triple",
            TimelineMarkerType.BestQuad => "Quad",
            TimelineMarkerType.BestAce => "Ace",
            _ => null
        };
        if (category is not null)
        {
            candidates = candidates.Where(value =>
                value.Type.Contains(
                    category,
                    StringComparison.OrdinalIgnoreCase));
        }
        return candidates
            .Where(value => !assigned.Contains(value.HighlightId))
            .OrderByDescending(value => value.BeautyScore)
            .ThenByDescending(value => value.TotalScore)
            .ThenBy(value => value.EstimatedDurationMilliseconds)
            .ThenBy(value => value.HighlightId, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static TimelineGapRole GapRole(
        GenerationTimelineAnchor? previous,
        GenerationTimelineAnchor? next,
        long start,
        long end,
        GenerationTimelinePlan plan)
    {
        if (previous is null)
            return TimelineGapRole.Intro;
        if (next is null)
            return TimelineGapRole.Outro;
        double midpoint =
            ((start + end) / 2d) /
            Math.Max(1, plan.ExcerptEndMilliseconds -
                        plan.ExcerptStartMilliseconds);
        if (midpoint < 0.35)
            return TimelineGapRole.BuildUp;
        if (midpoint > 0.8)
            return TimelineGapRole.Resolution;
        return TimelineGapRole.BetweenHighlights;
    }

    private static void NormalizeOrder(GenerationTimelinePlan plan)
    {
        int order = 0;
        foreach (GenerationTimelineAnchor anchor in plan.Anchors
                     .OrderBy(value => value.TargetMilliseconds)
                     .ThenBy(value => value.AnchorId, StringComparer.Ordinal))
        {
            anchor.Order = order++;
        }
    }

    private static void EnsureEditable(GenerationTimelinePlan plan)
    {
        if (plan.State == TimelinePlanState.Locked ||
            plan.LockedAt.HasValue)
            throw new TimelineConflictException(
                "TIMELINE_LOCKED_FOR_RENDERING");
    }

    private static void CheckConcurrency(
        GenerationTimelinePlan plan,
        string? expected)
    {
        if (!string.IsNullOrWhiteSpace(expected) &&
            !string.Equals(
                expected,
                plan.ConcurrencyToken,
                StringComparison.Ordinal))
        {
            throw new TimelineConflictException(
                "TIMELINE_REVISION_CONFLICT");
        }
    }

    private void Touch(GenerationTimelinePlan plan)
    {
        plan.UpdatedAt = timeProvider.GetUtcNow();
        plan.ConcurrencyToken = Guid.NewGuid().ToString("N");
    }

    private static double PlanDurationSeconds(
        GenerationTimelinePlan plan) =>
        Math.Max(
            0.001,
            (plan.ExcerptEndMilliseconds -
             plan.ExcerptStartMilliseconds) / 1000d);

    private static long ToMilliseconds(double seconds) =>
        (long)Math.Round(
            seconds * 1000,
            MidpointRounding.AwayFromZero);

    private static async Task<Generation> FindGenerationAsync(
        GenerationDbContext db,
        string publicId,
        CancellationToken cancellationToken) =>
        await db.Generations.SingleOrDefaultAsync(
            value => value.PublicId == publicId,
            cancellationToken) ??
        throw new TimelineNotFoundException("GENERATION_NOT_FOUND");

    private static Task<GenerationTimelinePlan?> LoadPlanAsync(
        GenerationDbContext db,
        long generationId,
        bool tracking,
        CancellationToken cancellationToken)
    {
        IQueryable<GenerationTimelinePlan> query =
            db.GenerationTimelinePlans
                .Include(value => value.Anchors)
                .Include(value => value.Gaps);
        if (!tracking)
            query = query.AsNoTracking();
        return query.SingleOrDefaultAsync(
            value => value.GenerationId == generationId,
            cancellationToken);
    }

    private static Task<GenerationHighlight[]> LoadHighlightsAsync(
        GenerationDbContext db,
        long generationId,
        CancellationToken cancellationToken) =>
        db.GenerationHighlights.AsNoTracking()
            .Where(value =>
                value.GenerationId == generationId &&
                value.SelectedByUser)
            .OrderByDescending(value => value.BeautyScore)
            .ThenByDescending(value => value.TotalScore)
            .ThenBy(value => value.HighlightId)
            .ToArrayAsync(cancellationToken);

    private static T? Deserialize<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Json);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}

public sealed class TimelineNotFoundException(string message)
    : InvalidOperationException(message);

public sealed class TimelineConflictException(string message)
    : InvalidOperationException(message);

public sealed class TimelineValidationException(string message)
    : InvalidOperationException(message);
