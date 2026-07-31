using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Cs2Highlight.Music;

namespace Cs2Highlight.Web.Domain;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TimelineDirectorMode
{
    Auto,
    Assisted,
    ManualAnchors
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TimelineMarkerType
{
    ExactHighlight,
    BestSolo,
    BestDouble,
    BestTriple,
    BestQuad,
    BestAce,
    BestAvailableHighlight
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AnchorFeasibilityStatus
{
    Natural,
    Acceptable,
    Risky,
    Invalid
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TimelinePlanState
{
    Draft,
    Ready,
    Locked
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TimelineGapRole
{
    Intro,
    Calm,
    BuildUp,
    BetweenHighlights,
    Recovery,
    Resolution,
    Outro
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TimelineGapState
{
    Dirty,
    Planned,
    Failed,
    Locked
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LocalRegionOutcome
{
    Natural,
    Retiming,
    CameraFallback,
    ShortenedExcerpt,
    Invalid
}

public sealed record LocalMusicBounds(
    long StartMilliseconds,
    long EndMilliseconds);

public sealed record LocalSourceMaterial
{
    public required string MaterialId { get; init; }
    public required string MaterialType { get; init; }
    public required string SourceInterval { get; init; }
    public required int NarrativePriority { get; init; }
    public required double EditorialScore { get; init; }
    public required bool Reused { get; init; }
    public required string Rationale { get; init; }
}

public sealed record LocalHighlightSegmentPlan
{
    public required string AnchorId { get; init; }
    public required string HighlightId { get; init; }
    public required long SourceStartTick { get; init; }
    public required long PrimaryKillTick { get; init; }
    public required long SafeEndTick { get; init; }
    public required long OutputStartMilliseconds { get; init; }
    public required long PrimaryKillOutputMilliseconds { get; init; }
    public required long OutputEndMilliseconds { get; init; }
    public required double PreRollSeconds { get; init; }
    public required double PostKillSeconds { get; init; }
    public required AnchorFeasibilityStatus Feasibility { get; init; }
}

public sealed record LocalBrollSegmentPlan
{
    public required string MaterialId { get; init; }
    public required string SourceInterval { get; init; }
    public required long OutputStartMilliseconds { get; init; }
    public required long OutputEndMilliseconds { get; init; }
    public required string NarrativeRole { get; init; }
    public required bool IsFreeCamera { get; init; }
}

public sealed record LocalTransitionDecision(
    string Type,
    long BoundaryMilliseconds,
    int DurationMilliseconds,
    string Rationale);

public sealed record LocalRetimingDecision(
    double BaseSpeed,
    double LocalSpeed,
    bool UsesLocalRamp,
    string Rationale);

public sealed record LocalAudioDecision(
    double MusicGainDb,
    double GameplayGainDb,
    bool MusicDuckingEnabled,
    bool GameplayTransientAccent,
    string Rationale);

public sealed record LocalEffectDecision(
    string EffectType,
    string Rarity,
    long StartMilliseconds,
    long EndMilliseconds,
    string Motivation);

public sealed record LocalRegionValidation
{
    public required bool IsValid { get; init; }
    public required LocalRegionOutcome Outcome { get; init; }
    public required int SourceIntervalReuseCount { get; init; }
    public required int OneFrameSegmentCount { get; init; }
    public required bool LockedAnchorTimesExact { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
}

public sealed record LocalTimelineRegionPlan
{
    public required string SchemaVersion { get; init; }
    public required string RegionId { get; init; }
    public string? PreviousAnchorId { get; init; }
    public string? NextAnchorId { get; init; }
    public required LocalMusicBounds MusicBounds { get; init; }
    public required double AvailableDurationSeconds { get; init; }
    public required IReadOnlyList<LocalSourceMaterial> SelectedSourceMaterials
        { get; init; }
    public LocalHighlightSegmentPlan? HighlightSegment { get; init; }
    public required IReadOnlyList<LocalBrollSegmentPlan> BrollSegments
        { get; init; }
    public required IReadOnlyList<CameraShotPlan> CameraShots { get; init; }
    public required IReadOnlyList<LocalTransitionDecision> Transitions
        { get; init; }
    public required LocalRetimingDecision Retiming { get; init; }
    public required LocalAudioDecision Audio { get; init; }
    public required IReadOnlyList<LocalEffectDecision> Effects { get; init; }
    public required LocalRegionValidation Validation { get; init; }
    public required string DeterministicSeed { get; init; }
    public required string PlannerVersion { get; init; }
    public required bool ReusedSuccessfulPlan { get; init; }
}

public sealed record UserKillAnchor
{
    public required string Id { get; init; }
    public required string GenerationId { get; init; }
    public required TimelineMarkerType MarkerType { get; init; }
    public string? HighlightId { get; init; }
    public required double TargetMusicTimeSeconds { get; init; }
    public required bool IsLocked { get; init; }
    public required int Order { get; init; }
    public required AnchorFeasibilityStatus Feasibility { get; init; }
    public required double RequiredBaseSpeed { get; init; }
    public required double RequiredLocalSpeed { get; init; }
    public required double EstimatedPreRollSeconds { get; init; }
    public required double EstimatedPostRollSeconds { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
}

public sealed class InteractiveRetimingOptions
{
    public double NaturalMinimumSpeed { get; init; } = 0.90;
    public double NaturalMaximumSpeed { get; init; } = 1.10;
    public double AcceptableMinimumSpeed { get; init; } = 0.75;
    public double AcceptableMaximumSpeed { get; init; } = 1.25;
    public double HighFpsMinimumSpeed { get; init; } = 0.50;
    public double MaximumNormalCaptureSlowdown { get; init; } = 0.75;
    public double MaximumPostKillSpeed { get; init; } = 1.05;
}

public sealed class GenerationTimelinePlan
{
    public long Id { get; set; }
    public long GenerationId { get; set; }
    public Generation Generation { get; set; } = null!;
    public TimelineDirectorMode Mode { get; set; } = TimelineDirectorMode.Assisted;
    public TimelinePlanState State { get; set; } = TimelinePlanState.Draft;
    public long ExcerptStartMilliseconds { get; set; }
    public long ExcerptEndMilliseconds { get; set; }
    public int RevisionNumber { get; set; }
    public int RevisionCursor { get; set; }
    public string DiagnosticsJson { get; set; } = "{}";
    [MaxLength(64), ConcurrencyCheck]
    public string ConcurrencyToken { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? LockedAt { get; set; }
    public List<GenerationTimelineAnchor> Anchors { get; set; } = [];
    public List<GenerationTimelineGap> Gaps { get; set; } = [];
    public List<GenerationTimelineRevision> Revisions { get; set; } = [];
}

public sealed class GenerationTimelineAnchor
{
    public long Id { get; set; }
    public long TimelinePlanId { get; set; }
    public GenerationTimelinePlan TimelinePlan { get; set; } = null!;
    [MaxLength(64)] public string AnchorId { get; set; } = string.Empty;
    public TimelineMarkerType MarkerType { get; set; }
    [MaxLength(256)] public string? HighlightId { get; set; }
    public long TargetMilliseconds { get; set; }
    public bool IsLocked { get; set; }
    public int Order { get; set; }
    public AnchorFeasibilityStatus FeasibilityStatus { get; set; }
    public double RequiredBaseSpeed { get; set; } = 1;
    public double RequiredLocalSpeed { get; set; } = 1;
    public double EstimatedPreRollSeconds { get; set; }
    public double EstimatedPostRollSeconds { get; set; }
    public string WarningsJson { get; set; } = "[]";
}

public sealed class GenerationTimelineGap
{
    public long Id { get; set; }
    public long TimelinePlanId { get; set; }
    public GenerationTimelinePlan TimelinePlan { get; set; } = null!;
    [MaxLength(64)] public string GapId { get; set; } = string.Empty;
    [MaxLength(64)] public string? PreviousAnchorId { get; set; }
    [MaxLength(64)] public string? NextAnchorId { get; set; }
    public long StartMilliseconds { get; set; }
    public long EndMilliseconds { get; set; }
    public TimelineGapRole Role { get; set; }
    public string PlanJson { get; set; } = "{}";
    public TimelineGapState State { get; set; } = TimelineGapState.Dirty;
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class GenerationTimelineRevision
{
    public long Id { get; set; }
    public long TimelinePlanId { get; set; }
    public GenerationTimelinePlan TimelinePlan { get; set; } = null!;
    public int Number { get; set; }
    [MaxLength(64)] public string Reason { get; set; } = string.Empty;
    public string SnapshotJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed record TimelineAnchorSnapshot(
    string Id,
    TimelineMarkerType MarkerType,
    string? HighlightId,
    long TargetMilliseconds,
    bool IsLocked,
    int Order);

public sealed record TimelineRevisionSnapshot(
    TimelineDirectorMode Mode,
    IReadOnlyList<TimelineAnchorSnapshot> Anchors);
