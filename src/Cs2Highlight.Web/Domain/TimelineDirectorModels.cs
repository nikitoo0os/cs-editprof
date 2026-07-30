using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

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
