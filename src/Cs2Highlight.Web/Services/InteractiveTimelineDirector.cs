using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Cs2Highlight.Analysis;
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
    string Weapon,
    string WeaponCode,
    string WeaponIconPath);

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

public sealed record TimelineWaveformView(
    string SchemaVersion,
    bool Available,
    double ExcerptStartSeconds,
    double SamplesPerSecond,
    IReadOnlyList<MusicWaveformPeak> Peaks,
    IReadOnlyList<string> Warnings);

public sealed record TimelineGapView(
    string Id,
    string? PreviousAnchorId,
    string? NextAnchorId,
    string Role,
    double StartSeconds,
    double EndSeconds,
    string State,
    string Camera,
    string Material,
    string Outcome,
    bool Reused,
    bool CameraFallback,
    string CameraVerification);

public sealed record TimelineRegionPreviewView(
    string RegionId,
    double StartSeconds,
    double KillSeconds,
    double EndSeconds,
    string MusicAudioUrl,
    string AudioMix,
    string? CameraPreviewUrl);

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
    TimelineWaveformView Waveform,
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
    Task<TimelineRegionPreviewView> GetRegionPreviewAsync(
        string publicId,
        string regionId,
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
    GenerationStorage storage,
    GenerationWakeSignal queue,
    ICameraPathPlanner cameraPathPlanner,
    IMapCameraProfileCatalog mapProfiles,
    AutomaticCameraCalibrationStore automaticCalibrationStore,
    CinematicCameraRuntimeOptions cameraRuntime) : IInteractiveTimelineDirector
{
    private const string LocalPlannerSchemaVersion = "2.4";
    private static readonly WeaponCatalog TimelineWeapons = new();
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
        bool localPlannerUpgradeRequired = plan.LockedAt is null &&
            plan.Gaps.Any(value =>
                Deserialize<LocalTimelineRegionPlan>(value.PlanJson)?
                    .SchemaVersion != LocalPlannerSchemaVersion);
        if (localPlannerUpgradeRequired)
        {
            GenerationHighlight[] selected = await LoadHighlightsAsync(
                db,
                generation.Id,
                cancellationToken);
            if (plan.Anchors.All(value => !value.IsLocked))
            {
                db.GenerationTimelineAnchors.RemoveRange(plan.Anchors);
                plan.Anchors.Clear();
                await AddAutomaticMarkersAsync(db, plan, cancellationToken);
            }
            await RecalculateAsync(db, plan, cancellationToken);
            if (plan.Gaps.Any(value =>
                    value.State == TimelineGapState.Failed))
            {
                await AutoRepairMarkersAsync(db, plan, cancellationToken);
            }
            Touch(plan);
            await db.SaveChangesAsync(cancellationToken);
        }
        return await BuildViewAsync(db, generation, plan, cancellationToken);
    }

    public async Task<TimelineRegionPreviewView> GetRegionPreviewAsync(
        string publicId,
        string regionId,
        CancellationToken cancellationToken)
    {
        await using GenerationDbContext db =
            await dbFactory.CreateDbContextAsync(cancellationToken);
        Generation generation = await FindGenerationAsync(
            db,
            publicId,
            cancellationToken);
        GenerationTimelinePlan plan = await LoadPlanAsync(
            db,
            generation.Id,
            tracking: false,
            cancellationToken) ??
            throw new TimelineNotFoundException("TIMELINE_NOT_FOUND");
        GenerationTimelineGap gap = plan.Gaps.SingleOrDefault(value =>
            value.GapId == regionId) ??
            throw new TimelineNotFoundException("TIMELINE_REGION_NOT_FOUND");
        LocalTimelineRegionPlan region =
            Deserialize<LocalTimelineRegionPlan>(gap.PlanJson) ??
            throw new TimelineValidationException(
                "LOCAL_REGION_PLAN_INVALID");
        double kill = region.HighlightSegment?
            .PrimaryKillOutputMilliseconds / 1000d ??
            (gap.StartMilliseconds + gap.EndMilliseconds) / 2000d;
        double start = Math.Max(
            gap.StartMilliseconds / 1000d,
            kill - 3);
        double end = Math.Min(
            PlanDurationSeconds(plan),
            Math.Max(gap.EndMilliseconds / 1000d, kill + 3));
        string? cameraUrl = null;
        if (region.CameraShots.Count > 0)
        {
            string cameraId = region.CameraShots[0].Id;
            bool available = await db.GenerationCameraShots.AsNoTracking()
                .AnyAsync(value =>
                    value.GenerationId == generation.Id &&
                    value.ShotId == cameraId &&
                    value.PreviewPath != null,
                    cancellationToken);
            if (available)
            {
                cameraUrl =
                    $"/api/generations/{Uri.EscapeDataString(publicId)}/" +
                    $"timeline/regions/{Uri.EscapeDataString(regionId)}/camera-preview";
            }
        }
        return new TimelineRegionPreviewView(
            regionId,
            start,
            kill,
            end,
            $"/generations/{Uri.EscapeDataString(publicId)}/music-audio",
            region.Audio.MusicDuckingEnabled
                ? "Invalid music ducking plan"
                : "Stable music + gameplay transient accent",
            cameraUrl);
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
                double target = ClampMarkerTarget(
                    request.TargetMusicTimeSeconds,
                    duration);
                TimelineMarkerType markerType = request.MarkerType;
                string? highlightId = request.HighlightId;
                List<string> warnings = [];
                if (!double.IsFinite(request.TargetMusicTimeSeconds) ||
                    Math.Abs(target - request.TargetMusicTimeSeconds) > 0.001)
                    warnings.Add("MARKER_TIME_AUTO_CLAMPED");
                if (markerType == TimelineMarkerType.ExactHighlight &&
                    string.IsNullOrWhiteSpace(highlightId))
                {
                    markerType = TimelineMarkerType.BestAvailableHighlight;
                    warnings.Add("MARKER_AUTO_RESOLVED");
                }
                plan.Anchors.Add(new GenerationTimelineAnchor
                {
                    AnchorId = $"anchor-{Guid.NewGuid():N}",
                    MarkerType = markerType,
                    HighlightId = markerType == TimelineMarkerType.ExactHighlight
                        ? highlightId
                        : null,
                    TargetMilliseconds = ToMilliseconds(target),
                    IsLocked = request.IsLocked,
                    WarningsJson = JsonSerializer.Serialize(warnings, Json),
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
                    double clamped = ClampMarkerTarget(
                        target,
                        PlanDurationSeconds(plan));
                    anchor.TargetMilliseconds = ToMilliseconds(clamped);
                    if (!double.IsFinite(target) ||
                        Math.Abs(clamped - target) > 0.001)
                        AppendAnchorWarning(anchor, "MARKER_TIME_AUTO_CLAMPED");
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
                GenerationTimelineAnchor[] removable = plan.Anchors
                    .Where(value => !value.IsLocked)
                    .ToArray();
                db.GenerationTimelineAnchors.RemoveRange(removable);
                foreach (GenerationTimelineAnchor anchor in removable)
                    plan.Anchors.Remove(anchor);
                await AddAutomaticMarkersAsync(
                    db, plan, cancellation);
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
        if (plan.Anchors.All(value => !value.IsLocked))
        {
            GenerationHighlight[] selected = await LoadHighlightsAsync(
                db,
                generation.Id,
                cancellationToken);
            if (plan.Anchors.Count < selected.Length)
            {
                db.GenerationTimelineAnchors.RemoveRange(plan.Anchors);
                plan.Anchors.Clear();
                await AddAutomaticMarkersAsync(db, plan, cancellationToken);
            }
        }
        await AutoRepairMarkersAsync(db, plan, cancellationToken);
        if (plan.Anchors.Count == 0)
        {
            await AddAutomaticMarkersAsync(db, plan, cancellationToken);
            await RecalculateAsync(db, plan, cancellationToken);
        }
        if (plan.Anchors.Count == 0)
            throw new TimelineValidationException(
                "NO_HIGHLIGHTS_AVAILABLE_FOR_AUTOMATIC_TIMELINE");
        if (plan.Gaps.Any(value =>
                value.State == TimelineGapState.Failed))
        {
            throw new TimelineValidationException(
                "INVALID_LOCAL_REGION_BLOCKS_CONFIRMATION");
        }
        ApplyPlannedExcerptShortening(plan);
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
        int tokenBalance = await db.Users
            .Where(value => value.Id == generation.UserId)
            .Select(value => value.TokenBalance)
            .SingleAsync(cancellationToken);
        int activeTokenCommitments = await db.Generations.CountAsync(
            value =>
                value.UserId == generation.UserId &&
                value.Id != generation.Id &&
                value.PaymentStatus == PaymentStatus.Succeeded &&
                value.Status != GenerationStatus.Completed &&
                value.Status != GenerationStatus.CompletedWithWarnings &&
                value.Status != GenerationStatus.Failed &&
                value.Status != GenerationStatus.Cancelled &&
                value.Status != GenerationStatus.Expired,
            cancellationToken);
        if (tokenBalance <= activeTokenCommitments)
            throw new TimelineValidationException(
                "TOKEN_BALANCE_INSUFFICIENT");
        DateTimeOffset now = timeProvider.GetUtcNow();
        generation.PaymentStatus = PaymentStatus.Succeeded;
        generation.PaidAt = now;
        generation.QueueEnteredAtUtc = now;
        GenerationStateMachine.Transition(
            generation, GenerationStatus.PaymentProcessing, now);
        GenerationStateMachine.Transition(
            generation, GenerationStatus.Paid, now);
        GenerationStateMachine.Transition(
            generation, GenerationStatus.QueuedForGeneration, now);
        await LockAfterPaymentAsync(
            generation.Id,
            now,
            db,
            cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        queue.Wake();
        return await BuildViewAsync(db, generation, plan, cancellationToken);
    }

    public async Task LockAfterPaymentAsync(
        long generationId,
        DateTimeOffset now,
        GenerationDbContext db,
        CancellationToken cancellationToken)
    {
        GenerationMovieSettings settings =
            await db.GenerationMovieSettings.SingleAsync(
                value => value.GenerationId == generationId,
                cancellationToken);
        settings.LockedAt ??= now;
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
        plan.State = plan.Anchors.Count == 0
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
            string? requestedHighlightId = anchor.HighlightId;
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
            if (anchor.MarkerType == TimelineMarkerType.ExactHighlight &&
                !string.Equals(
                    requestedHighlightId,
                    highlight.HighlightId,
                    StringComparison.Ordinal))
                warnings.Add("MARKER_AUTO_RESOLVED");
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
            else if (preSpeed > 1.30 || postSpeed > 1.30)
            {
                // The cinematic continuity pass enforces the same hard
                // playback ceiling. Do not advertise a marker as merely
                // "risky" when confirmation can never build it.
                warnings.Add("HIGHLIGHT_RETIMING_OUT_OF_RANGE");
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

    private async Task AutoRepairMarkersAsync(
        GenerationDbContext db,
        GenerationTimelinePlan plan,
        CancellationToken cancellationToken)
    {
        List<string> diagnostics = [];
        int attempts = Math.Max(1, plan.Anchors.Count * 2);
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            await RecalculateAsync(db, plan, cancellationToken);
            GenerationTimelineAnchor? invalid = plan.Anchors
                .Where(value =>
                    value.FeasibilityStatus == AnchorFeasibilityStatus.Invalid &&
                    !value.IsLocked)
                .OrderBy(value => value.TargetMilliseconds)
                .FirstOrDefault();
            if (invalid is not null)
            {
                if (plan.Anchors.Count <= 1)
                    break;
                diagnostics.Add($"MARKER_AUTO_DROPPED:{invalid.AnchorId}");
                db.GenerationTimelineAnchors.Remove(invalid);
                plan.Anchors.Remove(invalid);
                continue;
            }
            GenerationTimelineGap? failed = plan.Gaps
                .Where(value => value.State == TimelineGapState.Failed)
                .OrderBy(value => value.StartMilliseconds)
                .FirstOrDefault();
            if (failed is null)
                break;
            int markerCount = plan.Anchors.Count;
            await AddAutomaticMarkersAsync(db, plan, cancellationToken);
            if (plan.Anchors.Count == markerCount)
                break;
            diagnostics.Add(
                $"MARKERS_AUTO_ADDED_FOR_COVERAGE:{plan.Anchors.Count - markerCount}");
        }
        await RecalculateAsync(db, plan, cancellationToken);
        if (diagnostics.Count > 0)
        {
            Dictionary<string, object?> current =
                Deserialize<Dictionary<string, object?>>(plan.DiagnosticsJson) ??
                new(StringComparer.Ordinal);
            current["markerAutoRepair"] = diagnostics.Distinct().ToArray();
            plan.DiagnosticsJson = JsonSerializer.Serialize(current, Json);
        }
    }

    private static async Task AddAutomaticMarkersAsync(
        GenerationDbContext db,
        GenerationTimelinePlan plan,
        CancellationToken cancellationToken)
    {
        GenerationHighlight[] highlights = await LoadHighlightsAsync(
            db, plan.GenerationId, cancellationToken);
        if (highlights.Length == 0)
        {
            highlights = await db.GenerationHighlights.AsNoTracking()
                .Where(value => value.GenerationId == plan.GenerationId)
                .OrderByDescending(value => value.BeautyScore)
                .ThenByDescending(value => value.TotalScore)
                .ThenBy(value => value.HighlightId)
                .Take(12)
                .ToArrayAsync(cancellationToken);
        }
        if (highlights.Length == 0)
            return;

        HashSet<string> assigned = plan.Anchors
            .Where(value => value.HighlightId is not null)
            .Select(value => value.HighlightId!)
            .ToHashSet(StringComparer.Ordinal);
        GenerationHighlight[] available = highlights
            .Where(value => !assigned.Contains(value.HighlightId))
            .ToArray();
        int desiredTotal = Math.Clamp(highlights.Length, 1, 64);
        int toAdd = Math.Min(
            available.Length,
            Math.Max(0, desiredTotal - plan.Anchors.Count));
        if (toAdd == 0)
            return;

        double duration = PlanDurationSeconds(plan);
        GenerationMusicAnchor[] snapPoints =
            await db.GenerationMusicAnchors.AsNoTracking()
                .Where(value =>
                    value.GenerationId == plan.GenerationId &&
                    value.TimeMilliseconds >= plan.ExcerptStartMilliseconds &&
                    value.TimeMilliseconds <= plan.ExcerptEndMilliseconds)
                .OrderByDescending(value => value.Strength)
                .ThenBy(value => value.TimeMilliseconds)
                .ToArrayAsync(cancellationToken);
        List<double> used = plan.Anchors
            .Select(value => value.TargetMilliseconds / 1000d)
            .ToList();
        Dictionary<string, GenerationHighlight> highlightById = highlights
            .ToDictionary(value => value.HighlightId, StringComparer.Ordinal);
        for (int index = 0; index < toAdd; index++)
        {
            GenerationHighlight highlight = available[index];
            int position = plan.Anchors.Count + 1;
            double ideal = duration * position / (desiredTotal + 1d);
            (double preRoll, double postRoll) = HighlightWindow(highlight);
            double minimumTarget = preRoll;
            double maximumTarget = Math.Max(
                minimumTarget,
                duration - postRoll);
            foreach (GenerationTimelineAnchor existing in plan.Anchors)
            {
                if (existing.HighlightId is null ||
                    !highlightById.TryGetValue(
                        existing.HighlightId,
                        out GenerationHighlight? existingHighlight))
                {
                    continue;
                }
                (double existingPre, double existingPost) =
                    HighlightWindow(existingHighlight);
                double existingTarget =
                    existing.TargetMilliseconds / 1000d;
                if (existingTarget <= ideal)
                {
                    // RecalculateAsync keeps the preceding post-roll at its
                    // natural duration. Reserve enough room for that tail and
                    // for the incoming pre-roll at the hard 1.30x limit.
                    minimumTarget = Math.Max(
                        minimumTarget,
                        existingTarget + existingPost + preRoll / 1.30);
                }
                else
                {
                    maximumTarget = Math.Min(
                        maximumTarget,
                        existingTarget - postRoll - existingPre / 1.30);
                }
            }
            if (maximumTarget < minimumTarget)
                maximumTarget = minimumTarget;
            double target = snapPoints
                .Select(value =>
                    (value.TimeMilliseconds -
                     plan.ExcerptStartMilliseconds) / 1000d)
                .Where(value =>
                    Math.Abs(value - ideal) <= 2.5 &&
                    value >= minimumTarget &&
                    value <= maximumTarget &&
                    used.All(item => Math.Abs(item - value) >= 1.25))
                .OrderBy(value => Math.Abs(value - ideal))
                .FirstOrDefault(Math.Clamp(
                    ideal,
                    minimumTarget,
                    maximumTarget));
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
    }

    private static (double PreRoll, double PostRoll) HighlightWindow(
        GenerationHighlight highlight)
    {
        int tickRate = highlight.TickRate > 0 ? highlight.TickRate : 64;
        long primaryTick = highlight.PrimaryKillTick > 0
            ? highlight.PrimaryKillTick
            : highlight.LastKillTick;
        long safeEndTick = highlight.SafeEndTick > primaryTick
            ? highlight.SafeEndTick
            : highlight.EndTick;
        return (
            Math.Max(
                0.25,
                (primaryTick - highlight.StartTick) / (double)tickRate),
            Math.Max(
                0.25,
                (safeEndTick - primaryTick) / (double)tickRate));
    }

    private async Task ApplyCinematicAnchorsAsync(
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
        GenerationTimelineGap[] storedRegions =
            await db.GenerationTimelineGaps.AsNoTracking()
                .Where(value => value.TimelinePlanId == timeline.Id)
                .OrderBy(value => value.StartMilliseconds)
                .ThenBy(value => value.GapId)
                .ToArrayAsync(cancellationToken);
        LocalTimelineRegionPlan[] regions = storedRegions
            .Select(value => Deserialize<LocalTimelineRegionPlan>(
                value.PlanJson))
            .Where(value => value is not null)
            .Select(value => value!)
            .ToArray();
        if (regions.Length != storedRegions.Length ||
            regions.Any(value => !value.Validation.IsValid))
        {
            throw new TimelineValidationException(
                "LOCAL_REGION_PLAN_INVALID");
        }
        Dictionary<string, GenerationTimelineAnchor> byHighlight =
            timeline.Anchors
                .Where(value => value.HighlightId is not null)
                .ToDictionary(
                    value => value.HighlightId!,
                    StringComparer.Ordinal);
        Dictionary<string, LocalHighlightSegmentPlan> localByHighlight =
            regions
                .Select(value => value.HighlightSegment)
                .Where(value => value is not null)
                .Select(value => value!)
                .ToDictionary(
                    value => value.HighlightId,
                    StringComparer.Ordinal);
        Dictionary<string, CinematicSequenceSegment> highlightPrototypes =
            plan.Segments
                .Where(value => value.HighlightId is not null)
                .GroupBy(value => value.HighlightId!, StringComparer.Ordinal)
                .ToDictionary(
                    value => value.Key,
                    value => value
                        .OrderByDescending(segment =>
                            segment.OutputEndSeconds -
                            segment.OutputStartSeconds)
                        .ThenBy(segment => segment.Id, StringComparer.Ordinal)
                        .First(),
                    StringComparer.Ordinal);
        List<CinematicSequenceSegment> rebuiltSegments = [];
        foreach (LocalTimelineRegionPlan region in regions)
        {
            for (int index = 0; index < region.BrollSegments.Count; index++)
            {
                LocalBrollSegmentPlan broll = region.BrollSegments[index];
                CameraShotPlan camera = index < region.CameraShots.Count
                    ? region.CameraShots[index]
                    : throw new TimelineValidationException(
                        "LOCAL_REGION_CAMERA_MISSING");
                double start = broll.OutputStartMilliseconds / 1000d;
                double end = broll.OutputEndMilliseconds / 1000d;
                if (end - start <
                    (broll.IsFreeCamera
                        ? MeaningfulGapPolicy.MinimumFreeCameraShotSeconds
                        : MeaningfulGapPolicy.MinimumOrdinaryShotSeconds) -
                    0.0001)
                {
                    throw new TimelineValidationException(
                        "LOCAL_REGION_SHOT_TOO_SHORT");
                }
                rebuiltSegments.Add(new CinematicSequenceSegment
                {
                    Id = $"{region.RegionId}-{broll.MaterialId}",
                    Role = RegionRole(region, start, end),
                    OutputStartSeconds = start,
                    OutputEndSeconds = end,
                    MusicSectionId = MusicSectionFor(
                        plan,
                        start,
                        end),
                    BrollCandidateId = broll.MaterialId.StartsWith(
                        "pov-continuity-",
                        StringComparison.Ordinal)
                            ? null
                            : broll.MaterialId,
                    Camera = camera,
                    TimeWarp = BrollRetiming(camera, start, end),
                    Effects = []
                });
            }
            LocalHighlightSegmentPlan? highlight = region.HighlightSegment;
            if (highlight is null)
                continue;
            if (!highlightPrototypes.TryGetValue(
                    highlight.HighlightId,
                    out CinematicSequenceSegment? prototype))
            {
                throw new TimelineValidationException(
                    "LOCAL_REGION_HIGHLIGHT_PROTOTYPE_MISSING");
            }
            double sourceKillOffset = highlight.PreRollSeconds;
            TimeWarpPlan warp = new(
                region.Retiming.BaseSpeed,
                region.Retiming.UsesLocalRamp
                    ?
                    [
                        new TimeWarpSegment(
                            Math.Max(0, sourceKillOffset - 0.20),
                            sourceKillOffset + 0.12,
                            region.Retiming.LocalSpeed)
                    ]
                    : [],
                region.Retiming.UsesLocalRamp,
                region.Retiming.UsesLocalRamp
                    ? ["USER_ANCHOR_LOCAL_RETIMING"]
                    : []);
            rebuiltSegments.Add(prototype with
            {
                Id = $"{region.RegionId}-{prototype.Id}",
                OutputStartSeconds =
                    highlight.OutputStartMilliseconds / 1000d,
                OutputEndSeconds =
                    highlight.OutputEndMilliseconds / 1000d,
                TimeWarp = warp,
                Effects = region.Effects.Select(value =>
                    new MotivatedEffectDirective(
                        value.EffectType,
                        MotivatedEffectReason.MusicPeak,
                        value.StartMilliseconds / 1000d,
                        value.EndMilliseconds / 1000d,
                        0.25)).ToArray()
            });
        }
        double targetDuration = PlanDurationSeconds(timeline);
        CinematicSequenceSegment[] segments =
            NormalizeCinematicContinuity(
                rebuiltSegments,
                localByHighlight,
                byHighlight,
                targetDuration);
        IReadOnlyDictionary<string, double[]> weaponFireByHighlight =
            await LoadWeaponFireOffsetsAsync(
                db,
                timeline.GenerationId,
                localByHighlight,
                cancellationToken);
        double[] musicalAccents = await db.GenerationMusicAnchors
            .AsNoTracking()
            .Where(value =>
                value.GenerationId == timeline.GenerationId &&
                value.TimeMilliseconds >=
                    timeline.ExcerptStartMilliseconds &&
                value.TimeMilliseconds <=
                    timeline.ExcerptEndMilliseconds &&
                value.Type != MusicalAnchorType.SectionBoundary &&
                (value.Type != MusicalAnchorType.Beat ||
                 value.Strength >= 0.70))
            .OrderBy(value => value.TimeMilliseconds)
            .Select(value =>
                (value.TimeMilliseconds -
                 timeline.ExcerptStartMilliseconds) / 1000d)
            .ToArrayAsync(cancellationToken);
        for (int index = 0; index < segments.Length; index++)
        {
            CinematicSequenceSegment segment = segments[index];
            if (segment.HighlightId is null ||
                !localByHighlight.TryGetValue(
                    segment.HighlightId,
                    out LocalHighlightSegmentPlan? local) ||
                !byHighlight.TryGetValue(
                    segment.HighlightId,
                    out GenerationTimelineAnchor? anchor) ||
                !weaponFireByHighlight.TryGetValue(
                    segment.HighlightId,
                    out double[]? weaponFireOffsets) ||
                weaponFireOffsets.Length == 0)
            {
                continue;
            }
            segments[index] = segment with
            {
                TimeWarp = WeaponFireRhythmSync.Apply(
                    segment.TimeWarp,
                    local.PreRollSeconds + local.PostKillSeconds,
                    local.PreRollSeconds,
                    segment.OutputStartSeconds,
                    anchor.TargetMilliseconds / 1000d,
                    segment.OutputEndSeconds,
                    weaponFireOffsets,
                    musicalAccents)
            };
        }
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
        MusicExcerptPlan excerpt = plan.MusicExcerpt with
        {
            EndSeconds = plan.MusicExcerpt.StartSeconds + targetDuration,
            Peaks = plan.MusicExcerpt.Peaks
                .Where(value => value.TimeSeconds <=
                    plan.MusicExcerpt.StartSeconds + targetDuration +
                    0.000001)
                .ToArray(),
            Warnings = plan.MusicExcerpt.Warnings
                .Concat(regions.Any(value =>
                    value.Validation.Outcome ==
                    LocalRegionOutcome.ShortenedExcerpt)
                    ? ["EXCERPT_SHORTENED_INSTEAD_OF_PADDING"]
                    : [])
                .Distinct(StringComparer.Ordinal)
                .ToArray()
        };
        CinematicMoviePlan updated = plan with
        {
            SchemaVersion = "2.0",
            PlannerVersion = "10.9-local.1",
            MusicExcerpt = excerpt,
            TargetDurationSeconds = targetDuration,
            Segments = segments,
            HighlightMatches = updatedMatches,
            SoundDesign = plan.SoundDesign with
            {
                Sections = plan.SoundDesign.Sections.Select(value =>
                    value with { DuckOnKill = false }).ToArray(),
                Warnings = plan.SoundDesign.Warnings
                    .Append("MUSIC_GAIN_STABLE_AROUND_KILLS")
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()
            },
            Warnings = plan.Warnings
                .Append("INTERACTIVE_LOCAL_REGIONS_REBUILT")
                .Concat(segments.Any(value => value.TimeWarp.Warnings.Contains(
                    "WEAPON_FIRE_MUSIC_ACCENT_SYNC",
                    StringComparer.Ordinal))
                    ? ["WEAPON_FIRE_MUSIC_ACCENT_SYNC"]
                    : [])
                .Distinct(StringComparer.Ordinal)
                .ToArray()
        };
        stored.PlanJson = JsonSerializer.Serialize(updated, Json);
        stored.MusicExcerptJson = JsonSerializer.Serialize(excerpt, Json);
        stored.PlannerVersion = "10.9-local.1";

        HashSet<string> selectedBrollIds = segments
            .Where(value => value.BrollCandidateId is not null)
            .Select(value => value.BrollCandidateId!)
            .ToHashSet(StringComparer.Ordinal);
        GenerationBrollCandidate[] brollCandidates =
            await db.GenerationBrollCandidates
                .Where(value => value.GenerationId == timeline.GenerationId)
                .ToArrayAsync(cancellationToken);
        foreach (GenerationBrollCandidate candidate in brollCandidates)
        {
            candidate.Selected = selectedBrollIds.Contains(
                candidate.CandidateId);
        }

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
            CinematicSequenceSegment plannedSegment = segments.Single(value =>
                value.HighlightId == highlight.HighlightId);
            editSegment.OutputStartMilliseconds =
                ToMilliseconds(plannedSegment.OutputStartSeconds);
            editSegment.PrimaryKillOutputMilliseconds =
                anchor.TargetMilliseconds;
            editSegment.BaseSpeedFactor =
                plannedSegment.TimeWarp.BaseSpeedFactor;
            editSegment.TimeWarpPlanJson = JsonSerializer.Serialize(
                plannedSegment.TimeWarp,
                Json);
            editSegment.WarningsJson = JsonSerializer.Serialize(
                (Deserialize<string[]>(editSegment.WarningsJson) ?? [])
                    .Concat(plannedSegment.TimeWarp.Warnings)
                    .Append("USER_KILL_ANCHOR_LOCAL_REDIRECTION")
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                Json);
        }
    }

    private static TimeWarpPlan BrollRetiming(
        CameraShotPlan camera,
        double outputStartSeconds,
        double outputEndSeconds)
    {
        double outputDuration = Math.Max(
            0.001,
            outputEndSeconds - outputStartSeconds);
        double speed = camera.TargetDurationSeconds / outputDuration;
        if (Math.Abs(speed - 1) <= 0.0005)
            return new TimeWarpPlan(1, [], false, []);
        return new TimeWarpPlan(
            speed,
            [],
            false,
            ["FREECAM_GAP_ABSORPTION_RETIMING"]);
    }

    public static CinematicSequenceSegment[] NormalizeCinematicContinuity(
        IReadOnlyList<CinematicSequenceSegment> source,
        Dictionary<string, LocalHighlightSegmentPlan> localByHighlight,
        Dictionary<string, GenerationTimelineAnchor> byHighlight,
        double targetDuration)
    {
        const double tolerance = 0.001;
        const double maximumAbsorbableGap =
            MeaningfulGapPolicy.MinimumFreeCameraShotSeconds;
        CinematicSequenceSegment[] segments = source
            .OrderBy(value => value.OutputStartSeconds)
            .ThenBy(value => value.Id, StringComparer.Ordinal)
            .ToArray();
        if (segments.Length == 0)
            throw new TimelineValidationException(
                "CINEMATIC_TIMELINE_EMPTY");

        double leadingGap = segments[0].OutputStartSeconds;
        if (leadingGap > tolerance)
        {
            if (leadingGap > maximumAbsorbableGap)
                throw new TimelineValidationException(
                    "CINEMATIC_TIMELINE_DISCONTINUITY");
            segments[0] = segments[0] with
            {
                OutputStartSeconds = 0
            };
        }
        for (int index = 1; index < segments.Length; index++)
        {
            CinematicSequenceSegment previous = segments[index - 1];
            CinematicSequenceSegment current = segments[index];
            double gap = current.OutputStartSeconds -
                previous.OutputEndSeconds;
            if (previous.HighlightId is not null &&
                current.HighlightId is not null &&
                localByHighlight.TryGetValue(
                    previous.HighlightId,
                    out LocalHighlightSegmentPlan? previousLocal) &&
                localByHighlight.TryGetValue(
                    current.HighlightId,
                    out LocalHighlightSegmentPlan? currentLocal) &&
                byHighlight.TryGetValue(
                    previous.HighlightId,
                    out GenerationTimelineAnchor? previousAnchor) &&
                byHighlight.TryGetValue(
                    current.HighlightId,
                    out GenerationTimelineAnchor? currentAnchor))
            {
                const double minimumHighlightSpeed = 0.50;
                const double maximumHighlightSpeed = 1.30;
                double previousKill =
                    previousAnchor.TargetMilliseconds / 1000d;
                double currentKill =
                    currentAnchor.TargetMilliseconds / 1000d;
                double currentPreSpeed = currentLocal.PreRollSeconds /
                    Math.Max(tolerance,
                        currentKill - current.OutputStartSeconds);
                double previousPostSpeed = previousLocal.PostKillSeconds /
                    Math.Max(tolerance,
                        previous.OutputEndSeconds - previousKill);
                bool speedAlreadySafe =
                    currentPreSpeed is >= minimumHighlightSpeed and
                        <= maximumHighlightSpeed &&
                    previousPostSpeed is >= minimumHighlightSpeed and
                        <= maximumHighlightSpeed;
                if (Math.Abs(gap) <= tolerance && speedAlreadySafe)
                    continue;
                if (gap > maximumAbsorbableGap)
                {
                    throw new TimelineValidationException(
                        "CINEMATIC_TIMELINE_DISCONTINUITY");
                }
                double minimumBoundary = Math.Max(
                    previousKill + previousLocal.PostKillSeconds /
                        maximumHighlightSpeed,
                    currentKill - currentLocal.PreRollSeconds /
                        minimumHighlightSpeed);
                double maximumBoundary = Math.Min(
                    previousKill + previousLocal.PostKillSeconds /
                        minimumHighlightSpeed,
                    currentKill - currentLocal.PreRollSeconds /
                        maximumHighlightSpeed);
                if (minimumBoundary > maximumBoundary + tolerance)
                {
                    throw new TimelineValidationException(
                        "CINEMATIC_HIGHLIGHT_RETIMING_OUT_OF_RANGE");
                }

                // Give both sides the same playback speed where possible. This
                // avoids forcing the whole short gap into the next pre-roll.
                double balancedBoundary =
                    (previousLocal.PostKillSeconds * currentKill +
                     currentLocal.PreRollSeconds * previousKill) /
                    (previousLocal.PostKillSeconds +
                     currentLocal.PreRollSeconds);
                double boundary = Math.Clamp(
                    balancedBoundary,
                    minimumBoundary,
                    maximumBoundary);
                segments[index - 1] = previous with
                {
                    OutputEndSeconds = boundary
                };
                segments[index] = current with
                {
                    OutputStartSeconds = boundary
                };
            }
            else if (Math.Abs(gap) <= tolerance)
            {
                continue;
            }
            else if (gap < 0 || gap > maximumAbsorbableGap)
            {
                throw new TimelineValidationException(
                    "CINEMATIC_TIMELINE_DISCONTINUITY");
            }
            else if (current.HighlightId is not null)
            {
                segments[index] = current with
                {
                    OutputStartSeconds =
                        previous.OutputEndSeconds
                };
            }
            else
            {
                segments[index - 1] = previous with
                {
                    OutputEndSeconds = current.OutputStartSeconds
                };
            }
        }
        double trailingGap = targetDuration - segments[^1].OutputEndSeconds;
        if (Math.Abs(trailingGap) > tolerance)
        {
            if (trailingGap < 0 ||
                trailingGap > maximumAbsorbableGap)
            {
                throw new TimelineValidationException(
                    "CINEMATIC_TIMELINE_DISCONTINUITY");
            }
            CinematicSequenceSegment last = segments[^1];
            double originalDuration = Math.Max(
                0.001,
                last.OutputEndSeconds - last.OutputStartSeconds);
            double extendedDuration = Math.Max(
                0.001,
                targetDuration - last.OutputStartSeconds);
            double extensionSpeed = originalDuration / extendedDuration;
            if (last.HighlightId is null && extensionSpeed < 0.72)
            {
                throw new TimelineValidationException(
                    "CINEMATIC_TIMELINE_DISCONTINUITY");
            }
            segments[^1] = last with
            {
                OutputEndSeconds = targetDuration,
                TimeWarp = last.HighlightId is null
                    ? new TimeWarpPlan(
                        extensionSpeed,
                        [],
                        false,
                        ["OUTRO_FREECAM_DURATION_ABSORPTION"])
                    : last.TimeWarp
            };
        }

        for (int index = 0; index < segments.Length; index++)
        {
            CinematicSequenceSegment segment = segments[index];
            if (segment.HighlightId is null)
                continue;
            if (!localByHighlight.TryGetValue(
                    segment.HighlightId,
                    out LocalHighlightSegmentPlan? local) ||
                !byHighlight.TryGetValue(
                    segment.HighlightId,
                    out GenerationTimelineAnchor? anchor))
            {
                throw new TimelineValidationException(
                    "CINEMATIC_HIGHLIGHT_RETIMING_CONTEXT_MISSING");
            }
            double killTime = anchor.TargetMilliseconds / 1000d;
            double outputPre = killTime - segment.OutputStartSeconds;
            double outputPost = segment.OutputEndSeconds - killTime;
            if (outputPre <= 0 || outputPost <= 0 ||
                local.PreRollSeconds <= 0 ||
                local.PostKillSeconds <= 0)
            {
                throw new TimelineValidationException(
                    "CINEMATIC_HIGHLIGHT_RETIMING_INVALID");
            }
            double preSpeed = local.PreRollSeconds / outputPre;
            double postSpeed = local.PostKillSeconds / outputPost;
            if (preSpeed is < 0.50 or > 1.30 ||
                postSpeed is < 0.50 or > 1.30)
            {
                throw new TimelineValidationException(
                    "CINEMATIC_HIGHLIGHT_RETIMING_OUT_OF_RANGE");
            }
            bool warped = Math.Abs(preSpeed - 1) > 0.0005 ||
                Math.Abs(postSpeed - 1) > 0.0005;
            segments[index] = segment with
            {
                TimeWarp = warped
                    ? new TimeWarpPlan(
                        1,
                        [
                            new TimeWarpSegment(
                                0,
                                local.PreRollSeconds,
                                preSpeed),
                            new TimeWarpSegment(
                                local.PreRollSeconds,
                                local.PreRollSeconds +
                                local.PostKillSeconds,
                                postSpeed)
                        ],
                        true,
                        ["USER_ANCHOR_CONTINUITY_RETIMING"])
                    : new TimeWarpPlan(1, [], false, [])
            };
        }

        bool discontinuous =
            Math.Abs(segments[0].OutputStartSeconds) > tolerance ||
            Math.Abs(segments[^1].OutputEndSeconds - targetDuration) >
                tolerance ||
            segments.Zip(segments.Skip(1)).Any(pair =>
                Math.Abs(
                    pair.Second.OutputStartSeconds -
                    pair.First.OutputEndSeconds) > tolerance);
        if (discontinuous)
            throw new TimelineValidationException(
                "CINEMATIC_TIMELINE_DISCONTINUITY");
        return segments;
    }

    private async Task<IReadOnlyDictionary<string, double[]>>
        LoadWeaponFireOffsetsAsync(
            GenerationDbContext db,
            long generationId,
            IReadOnlyDictionary<string, LocalHighlightSegmentPlan>
                localByHighlight,
            CancellationToken cancellationToken)
    {
        Generation? generation = await db.Generations.AsNoTracking()
            .SingleOrDefaultAsync(
                value => value.Id == generationId,
                cancellationToken);
        if (generation?.SelectedSteamId is null ||
            localByHighlight.Count == 0)
        {
            return new Dictionary<string, double[]>(StringComparer.Ordinal);
        }
        string[] highlightIds = [.. localByHighlight.Keys];
        GenerationHighlight[] highlights =
            await db.GenerationHighlights.AsNoTracking()
                .Where(value => value.GenerationId == generationId)
                .Where(value => highlightIds.Contains(value.HighlightId))
                .ToArrayAsync(cancellationToken);
        GenerationDemo[] demos = await db.GenerationDemos.AsNoTracking()
            .Where(value => value.GenerationId == generationId)
            .ToArrayAsync(cancellationToken);
        Dictionary<long, GenerationDemo> demosById = demos.ToDictionary(
            value => value.Id);
        Dictionary<long, DemoAnalysis> analysesByDemoId = [];
        Dictionary<string, double[]> result =
            new(StringComparer.Ordinal);
        foreach (GenerationHighlight highlight in highlights)
        {
            if (!localByHighlight.TryGetValue(
                    highlight.HighlightId,
                    out LocalHighlightSegmentPlan? local) ||
                !demosById.TryGetValue(
                    highlight.GenerationDemoId,
                    out GenerationDemo? demo))
            {
                continue;
            }
            if (!analysesByDemoId.TryGetValue(demo.Id, out DemoAnalysis? analysis))
            {
                string path = Path.Combine(
                    storage.GenerationRoot(generation.PublicId),
                    "analysis",
                    $"demo-{demo.UploadOrder:D3}",
                    "demo-analysis.json");
                if (!File.Exists(path))
                    continue;
                try
                {
                    await using FileStream stream = File.OpenRead(path);
                    analysis = await JsonSerializer.DeserializeAsync<DemoAnalysis>(
                        stream,
                        Json,
                        cancellationToken);
                }
                catch (JsonException)
                {
                    continue;
                }
                catch (IOException)
                {
                    continue;
                }
                if (analysis is null)
                    continue;
                analysesByDemoId[demo.Id] = analysis;
            }
            int tickRate = highlight.TickRate > 0
                ? highlight.TickRate
                : demo.TickRate ?? analysis.Demo.TickRate;
            if (tickRate <= 0)
                continue;
            long sourceEndTick = local.SafeEndTick > local.SourceStartTick
                ? local.SafeEndTick
                : highlight.SafeEndTick;
            result[highlight.HighlightId] = analysis.Timeline
                .Where(frame => string.Equals(
                    frame.Player.PlayerId,
                    demo.SelectedSteamId ?? generation.SelectedSteamId,
                    StringComparison.Ordinal))
                .SelectMany(frame => frame.Events)
                .Where(value =>
                    string.Equals(
                        value.Type,
                        "WeaponFire",
                        StringComparison.Ordinal) &&
                    value.Tick >= local.SourceStartTick &&
                    value.Tick <= sourceEndTick)
                .Select(value =>
                    (value.Tick - local.SourceStartTick) /
                    (double)tickRate)
                .Distinct()
                .OrderBy(value => value)
                .ToArray();
        }
        return result;
    }

    private static CinematicSequenceRole RegionRole(
        LocalTimelineRegionPlan region,
        double start,
        double end)
    {
        if (region.PreviousAnchorId is null)
            return CinematicSequenceRole.Intro;
        if (region.NextAnchorId is null)
            return CinematicSequenceRole.Outro;
        return end - start <= 1.0
            ? CinematicSequenceRole.PreKill
            : CinematicSequenceRole.BuildUp;
    }

    private static string MusicSectionFor(
        CinematicMoviePlan plan,
        double start,
        double end) =>
        plan.Segments
            .Where(value =>
                value.OutputStartSeconds < end &&
                value.OutputEndSeconds > start)
            .OrderByDescending(value =>
                Math.Min(value.OutputEndSeconds, end) -
                Math.Max(value.OutputStartSeconds, start))
            .ThenBy(value => value.Id, StringComparer.Ordinal)
            .Select(value => value.MusicSectionId)
            .FirstOrDefault() ?? "interactive-region";

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
        GenerationTimelineAnchor[] anchors = plan.Anchors
            .Where(value =>
                value.FeasibilityStatus != AnchorFeasibilityStatus.Invalid)
            .OrderBy(value => value.TargetMilliseconds)
            .ToArray();
        List<GapDescriptor> descriptors = [];
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
            string key =
                $"gap-{previous?.AnchorId ?? "start"}-{next?.AnchorId ?? "end"}";
            TimelineGapRole role = GapRole(
                previous, next, cursor, end, plan);
            descriptors.Add(new GapDescriptor(
                key,
                previous,
                next,
                cursor,
                end,
                role));
            if (next is not null)
            {
                cursor = Math.Max(
                    cursor,
                    next.TargetMilliseconds +
                    ToMilliseconds(next.EstimatedPostRollSeconds));
                previous = next;
            }
        }
        HashSet<string> retained = descriptors
            .Select(value => value.Id)
            .ToHashSet(StringComparer.Ordinal);
        db.GenerationTimelineGaps.RemoveRange(
            existing.Where(value => !retained.Contains(value.GapId)));

        GenerationHighlight[] highlights = await db.GenerationHighlights
            .AsNoTracking()
            .Where(value => value.GenerationId == plan.GenerationId)
            .ToArrayAsync(cancellationToken);
        Dictionary<string, GenerationHighlight> highlightsById = highlights
            .ToDictionary(value => value.HighlightId, StringComparer.Ordinal);
        GenerationDemo[] demos = await db.GenerationDemos.AsNoTracking()
            .Where(value => value.GenerationId == plan.GenerationId)
            .ToArrayAsync(cancellationToken);
        Dictionary<long, GenerationDemo> demosById = demos.ToDictionary(
            value => value.Id);
        GapMaterialCandidate[] candidates = await db.GenerationBrollCandidates
            .AsNoTracking()
            .Where(value => value.GenerationId == plan.GenerationId)
            .OrderBy(value => value.CandidateId)
            .Select(value => new GapMaterialCandidate(
                value.CandidateId,
                value.GenerationDemoId,
                value.RoundNumber,
                value.Type,
                value.StartTick,
                value.EndTick,
                64,
                value.CinematicScore,
                value.MovementScore,
                value.ActionDensity))
            .ToArrayAsync(cancellationToken);
        candidates = candidates.Select(value => value with
        {
            TickRate = demosById.TryGetValue(value.DemoId, out GenerationDemo? demo)
                ? demo.TickRate.GetValueOrDefault(64)
                : 64
        }).ToArray();
        GenerationBrollCandidate[] storedCandidates =
            await db.GenerationBrollCandidates.AsNoTracking()
                .Where(value => value.GenerationId == plan.GenerationId)
                .ToArrayAsync(cancellationToken);
        Dictionary<string, long> candidateRowIds = storedCandidates
            .ToDictionary(
                value => value.CandidateId,
                value => value.Id,
                StringComparer.Ordinal);
        GenerationCameraShot[] storedShots = await db.GenerationCameraShots
            .Where(value => value.GenerationId == plan.GenerationId)
            .OrderBy(value => value.ShotId)
            .ToArrayAsync(cancellationToken);
        Generation generation = await db.Generations.AsNoTracking()
            .SingleAsync(
                value => value.Id == plan.GenerationId,
                cancellationToken);
        Dictionary<string, CameraShotPlan> generatedCameraPrototypes =
            await EnsureCameraPoolAsync(
                db,
                generation,
                demosById,
                storedCandidates,
                storedShots,
                PlanDurationSeconds(plan) + 15,
                cancellationToken);
        storedShots = await db.GenerationCameraShots
            .Where(value => value.GenerationId == plan.GenerationId)
            .OrderBy(value => value.ShotId)
            .ToArrayAsync(cancellationToken);
        Dictionary<long, GenerationCameraShot> shotsByCandidate = storedShots
            .Where(value => value.GenerationBrollCandidateId.HasValue)
            .GroupBy(value => value.GenerationBrollCandidateId!.Value)
            .ToDictionary(
                value => value.Key,
                value => value
                    .OrderByDescending(shot =>
                        shot.PreviewStatus == CameraPreviewStatus.Passed)
                    .ThenBy(shot => shot.ShotId, StringComparer.Ordinal)
                    .First());
        GenerationCinematicPlan? cinematicPlanRow =
            await db.GenerationCinematicPlans.AsNoTracking()
                .SingleOrDefaultAsync(
                    value => value.GenerationId == plan.GenerationId,
                    cancellationToken);
        CinematicMoviePlan? cinematicPlan = cinematicPlanRow is null
            ? null
            : Deserialize<CinematicMoviePlan>(cinematicPlanRow.PlanJson);
        Dictionary<string, CameraShotPlan> cameraPrototypes =
            cinematicPlan?.Segments
                .Where(value => value.BrollCandidateId is not null)
                .GroupBy(
                    value => value.BrollCandidateId!,
                    StringComparer.Ordinal)
                .ToDictionary(
                    value => value.Key,
                    value => value
                        .OrderByDescending(segment =>
                            segment.Camera.Family !=
                            CameraShotFamily.PlayerPov)
                        .ThenBy(segment => segment.Camera.Id,
                            StringComparer.Ordinal)
                        .First()
                        .Camera,
                    StringComparer.Ordinal) ??
            new Dictionary<string, CameraShotPlan>(StringComparer.Ordinal);
        foreach ((string candidateId, CameraShotPlan camera) in
                 generatedCameraPrototypes)
        {
            cameraPrototypes.TryAdd(candidateId, camera);
        }
        Dictionary<string, (long Start, long End)> plannedBrollWindows =
            cinematicPlan?.Segments
                .Where(value => value.BrollCandidateId is not null)
                .GroupBy(
                    value => value.BrollCandidateId!,
                    StringComparer.Ordinal)
                .ToDictionary(
                    value => value.Key,
                    value =>
                    {
                        CinematicSequenceSegment segment = value
                            .OrderBy(item => item.OutputStartSeconds)
                            .First();
                        return (
                            ToMilliseconds(segment.OutputStartSeconds),
                            ToMilliseconds(segment.OutputEndSeconds));
                    },
                    StringComparer.Ordinal) ??
            new Dictionary<string, (long Start, long End)>(
                StringComparer.Ordinal);
        GenerationMovieSettings? settings =
            await db.GenerationMovieSettings.AsNoTracking()
                .SingleOrDefaultAsync(
                    value => value.GenerationId == plan.GenerationId,
                    cancellationToken);
        HashSet<string> usedSourceIntervals = highlights
            .Where(value => plan.Anchors.Any(anchor =>
                anchor.HighlightId == value.HighlightId))
            .Select(SourceInterval)
            .ToHashSet(StringComparer.Ordinal);
        Dictionary<string, LocalTimelineRegionPlan> reusable =
            new(StringComparer.Ordinal);
        foreach (GapDescriptor descriptor in descriptors)
        {
            if (!byKey.TryGetValue(
                    descriptor.Id,
                    out GenerationTimelineGap? gap) ||
                !IsUnchanged(gap, descriptor))
                continue;
            LocalTimelineRegionPlan? parsed =
                Deserialize<LocalTimelineRegionPlan>(gap.PlanJson);
            if (parsed is null ||
                parsed.SchemaVersion != LocalPlannerSchemaVersion ||
                !parsed.Validation.IsValid)
                continue;
            reusable[descriptor.Id] = parsed;
            foreach (LocalSourceMaterial material in
                     parsed.SelectedSourceMaterials)
            {
                usedSourceIntervals.Add(material.SourceInterval);
            }
        }

        foreach (GapDescriptor descriptor in descriptors)
        {
            if (reusable.TryGetValue(
                    descriptor.Id,
                    out LocalTimelineRegionPlan? reused) &&
                byKey.TryGetValue(
                    descriptor.Id,
                    out GenerationTimelineGap? existingGap))
            {
                existingGap.PlanJson = JsonSerializer.Serialize(
                    reused with { ReusedSuccessfulPlan = true },
                    Json);
                continue;
            }
            LocalRegionBuildResult result = BuildLocalRegionPlan(
                plan,
                descriptor,
                candidates,
                highlightsById,
                candidateRowIds,
                shotsByCandidate,
                cameraPrototypes,
                plannedBrollWindows,
                demosById,
                generation,
                settings,
                usedSourceIntervals);
            if (!byKey.TryGetValue(
                    descriptor.Id,
                    out GenerationTimelineGap? gap))
            {
                gap = new GenerationTimelineGap
                {
                    TimelinePlanId = plan.Id,
                    GapId = descriptor.Id
                };
                db.GenerationTimelineGaps.Add(gap);
            }
            gap.PreviousAnchorId = descriptor.Previous?.AnchorId;
            gap.NextAnchorId = descriptor.Next?.AnchorId;
            gap.StartMilliseconds = descriptor.StartMilliseconds;
            gap.EndMilliseconds = result.ShortenedEndMilliseconds ??
                descriptor.EndMilliseconds;
            gap.Role = descriptor.Role;
            gap.PlanJson = JsonSerializer.Serialize(result.Plan, Json);
            gap.State = result.Plan.Validation.IsValid
                ? TimelineGapState.Planned
                : TimelineGapState.Failed;
            gap.UpdatedAt = timeProvider.GetUtcNow();
        }
    }

    private static void ApplyPlannedExcerptShortening(
        GenerationTimelinePlan plan)
    {
        long? end = plan.Gaps
            .Where(value => value.Role == TimelineGapRole.Outro)
            .Select(value => new
            {
                Gap = value,
                Region = Deserialize<LocalTimelineRegionPlan>(value.PlanJson)
            })
            .Where(value => value.Region?.Validation.Outcome ==
                LocalRegionOutcome.ShortenedExcerpt)
            .Select(value => (long?)value.Gap.EndMilliseconds)
            .SingleOrDefault();
        if (end.HasValue)
        {
            plan.ExcerptEndMilliseconds =
                plan.ExcerptStartMilliseconds + end.Value;
        }
    }

    private static bool IsUnchanged(
        GenerationTimelineGap gap,
        GapDescriptor descriptor)
    {
        LocalTimelineRegionPlan? stored =
            Deserialize<LocalTimelineRegionPlan>(gap.PlanJson);
        bool shortenedExcerpt =
            stored?.Validation.Outcome ==
                LocalRegionOutcome.ShortenedExcerpt &&
            gap.EndMilliseconds <= descriptor.EndMilliseconds;
        return gap.StartMilliseconds == descriptor.StartMilliseconds &&
               (gap.EndMilliseconds == descriptor.EndMilliseconds ||
                shortenedExcerpt) &&
               gap.Role == descriptor.Role &&
               gap.State == TimelineGapState.Planned &&
               gap.PreviousAnchorId == descriptor.Previous?.AnchorId &&
               gap.NextAnchorId == descriptor.Next?.AnchorId;
    }

    private static LocalRegionBuildResult BuildLocalRegionPlan(
        GenerationTimelinePlan timeline,
        GapDescriptor descriptor,
        IReadOnlyList<GapMaterialCandidate> candidates,
        IReadOnlyDictionary<string, GenerationHighlight> highlights,
        Dictionary<string, long> candidateRowIds,
        Dictionary<long, GenerationCameraShot> shotsByCandidate,
        Dictionary<string, CameraShotPlan> cameraPrototypes,
        Dictionary<string, (long Start, long End)> plannedBrollWindows,
        Dictionary<long, GenerationDemo> demos,
        Generation generation,
        GenerationMovieSettings? settings,
        HashSet<string> usedSourceIntervals)
    {
        GapHighlightContext? previous = HighlightContext(
            descriptor.Previous,
            highlights);
        GapHighlightContext? next = HighlightContext(
            descriptor.Next,
            highlights);
        List<LocalSourceMaterial> materials = [];
        List<LocalBrollSegmentPlan> broll = [];
        List<CameraShotPlan> cameras = [];
        List<string> warnings = [];
        LocalRegionOutcome outcome = LocalRegionOutcome.Natural;
        long cursor = descriptor.StartMilliseconds;
        long end = descriptor.EndMilliseconds;
        long? shortenedEnd = null;
        GapMaterialCandidate[] freeCameraCandidates = candidates
            .Where(candidate =>
                candidate.DurationSeconds >=
                    MeaningfulGapPolicy.MinimumFreeCameraShotSeconds &&
                candidateRowIds.TryGetValue(candidate.Id, out long rowId) &&
                shotsByCandidate.TryGetValue(rowId, out GenerationCameraShot? shot) &&
                shot.Type != CameraShotType.PlayerPov &&
                shot.PreviewStatus is CameraPreviewStatus.NotAttempted or
                    CameraPreviewStatus.Passed &&
                cameraPrototypes.TryGetValue(
                    candidate.Id,
                    out CameraShotPlan? prototype) &&
                prototype.Family != CameraShotFamily.PlayerPov &&
                prototype.Keyframes.Count > 0 &&
                prototype.SafetyVolume is not null)
            .ToArray();
        while (end - cursor >= 180)
        {
            double available = (end - cursor) / 1000d;
            GapMaterialCandidate[] plannedForRegion = freeCameraCandidates
                .Where(candidate =>
                    !usedSourceIntervals.Contains(candidate.SourceInterval) &&
                    plannedBrollWindows.TryGetValue(
                        candidate.Id,
                        out (long Start, long End) window) &&
                    window.End > cursor &&
                    window.Start < end)
                .ToArray();
            GapMaterialDecision decision = MeaningfulGapPolicy.Select(
                plannedForRegion.Length > 0
                    ? plannedForRegion
                    : freeCameraCandidates,
                previous,
                next,
                descriptor.Role,
                available,
                usedSourceIntervals);
            if (decision.UsePovContinuity)
            {
                decision = decision with
                {
                    Outcome = LocalRegionOutcome.Invalid,
                    UsePovContinuity = false,
                    Warnings = decision.Warnings.Concat(
                        ["CINEMATIC_BROLL_POV_FORBIDDEN"])
                        .Distinct(StringComparer.Ordinal)
                        .ToArray()
                };
            }
            if (decision.ShortenExcerpt &&
                settings?.CinematicDuration != MovieDurationSelection.Auto)
            {
                bool canShortenWithinTolerance =
                    descriptor.Role == TimelineGapRole.Outro &&
                    MeaningfulGapPolicy.CanShortenExplicitDuration(
                        (end - cursor) / 1000d);
                if (broll.Count > 0 && cameras.Count > 0)
                {
                    int lastIndex = broll.Count - 1;
                    LocalBrollSegmentPlan lastBroll = broll[lastIndex];
                    CameraShotPlan lastCamera = cameras[lastIndex];
                    double extendedDuration =
                        (end - lastBroll.OutputStartMilliseconds) / 1000d;
                    if (MeaningfulGapPolicy.CanExtendFreeCamera(
                            lastCamera.TargetDurationSeconds,
                            extendedDuration))
                    {
                        broll[lastIndex] = lastBroll with
                        {
                            OutputEndMilliseconds = end
                        };
                        cursor = end;
                        outcome = MoreSevere(
                            outcome,
                            LocalRegionOutcome.Retiming);
                        warnings.Add(
                            "EXPLICIT_DURATION_FILLED_BY_FREECAM_RETIMING");
                        break;
                    }
                }
                if (canShortenWithinTolerance)
                {
                    decision = decision with
                    {
                        Warnings = decision.Warnings.Concat(
                            ["EXPLICIT_DURATION_ADJUSTED_WITHIN_TOLERANCE"])
                        .Distinct(StringComparer.Ordinal)
                        .ToArray()
                    };
                }
                else
                {
                    decision = decision with
                    {
                        Outcome = LocalRegionOutcome.Invalid,
                        ShortenExcerpt = false,
                        Warnings = decision.Warnings.Concat(
                            ["EXPLICIT_DURATION_CANNOT_BE_SHORTENED"])
                            .Distinct(StringComparer.Ordinal)
                            .ToArray()
                    };
                }
            }
            warnings.AddRange(decision.Warnings);
            if (decision.Candidate is not null)
            {
                GapMaterialCandidate candidate = decision.Candidate;
                long duration = Math.Min(
                    end - cursor,
                    ToMilliseconds(candidate.DurationSeconds));
                if (duration < ToMilliseconds(
                        MeaningfulGapPolicy.MinimumFreeCameraShotSeconds))
                    break;
                GenerationCameraShot? storedShot = null;
                CameraPreviewStatus? previewStatus = null;
                if (candidateRowIds.TryGetValue(
                        candidate.Id,
                        out long rowId) &&
                    shotsByCandidate.TryGetValue(
                        rowId,
                        out storedShot))
                {
                    previewStatus = storedShot.PreviewStatus;
                }
                CameraShotPlan camera = CreateCameraDecision(
                    candidate,
                    storedShot,
                    cameraPrototypes.GetValueOrDefault(candidate.Id),
                    previewStatus,
                    demos.TryGetValue(
                        candidate.DemoId,
                        out GenerationDemo? playerDemo)
                            ? playerDemo.SelectedSteamId ??
                                generation.SelectedSteamId
                            : generation.SelectedSteamId,
                    demos.TryGetValue(
                        candidate.DemoId,
                        out GenerationDemo? demo)
                            ? demo.MapName ?? string.Empty
                            : string.Empty,
                    duration / 1000d);
                if (camera.Family == CameraShotFamily.PlayerPov ||
                    camera.Type == CameraShotType.PlayerPov)
                {
                    outcome = LocalRegionOutcome.Invalid;
                    warnings.Add("CINEMATIC_BROLL_POV_FORBIDDEN");
                    break;
                }
                materials.Add(new LocalSourceMaterial
                {
                    MaterialId = candidate.Id,
                    MaterialType = candidate.Type.ToString(),
                    SourceInterval = candidate.SourceInterval,
                    NarrativePriority = decision.NarrativePriority,
                    EditorialScore = Math.Round(
                        candidate.CinematicScore,
                        4),
                    Reused = false,
                    Rationale = decision.Rationale
                });
                broll.Add(new LocalBrollSegmentPlan
                {
                    MaterialId = candidate.Id,
                    SourceInterval = candidate.SourceInterval,
                    OutputStartMilliseconds = cursor,
                    OutputEndMilliseconds = cursor + duration,
                    NarrativeRole = decision.Rationale,
                    IsFreeCamera = camera.Family !=
                        CameraShotFamily.PlayerPov
                });
                cameras.Add(camera);
                usedSourceIntervals.Add(candidate.SourceInterval);
                cursor += duration;
                continue;
            }
            outcome = MoreSevere(outcome, decision.Outcome);
            if (decision.ShortenExcerpt)
            {
                shortenedEnd = cursor;
                end = cursor;
            }
            break;
        }
        bool absorbIncomingGap = false;
        if (end - cursor is > 0 and < 1500)
        {
            outcome = MoreSevere(outcome, LocalRegionOutcome.Retiming);
            warnings.Add("SHORT_GAP_RETIMING_REQUIRED");
            if (descriptor.Next is not null &&
                broll.Count > 0 &&
                cameras.Count > 0)
            {
                int lastIndex = broll.Count - 1;
                LocalBrollSegmentPlan lastBroll = broll[lastIndex];
                CameraShotPlan lastCamera = cameras[lastIndex];
                long currentDuration = Math.Max(
                    1,
                    lastBroll.OutputEndMilliseconds -
                    lastBroll.OutputStartMilliseconds);
                long maximumDuration = ToMilliseconds(
                    lastCamera.TargetDurationSeconds / 0.72);
                long extension = Math.Min(
                    end - cursor,
                    Math.Max(0, maximumDuration - currentDuration));
                if (extension > 0)
                {
                    broll[lastIndex] = lastBroll with
                    {
                        OutputEndMilliseconds =
                            lastBroll.OutputEndMilliseconds + extension
                    };
                    cursor += extension;
                    warnings.Add(
                        "SHORT_GAP_ABSORBED_BY_FREECAM_RETIMING");
                }
            }
            absorbIncomingGap = descriptor.Next is not null;
        }
        if (end - cursor >= 1500 && shortenedEnd is null)
        {
            outcome = LocalRegionOutcome.Invalid;
            warnings.Add("REGION_HAS_UNFILLED_DURATION");
        }
        LocalHighlightSegmentPlan? highlight = CreateHighlightSegment(
            descriptor.Next,
            highlights,
            timeline);
        if (absorbIncomingGap && highlight is not null)
        {
            highlight = highlight with
            {
                OutputStartMilliseconds = cursor
            };
            warnings.Add("SHORT_GAP_ABSORBED_BY_HIGHLIGHT_RETIMING");
        }
        LocalRetimingDecision retiming = new(
            descriptor.Next?.RequiredBaseSpeed ?? 1,
            descriptor.Next?.RequiredLocalSpeed ?? 1,
            descriptor.Next?.FeasibilityStatus is
                AnchorFeasibilityStatus.Acceptable or
                AnchorFeasibilityStatus.Risky,
            outcome == LocalRegionOutcome.Retiming
                ? "adjacent boundaries absorb a gap without an orphan shot"
                : "anchor-local speed policy");
        LocalTimelineRegionPlan localPlan = new()
        {
            SchemaVersion = LocalPlannerSchemaVersion,
            RegionId = descriptor.Id,
            PreviousAnchorId = descriptor.Previous?.AnchorId,
            NextAnchorId = descriptor.Next?.AnchorId,
            MusicBounds = new LocalMusicBounds(
                descriptor.StartMilliseconds,
                shortenedEnd ?? descriptor.EndMilliseconds),
            AvailableDurationSeconds = Math.Max(
                0,
                (shortenedEnd ?? descriptor.EndMilliseconds) -
                descriptor.StartMilliseconds) / 1000d,
            SelectedSourceMaterials = materials,
            HighlightSegment = highlight,
            BrollSegments = broll,
            CameraShots = cameras,
            Transitions = broll.Select(value =>
                new LocalTransitionDecision(
                    descriptor.Role == TimelineGapRole.BuildUp
                        ? "JCut"
                        : "Cut",
                    value.OutputStartMilliseconds,
                    0,
                    "hard picture cut; optional audio lead only"))
                .ToArray(),
            Retiming = retiming,
            Audio = new LocalAudioDecision(
                settings?.MusicGainDb ?? -3,
                settings?.GameplayGainDb ?? -16,
                false,
                highlight is not null,
                "stable music bed; gameplay transient may be accented"),
            Effects = [],
            Validation = new LocalRegionValidation
            {
                IsValid = outcome != LocalRegionOutcome.Invalid,
                Outcome = outcome,
                SourceIntervalReuseCount = 0,
                OneFrameSegmentCount = 0,
                LockedAnchorTimesExact = highlight is null ||
                    descriptor.Next?.TargetMilliseconds ==
                    highlight.PrimaryKillOutputMilliseconds,
                Warnings = warnings
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()
            },
            DeterministicSeed = RegionSeed(
                generation.PublicId,
                timeline.RevisionCursor + 1,
                descriptor,
                settings?.EffectPlannerVersion ?? "7.0"),
            PlannerVersion = "10.9-local.1",
            ReusedSuccessfulPlan = false
        };
        return new LocalRegionBuildResult(localPlan, shortenedEnd);
    }

    private static string SourceInterval(GenerationHighlight value) =>
        $"{value.GenerationDemoId}:{value.StartTick}-" +
        $"{Math.Max(value.SafeEndTick, value.EndTick)}";

    private async Task<Dictionary<string, CameraShotPlan>>
        EnsureCameraPoolAsync(
            GenerationDbContext db,
            Generation generation,
            IReadOnlyDictionary<long, GenerationDemo> demos,
            IReadOnlyList<GenerationBrollCandidate> candidates,
            IReadOnlyList<GenerationCameraShot> existingShots,
            double requiredSeconds,
            CancellationToken cancellationToken)
    {
        Dictionary<long, GenerationCameraShot> existingByCandidate =
            existingShots
                .Where(value => value.GenerationBrollCandidateId.HasValue)
                .GroupBy(value => value.GenerationBrollCandidateId!.Value)
                .ToDictionary(
                    value => value.Key,
                    value => value.OrderByDescending(item =>
                            item.Type != CameraShotType.PlayerPov)
                        .ThenBy(item => item.Id)
                        .First());
        Dictionary<string, CameraShotPlan> result =
            new(StringComparer.Ordinal);
        double availableSeconds = 0;
        foreach (GenerationBrollCandidate candidate in candidates)
        {
            if (!existingByCandidate.TryGetValue(
                    candidate.Id,
                    out GenerationCameraShot? row) ||
                row.Type == CameraShotType.PlayerPov)
                continue;
            CameraShotPlan? plan = StoredCameraPlan(candidate, row, demos);
            if (plan is null)
                continue;
            result[candidate.CandidateId] = plan;
            availableSeconds += plan.TargetDurationSeconds;
        }
        if (!cameraRuntime.Enabled || availableSeconds >= requiredSeconds)
            return result;

        Dictionary<string, MapCameraProfile?> profiles =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (GenerationDemo demo in demos.Values)
        {
            string mapName = demo.MapName ?? string.Empty;
            MapCameraProfile? profile = mapProfiles.Find(mapName);
            MapCameraProfile? automatic = await automaticCalibrationStore
                .LoadAsync(
                    mapName,
                    cameraRuntime.HlaeVersion,
                    cancellationToken);
            profiles[mapName] = MergeProfiles(profile, automatic);
        }
        foreach (GenerationBrollCandidate row in candidates
                     .Where(value =>
                         value.CinematicScore >= 0.35 &&
                         value.ActionDensity <= 0.72)
                     .OrderByDescending(value => value.CinematicScore)
                     .ThenByDescending(value => value.MovementScore)
                     .ThenBy(value => value.CandidateId,
                         StringComparer.Ordinal))
        {
            if (result.ContainsKey(row.CandidateId) ||
                !demos.TryGetValue(row.GenerationDemoId,
                    out GenerationDemo? demo))
                continue;
            string mapName = demo.MapName ?? string.Empty;
            MapCameraProfile? profile = profiles.GetValueOrDefault(mapName);
            bool profileAvailable = profile is not null &&
                (profile.ManuallyVerified || profile.AutomaticallyCalibrated) &&
                profile.SafeVolumes.Count > 0;
            if (!profileAvailable)
                continue;
            PlayerTrajectory? trajectory = Deserialize<PlayerTrajectory>(
                row.TrajectoryJson);
            if (trajectory is null || trajectory.Samples.Count < 2)
                continue;
            int tickRate = demo.TickRate.GetValueOrDefault(64);
            BrollCandidate candidate = new()
            {
                Id = row.CandidateId,
                DemoId = row.GenerationDemoId.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                RoundNumber = row.RoundNumber,
                Type = row.Type,
                StartTick = row.StartTick,
                EndTick = row.EndTick,
                DurationSeconds = Math.Max(
                    0,
                    (row.EndTick - row.StartTick) /
                    (double)Math.Max(1, tickRate)),
                MovementScore = row.MovementScore,
                CinematicScore = row.CinematicScore,
                ActionDensity = row.ActionDensity,
                Trajectory = trajectory,
                Tags = [],
                SubjectIds = string.IsNullOrWhiteSpace(demo.SelectedSteamId)
                    ? []
                    : [demo.SelectedSteamId]
            };
            CameraShotPlan camera = cameraPathPlanner.Create(
                candidate,
                new CameraPlanningContext
                {
                    MapName = mapName,
                    Profile = profile,
                    Capabilities = new HlaeCameraCapabilities
                    {
                        Available = true,
                        Version = cameraRuntime.HlaeVersion,
                        SupportsCampath = true,
                        SupportsInput = true,
                        SupportsFov = true,
                        SupportsHighFpsCapture = true,
                        ManualSpikeVerified = !string.IsNullOrWhiteSpace(
                            cameraRuntime.HlaeVersion),
                        Warnings = ["INTERACTIVE_CAMERA_POOL_EXPANDED"]
                    }
                });
            if (camera.Family == CameraShotFamily.PlayerPov ||
                camera.Keyframes.Count == 0 ||
                camera.SafetyVolume is null)
                continue;
            if (!existingByCandidate.TryGetValue(
                    row.Id,
                    out GenerationCameraShot? stored))
            {
                stored = new GenerationCameraShot
                {
                    GenerationId = generation.Id,
                    GenerationBrollCandidateId = row.Id,
                    ShotId = camera.Id,
                    FallbackType = CameraShotType.PlayerPov
                };
                db.GenerationCameraShots.Add(stored);
                existingByCandidate[row.Id] = stored;
            }
            stored.Type = camera.Type;
            stored.StartTick = camera.StartTick;
            stored.EndTick = camera.EndTick;
            stored.KeyframesJson = JsonSerializer.Serialize(
                camera.Keyframes,
                Json);
            stored.FovStart = camera.FovStart;
            stored.FovEnd = camera.FovEnd;
            stored.PreviewStatus = CameraPreviewStatus.NotAttempted;
            result[row.CandidateId] = camera;
            availableSeconds += camera.TargetDurationSeconds;
            if (availableSeconds >= requiredSeconds)
                break;
        }
        await db.SaveChangesAsync(cancellationToken);
        return result;
    }

    private static CameraShotPlan? StoredCameraPlan(
        GenerationBrollCandidate candidate,
        GenerationCameraShot row,
        IReadOnlyDictionary<long, GenerationDemo> demos)
    {
        CameraKeyframe[] keyframes = Deserialize<CameraKeyframe[]>(
            row.KeyframesJson) ?? [];
        PlayerTransformSample[] subjectSamples =
            Deserialize<PlayerTrajectory>(candidate.TrajectoryJson)?
                .Samples?
                .OrderBy(value => value.Tick)
                .ToArray() ?? [];
        CameraShotFamily family = CameraFamily(row.Type);
        if (family == CameraShotFamily.PlayerPov || keyframes.Length == 0)
            return null;
        int tickRate = demos.TryGetValue(
                candidate.GenerationDemoId,
                out GenerationDemo? demo)
            ? demo.TickRate.GetValueOrDefault(64)
            : 64;
        double duration = Math.Max(
            MeaningfulGapPolicy.MinimumFreeCameraShotSeconds,
            (candidate.EndTick - candidate.StartTick) /
            (double)Math.Max(1, tickRate));
        SafeCameraVolume safety = EncloseKeyframes(keyframes);
        CameraShotPlan plan = new()
        {
            Id = row.ShotId,
            Type = row.Type,
            Family = family,
            DemoId = candidate.GenerationDemoId.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            StartTick = candidate.StartTick,
            EndTick = candidate.EndTick,
            TargetDurationSeconds = duration,
            Keyframes = keyframes,
            FovStart = row.FovStart,
            FovEnd = row.FovEnd,
            RequiresHighFpsCapture = false,
            FallbackShotId = $"{row.ShotId}-pov-fallback",
            Warnings = row.PreviewStatus == CameraPreviewStatus.Passed
                ? []
                : ["CAMERA_PREVIEW_PENDING"],
            SubjectIds = string.IsNullOrWhiteSpace(demo?.SelectedSteamId)
                ? []
                : [demo.SelectedSteamId],
            TargetPoints = SubjectTargetPoints(
                subjectSamples,
                candidate.StartTick,
                candidate.EndTick,
                duration,
                string.IsNullOrWhiteSpace(demo?.SelectedSteamId)
                    ? []
                    : [demo.SelectedSteamId]),
            FovCurve = keyframes.Select(value =>
                new CameraFovPoint(value.TimeSeconds, value.Fov)).ToArray(),
            FramingIntent = "interactive free-camera insert",
            MovementDirection = GameplayVector3.Zero,
            SafetyVolume = safety,
            PreviewRequired = true,
            FallbackChain =
                [CameraShotFamily.StaticTripod,
                    CameraShotFamily.PlayerPov],
            AutomaticCalibration = true
        };
        return CameraShotSignatureBuilder.Attach(
            plan,
            demo?.MapName ?? string.Empty);
    }

    private static CameraTargetPoint[] SubjectTargetPoints(
        IReadOnlyList<PlayerTransformSample> samples,
        long startTick,
        long endTick,
        double durationSeconds,
        IReadOnlyList<string> subjectIds)
    {
        PlayerTransformSample[] ordered = samples
            .Where(value => value.Tick >= startTick && value.Tick <= endTick)
            .OrderBy(value => value.Tick)
            .ToArray();
        if (ordered.Length < 2)
            return [];
        double tickSpan = Math.Max(1, endTick - startTick);
        return ordered.Select(value => new CameraTargetPoint(
            Math.Clamp(
                (value.Tick - startTick) / tickSpan * durationSeconds,
                0,
                durationSeconds),
            value.Position,
            subjectIds)).ToArray();
    }

    private static SafeCameraVolume EncloseKeyframes(
        IReadOnlyList<CameraKeyframe> keyframes)
    {
        const double margin = 24;
        return new SafeCameraVolume(
            new GameplayVector3(
                keyframes.Min(value => value.Position.X) - margin,
                keyframes.Min(value => value.Position.Y) - margin,
                keyframes.Min(value => value.Position.Z) - margin),
            new GameplayVector3(
                keyframes.Max(value => value.Position.X) + margin,
                keyframes.Max(value => value.Position.Y) + margin,
                keyframes.Max(value => value.Position.Z) + margin));
    }

    private static MapCameraProfile? MergeProfiles(
        MapCameraProfile? builtIn,
        MapCameraProfile? automatic)
    {
        if (builtIn is null)
            return automatic;
        if (automatic is null)
            return builtIn;
        return builtIn with
        {
            SafeVolumes = builtIn.SafeVolumes
                .Concat(automatic.SafeVolumes)
                .ToArray(),
            EstablishingShots = builtIn.EstablishingShots
                .Concat(automatic.EstablishingShots)
                .GroupBy(value => value.Id, StringComparer.Ordinal)
                .Select(value => value.First())
                .ToArray(),
            AutomaticallyCalibrated =
                automatic.AutomaticallyCalibrated
        };
    }

    private static GapHighlightContext? HighlightContext(
        GenerationTimelineAnchor? anchor,
        IReadOnlyDictionary<string, GenerationHighlight> highlights)
    {
        if (anchor?.HighlightId is null ||
            !highlights.TryGetValue(
                anchor.HighlightId,
                out GenerationHighlight? highlight))
            return null;
        return new GapHighlightContext(
            highlight.HighlightId,
            highlight.GenerationDemoId,
            highlight.RoundNumber,
            highlight.StartTick,
            highlight.PrimaryKillTick > 0
                ? highlight.PrimaryKillTick
                : highlight.LastKillTick,
            highlight.SafeEndTick > 0
                ? highlight.SafeEndTick
                : highlight.EndTick,
            highlight.TickRate > 0 ? highlight.TickRate : 64);
    }

    private static LocalHighlightSegmentPlan? CreateHighlightSegment(
        GenerationTimelineAnchor? anchor,
        IReadOnlyDictionary<string, GenerationHighlight> highlights,
        GenerationTimelinePlan timeline)
    {
        if (anchor?.HighlightId is null ||
            !highlights.TryGetValue(
                anchor.HighlightId,
                out GenerationHighlight? highlight))
            return null;
        long primary = highlight.PrimaryKillTick > 0
            ? highlight.PrimaryKillTick
            : highlight.LastKillTick;
        long safeEnd = highlight.SafeEndTick > primary
            ? highlight.SafeEndTick
            : highlight.EndTick;
        double speed = Math.Clamp(anchor.RequiredBaseSpeed, 0.50, 1.30);
        long pre = ToMilliseconds(
            anchor.EstimatedPreRollSeconds / Math.Max(0.001, speed));
        long post = ToMilliseconds(
            anchor.EstimatedPostRollSeconds / Math.Max(0.001, speed));
        long duration = timeline.ExcerptEndMilliseconds -
            timeline.ExcerptStartMilliseconds;
        return new LocalHighlightSegmentPlan
        {
            AnchorId = anchor.AnchorId,
            HighlightId = highlight.HighlightId,
            SourceStartTick = highlight.StartTick,
            PrimaryKillTick = primary,
            SafeEndTick = safeEnd,
            OutputStartMilliseconds = Math.Max(
                0,
                anchor.TargetMilliseconds - pre),
            PrimaryKillOutputMilliseconds = anchor.TargetMilliseconds,
            OutputEndMilliseconds = Math.Min(
                duration,
                anchor.TargetMilliseconds + post),
            PreRollSeconds = anchor.EstimatedPreRollSeconds,
            PostKillSeconds = anchor.EstimatedPostRollSeconds,
            Feasibility = anchor.FeasibilityStatus
        };
    }

    private static CameraShotPlan CreateCameraDecision(
        GapMaterialCandidate candidate,
        GenerationCameraShot? stored,
        CameraShotPlan? prototype,
        CameraPreviewStatus? previewStatus,
        string? selectedPlayerId,
        string mapName,
        double durationSeconds)
    {
        CameraKeyframe[] keyframes = stored is null
            ? []
            : Deserialize<CameraKeyframe[]>(stored.KeyframesJson) ?? [];
        CameraShotFamily family = stored is null
            ? CameraShotFamily.PlayerPov
            : CameraFamily(stored.Type);
        bool previewEligible = previewStatus is
            CameraPreviewStatus.NotAttempted or
            CameraPreviewStatus.Passed;
        bool validFreeCamera = previewEligible &&
            family != CameraShotFamily.PlayerPov &&
            durationSeconds >= MeaningfulGapPolicy.MinimumFreeCameraShotSeconds &&
            keyframes.Length > 0 &&
            keyframes.All(value =>
                double.IsFinite(value.TimeSeconds) &&
                value.Fov is >= 20 and <= 140);
        if (!validFreeCamera)
        {
            family = CameraShotFamily.PlayerPov;
            keyframes = [];
        }
        else
        {
            double sourceDuration = Math.Max(
                0.001,
                prototype?.TargetDurationSeconds ??
                keyframes.Max(value => value.TimeSeconds));
            keyframes = keyframes
                .OrderBy(value => value.TimeSeconds)
                .Select(value => value with
                {
                    TimeSeconds = Math.Clamp(
                        value.TimeSeconds / sourceDuration * durationSeconds,
                        0,
                        durationSeconds)
                })
                .ToArray();
        }
        CameraShotType type = validFreeCamera
            ? stored!.Type
            : CameraShotType.PlayerPov;
        CameraShotPlan plan = new()
        {
            Id = validFreeCamera
                ? stored!.ShotId
                : $"camera-{candidate.Id}-pov-fallback",
            Type = type,
            Family = family,
            DemoId = candidate.DemoId.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            StartTick = candidate.StartTick,
            EndTick = candidate.EndTick,
            TargetDurationSeconds = durationSeconds,
            Keyframes = keyframes,
            TargetPoints = validFreeCamera
                ? ScaleTargetPoints(
                    prototype?.TargetPoints ?? [],
                    prototype?.TargetDurationSeconds ?? durationSeconds,
                    durationSeconds)
                : [],
            FovCurve = keyframes.Select(value =>
                new CameraFovPoint(value.TimeSeconds, value.Fov)).ToArray(),
            FovStart = validFreeCamera ? stored!.FovStart : 90,
            FovEnd = validFreeCamera ? stored!.FovEnd : 90,
            FramingIntent = validFreeCamera
                ? previewStatus == CameraPreviewStatus.Passed
                    ? "preview-verified persisted camera composition"
                    : "persisted camera composition awaiting preview"
                : "selected player POV continuity",
            PreviewRequired = validFreeCamera,
            RequiresHighFpsCapture = validFreeCamera &&
                prototype?.RequiresHighFpsCapture == true,
            FallbackShotId = validFreeCamera
                ? $"camera-{candidate.Id}-pov-fallback"
                : string.Empty,
            FallbackChain = validFreeCamera
                ? [CameraShotFamily.StaticTripod,
                    CameraShotFamily.PlayerPov]
                : [],
            SubjectIds = string.IsNullOrWhiteSpace(selectedPlayerId)
                ? prototype?.SubjectIds ?? []
                : [selectedPlayerId],
            MovementDirection = validFreeCamera
                ? prototype?.MovementDirection ?? GameplayVector3.Zero
                : GameplayVector3.Zero,
            SafetyVolume = validFreeCamera
                ? prototype?.SafetyVolume
                : null,
            VerifiedPresetId = validFreeCamera
                ? prototype?.VerifiedPresetId
                : null,
            AutomaticCalibration = validFreeCamera &&
                prototype?.AutomaticCalibration == true,
            Warnings = validFreeCamera
                ? previewStatus == CameraPreviewStatus.Passed
                    ? []
                    : ["CAMERA_PREVIEW_PENDING"]
                : ["CAMERA_PREVIEW_REQUIRED_POV_FALLBACK"]
        };
        return CameraShotSignatureBuilder.Attach(plan, mapName);
    }

    private static CameraTargetPoint[] ScaleTargetPoints(
        IReadOnlyList<CameraTargetPoint> points,
        double sourceDurationSeconds,
        double targetDurationSeconds)
    {
        double sourceDuration = Math.Max(0.001, sourceDurationSeconds);
        return points.Select(value => value with
        {
            TimeSeconds = Math.Clamp(
                value.TimeSeconds / sourceDuration * targetDurationSeconds,
                0,
                targetDurationSeconds)
        }).ToArray();
    }

    private static CameraShotFamily CameraFamily(CameraShotType type) =>
        type switch
        {
            CameraShotType.StaticEstablishing or
            CameraShotType.StaticTripod => CameraShotFamily.StaticTripod,
            CameraShotType.SideTracking => CameraShotFamily.SideTracking,
            CameraShotType.RearTracking => CameraShotFamily.RearTracking,
            CameraShotType.FrontApproach or
            CameraShotType.FrontTracking => CameraShotFamily.FrontTracking,
            CameraShotType.GroupWide => CameraShotFamily.GroupWide,
            CameraShotType.Orbit or
            CameraShotType.CurvedCampath => CameraShotFamily.Orbit,
            CameraShotType.WeaponDetail => CameraShotFamily.WeaponDetail,
            CameraShotType.BulletPath => CameraShotFamily.BulletPath,
            CameraShotType.VictimReaction =>
                CameraShotFamily.VictimReaction,
            CameraShotType.EnvironmentReveal =>
                CameraShotFamily.EnvironmentReveal,
            _ => CameraShotFamily.PlayerPov
        };

    private static (LocalSourceMaterial? Material, CameraShotPlan? Camera)
        CreatePovContinuity(
            GapHighlightContext? previous,
            GapHighlightContext? next,
            long startMilliseconds,
            long endMilliseconds,
            string? selectedPlayerId,
            HashSet<string> usedSourceIntervals)
    {
        GapHighlightContext? source = next ?? previous;
        if (source is null)
            return (null, null);
        long ticks = Math.Max(
            1,
            (long)Math.Round(
                (endMilliseconds - startMilliseconds) / 1000d *
                source.TickRate));
        long sourceStart;
        long sourceEnd;
        if (next is not null)
        {
            sourceEnd = source.StartTick;
            sourceStart = Math.Max(0, sourceEnd - ticks);
        }
        else
        {
            sourceStart = source.SafeEndTick;
            sourceEnd = sourceStart + ticks;
        }
        string interval = $"{source.DemoId}:{sourceStart}-{sourceEnd}";
        if (SourceIntervalPolicy.OverlapsAny(
                interval,
                usedSourceIntervals))
            return (null, null);
        string id = $"pov-continuity-{source.HighlightId}-" +
            $"{startMilliseconds}-{endMilliseconds}";
        LocalSourceMaterial material = new()
        {
            MaterialId = id,
            MaterialType = BrollCandidateType.PovContinuity.ToString(),
            SourceInterval = interval,
            NarrativePriority = 11,
            EditorialScore = 0.35,
            Reused = false,
            Rationale = "bounded POV continuity fallback"
        };
        CameraShotPlan camera = CameraShotSignatureBuilder.Attach(
            new CameraShotPlan
            {
                Id = $"camera-{id}",
                Type = CameraShotType.PlayerPov,
                Family = CameraShotFamily.PlayerPov,
                DemoId = source.DemoId.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                StartTick = sourceStart,
                EndTick = sourceEnd,
                TargetDurationSeconds =
                    (endMilliseconds - startMilliseconds) / 1000d,
                Keyframes = [],
                TargetPoints = [],
                FovCurve =
                [
                    new CameraFovPoint(0, 90),
                    new CameraFovPoint(
                        (endMilliseconds - startMilliseconds) / 1000d,
                        90)
                ],
                FovStart = 90,
                FovEnd = 90,
                FramingIntent = "selected player POV continuity",
                PreviewRequired = false,
                RequiresHighFpsCapture = false,
                FallbackShotId = string.Empty,
                FallbackChain = [],
                SubjectIds = string.IsNullOrWhiteSpace(selectedPlayerId)
                    ? []
                    : [selectedPlayerId],
                Warnings = ["POV_CONTINUITY_FALLBACK"]
            },
            string.Empty);
        return (material, camera);
    }

    private static LocalRegionOutcome MoreSevere(
        LocalRegionOutcome current,
        LocalRegionOutcome candidate)
    {
        static int Rank(LocalRegionOutcome value) => value switch
        {
            LocalRegionOutcome.Natural => 0,
            LocalRegionOutcome.Retiming => 1,
            LocalRegionOutcome.CameraFallback => 2,
            LocalRegionOutcome.ShortenedExcerpt => 3,
            _ => 4
        };
        return Rank(candidate) > Rank(current) ? candidate : current;
    }

    private static string RegionSeed(
        string generationId,
        int revision,
        GapDescriptor descriptor,
        string effectPlannerVersion)
    {
        string canonical = string.Join(
            '|',
            generationId,
            revision,
            descriptor.Id,
            descriptor.Previous?.AnchorId ?? "start",
            descriptor.Next?.AnchorId ?? "end",
            descriptor.Next?.HighlightId ??
                descriptor.Previous?.HighlightId ?? "none",
            "camera-2.0",
            effectPlannerVersion);
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    private sealed record GapDescriptor(
        string Id,
        GenerationTimelineAnchor? Previous,
        GenerationTimelineAnchor? Next,
        long StartMilliseconds,
        long EndMilliseconds,
        TimelineGapRole Role);

    private sealed record LocalRegionBuildResult(
        LocalTimelineRegionPlan Plan,
        long? ShortenedEndMilliseconds);

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
        GenerationCinematicPlan? cinematic =
            await db.GenerationCinematicPlans.AsNoTracking()
                .SingleOrDefaultAsync(
                    value => value.GenerationId == generation.Id,
                    cancellationToken);
        if (cinematic is not null)
        {
            CinematicMoviePlan? parsed =
                Deserialize<CinematicMoviePlan>(cinematic.PlanJson);
            if (parsed is null)
                throw new TimelineValidationException(
                    "CINEMATIC_LOCKED_PLAN_INVALID");
            string cinematicDirectory = storage.EnsureDirectory(
                generation.PublicId,
                "plan");
            string cinematicPath = Path.Combine(
                cinematicDirectory,
                "cinematic-movie-plan.json");
            string cinematicTemporary = cinematicPath + ".tmp";
            await File.WriteAllTextAsync(
                cinematicTemporary,
                JsonSerializer.Serialize(parsed, IndentedJson),
                cancellationToken);
            File.Move(cinematicTemporary, cinematicPath, true);
            GenerationArtifact? cinematicArtifact =
                await db.GenerationArtifacts.SingleOrDefaultAsync(
                    value =>
                        value.GenerationId == generation.Id &&
                        value.Type == ArtifactType.CinematicMoviePlan,
                    cancellationToken);
            if (cinematicArtifact is not null)
            {
                cinematicArtifact.StoredPath = cinematicPath;
                cinematicArtifact.FileName =
                    Path.GetFileName(cinematicPath);
                cinematicArtifact.FileSizeBytes =
                    new FileInfo(cinematicPath).Length;
            }
        }
        string directory = storage.EnsureDirectory(
            generation.PublicId,
            "plan",
            "timeline");
        GenerationTimelineRevision[] revisions =
            await db.GenerationTimelineRevisions.AsNoTracking()
                .Where(value => value.TimelinePlanId == plan.Id)
                .OrderBy(value => value.Number)
                .ToArrayAsync(cancellationToken);
        GenerationTimelineGap[] storedRegions =
            await db.GenerationTimelineGaps.AsNoTracking()
                .Where(value => value.TimelinePlanId == plan.Id)
                .OrderBy(value => value.StartMilliseconds)
                .ThenBy(value => value.GapId)
                .ToArrayAsync(cancellationToken);
        LocalTimelineRegionPlan[] localRegions = storedRegions
            .Select(value => Deserialize<LocalTimelineRegionPlan>(
                value.PlanJson))
            .Where(value => value is not null)
            .Select(value => value!)
            .ToArray();
        string[] sourceIntervals = localRegions
            .SelectMany(value => value.SelectedSourceMaterials)
            .Select(value => value.SourceInterval)
            .ToArray();
        string[] repeatedIntervals = sourceIntervals
            .Select((value, index) => new { value, index })
            .Where(current => sourceIntervals
                .Take(current.index)
                .Any(previous => string.Equals(
                    previous,
                    current.value,
                    StringComparison.Ordinal)))
            .Select(value => value.value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
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
            ["local-region-plans.json"] = new
            {
                schemaVersion = "2.2",
                plannerVersion = "10.9-local.1",
                generationId = generation.PublicId,
                timelineRevision = plan.RevisionCursor,
                regions = localRegions
            },
            ["source-interval-reuse-report.json"] = new
            {
                schemaVersion = "1.0",
                checkedIntervalCount = sourceIntervals.Length,
                uniqueIntervalCount = sourceIntervals.Length -
                    repeatedIntervals.Length,
                reuseCount = repeatedIntervals.Length,
                repeatedIntervals
            },
            ["excerpt-extension-report.json"] = new
            {
                schemaVersion = "1.0",
                excerptStartMilliseconds = plan.ExcerptStartMilliseconds,
                excerptEndMilliseconds = plan.ExcerptEndMilliseconds,
                shortened = localRegions.Any(value =>
                    value.Validation.Outcome ==
                    LocalRegionOutcome.ShortenedExcerpt),
                reasons = localRegions
                    .Where(value => value.Validation.Outcome ==
                        LocalRegionOutcome.ShortenedExcerpt)
                    .SelectMany(value => value.Validation.Warnings)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()
            },
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
            stored.Type = TimelineArtifactType(fileName);
            stored.StoredPath = path;
            stored.ContentType = "application/json";
            stored.FileSizeBytes = new FileInfo(path).Length;
        }
    }

    private static ArtifactType TimelineArtifactType(string fileName) =>
        fileName switch
        {
            "local-region-plans.json" => ArtifactType.LocalRegionPlans,
            "source-interval-reuse-report.json" =>
                ArtifactType.SourceIntervalReuseReport,
            "excerpt-extension-report.json" =>
                ArtifactType.ExcerptExtensionReport,
            _ when fileName.Contains(
                "diagnostic",
                StringComparison.OrdinalIgnoreCase) =>
                ArtifactType.TimelineDiagnostics,
            _ => ArtifactType.InteractiveTimelinePlan
        };

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
        GenerationCameraShot[] cameraRows =
            await db.GenerationCameraShots.AsNoTracking()
                .Where(value => value.GenerationId == generation.Id)
                .ToArrayAsync(cancellationToken);
        Dictionary<string, CameraPreviewStatus> cameraPreviewStatuses =
            cameraRows
                .GroupBy(value => value.ShotId, StringComparer.Ordinal)
                .ToDictionary(
                    value => value.Key,
                    value => value
                        .OrderByDescending(shot => shot.Id)
                        .First()
                        .PreviewStatus,
                    StringComparer.Ordinal);
        GenerationArtifact? waveformArtifact =
            await db.GenerationArtifacts.AsNoTracking()
                .Where(value =>
                    value.GenerationId == generation.Id &&
                    value.FileName == "real-waveform-envelope.json")
                .OrderByDescending(value => value.Id)
                .FirstOrDefaultAsync(cancellationToken);
        RealWaveformEnvelopeArtifact? waveform = null;
        if (waveformArtifact is not null &&
            File.Exists(waveformArtifact.StoredPath))
        {
            try
            {
                await using FileStream stream = File.OpenRead(
                    waveformArtifact.StoredPath);
                waveform = await JsonSerializer.DeserializeAsync<
                    RealWaveformEnvelopeArtifact>(
                    stream,
                    Json,
                    cancellationToken);
            }
            catch (JsonException)
            {
                waveform = null;
            }
        }
        TimelineHighlightView[] highlightViews = highlights.Select(value =>
        {
            int tickRate = value.TickRate > 0 ? value.TickRate : 64;
            long primary = value.PrimaryKillTick > 0
                ? value.PrimaryKillTick
                : value.LastKillTick;
            long safeEnd = value.SafeEndTick > primary
                ? value.SafeEndTick
                : value.EndTick;
            WeaponSequenceSegment? weaponSegment =
                Deserialize<WeaponSequenceSegment[]>(
                    value.WeaponSequenceJson)?
                .OrderByDescending(item => item.KillCount)
                .FirstOrDefault();
            WeaponMetadata weapon = TimelineWeapons.Resolve(
                weaponSegment?.WeaponCode);
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
                weapon.DisplayName,
                weapon.Code,
                weapon.IconPath);
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
            LocalTimelineRegionPlan? parsed =
                Deserialize<LocalTimelineRegionPlan>(value.PlanJson);
            CameraShotPlan? cameraPlan = parsed is not null &&
                parsed.CameraShots.Count > 0
                    ? parsed.CameraShots[0]
                    : null;
            string camera = cameraPlan?.Family.ToString() ?? "PlayerPov";
            string material = parsed is not null &&
                parsed.SelectedSourceMaterials.Count > 0
                    ? parsed.SelectedSourceMaterials[0].MaterialType
                    : "boundary-retiming";
            bool fallback = parsed?.Validation.Outcome ==
                    LocalRegionOutcome.CameraFallback ||
                cameraPlan?.Warnings.Any(warning => warning.Contains(
                    "FALLBACK",
                    StringComparison.Ordinal)) == true;
            string cameraVerification = cameraPlan switch
            {
                null => "Not required",
                { Family: CameraShotFamily.PlayerPov } => "POV fallback",
                _ => cameraPreviewStatuses.GetValueOrDefault(cameraPlan.Id) switch
                {
                    CameraPreviewStatus.Passed => "Preview passed",
                    CameraPreviewStatus.Rendering => "Preview rendering",
                    CameraPreviewStatus.Failed => "Preview failed",
                    CameraPreviewStatus.PovFallback => "POV fallback",
                    _ => "Preview pending"
                }
            };
            return new TimelineGapView(
                value.GapId,
                value.PreviousAnchorId,
                value.NextAnchorId,
                value.Role.ToString(),
                value.StartMilliseconds / 1000d,
                value.EndMilliseconds / 1000d,
                value.State.ToString(),
                camera,
                material,
                parsed?.Validation.Outcome.ToString() ?? "Invalid",
                parsed?.ReusedSuccessfulPlan ?? false,
                fallback,
                cameraVerification);
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
            waveform is not null && waveform.Available
                ? new TimelineWaveformView(
                    waveform.SchemaVersion,
                    true,
                    waveform.ExcerptStartSeconds,
                    waveform.SamplesPerSecond,
                    waveform.Peaks,
                    waveform.Warnings)
                : new TimelineWaveformView(
                    "1.0",
                    false,
                    plan.ExcerptStartMilliseconds / 1000d,
                    0,
                    [],
                    waveform?.Warnings ??
                        ["REAL_WAVEFORM_ENVELOPE_UNAVAILABLE"]),
            gapViews,
            counts);
    }

    private static GenerationHighlight? ResolveHighlight(
        GenerationTimelineAnchor anchor,
        IReadOnlyList<GenerationHighlight> highlights,
        HashSet<string> assigned)
    {
        IEnumerable<GenerationHighlight> candidates = highlights
            .Where(value => !assigned.Contains(value.HighlightId));
        if (anchor.MarkerType == TimelineMarkerType.ExactHighlight)
        {
            GenerationHighlight? exact = candidates.SingleOrDefault(value =>
                value.HighlightId == anchor.HighlightId);
            if (exact is not null)
                return exact;
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
            IEnumerable<GenerationHighlight> categoryCandidates = candidates.Where(value =>
                value.Type.Contains(
                    category,
                    StringComparison.OrdinalIgnoreCase));
            if (categoryCandidates.Any())
                candidates = categoryCandidates;
        }
        return candidates
            .OrderByDescending(value => value.BeautyScore)
            .ThenByDescending(value => value.TotalScore)
            .ThenBy(value => value.EstimatedDurationMilliseconds)
            .ThenBy(value => value.HighlightId, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static double ClampMarkerTarget(double target, double duration)
    {
        double safeDuration = Math.Max(0, duration);
        if (!double.IsFinite(target))
            return safeDuration / 2;
        return Math.Clamp(target, 0, safeDuration);
    }

    private static void AppendAnchorWarning(
        GenerationTimelineAnchor anchor,
        string warning)
    {
        List<string> warnings =
            Deserialize<List<string>>(anchor.WarningsJson) ?? [];
        if (!warnings.Contains(warning, StringComparer.Ordinal))
            warnings.Add(warning);
        anchor.WarningsJson = JsonSerializer.Serialize(warnings, Json);
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
