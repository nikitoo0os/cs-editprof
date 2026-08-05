using System.Text.Json.Serialization;

namespace Cs2Highlight.Analysis;

public sealed record DemoAnalysis(
    string SchemaVersion,
    ParserInfo Parser,
    DemoMetadata Demo,
    IReadOnlyList<DemoPlayer> Players,
    IReadOnlyList<DemoRound> Rounds,
    IReadOnlyList<KillEvent> Kills,
    IReadOnlyList<string> Warnings)
{
    public IReadOnlyList<GameplayTimelineFrame> Timeline { get; init; } = [];
}

public sealed record GameplayVector3(double X, double Y, double Z)
{
    public static readonly GameplayVector3 Zero = new(0, 0, 0);

    public double DistanceTo(GameplayVector3 other)
    {
        double x = X - other.X;
        double y = Y - other.Y;
        double z = Z - other.Z;
        return Math.Sqrt(x * x + y * y + z * z);
    }
}

public sealed record PlayerTransform(
    string PlayerId,
    GameplayVector3 Position,
    GameplayVector3 Velocity,
    GameplayVector3 ViewAngles);

public sealed record GameplayEventReference(
    string Type,
    long Tick,
    string? WeaponCode = null);

public sealed record GameplayTimelineFrame(
    long Tick,
    int RoundNumber,
    PlayerTransform Player,
    double MovementSpeed,
    double ActionDensity,
    bool Alive,
    bool InFreezeTime,
    bool NearKillEvent,
    IReadOnlyList<GameplayEventReference> Events)
{
    public string? Team { get; init; }
    public string? ActiveWeapon { get; init; }
    public bool Firing { get; init; }
    public bool Reloading { get; init; }
    public bool UtilityActive { get; init; }
    public bool Scoped { get; init; }
    public bool Planting { get; init; }
    public bool Defusing { get; init; }
    public bool HasBomb { get; init; }
}

public sealed record PlayerTrajectory(
    IReadOnlyList<PlayerTransformSample> Samples);

public sealed record PlayerTransformSample(
    long Tick,
    GameplayVector3 Position,
    GameplayVector3 ViewAngles);

public sealed record ParserInfo(string Name, string Version);

public sealed record DemoMetadata(
    string FileName,
    string MapName,
    int TickRate,
    long DurationTicks,
    long? DurationMilliseconds);

public sealed record DemoPlayer(string PlayerId, string? SteamId, string Name);

public sealed record DemoRound(
    int RoundNumber,
    long StartTick,
    long? FreezeEndTick,
    long EndTick,
    string? Winner,
    string? Reason);

public sealed record KillEvent(
    int EventIndex,
    long Tick,
    int RoundNumber,
    string? KillerPlayerId,
    string? KillerName,
    string VictimPlayerId,
    string VictimName,
    string? AssisterPlayerId,
    string Weapon,
    bool Headshot,
    string? KillerTeam,
    string? VictimTeam)
{
    public bool? Wallbang { get; init; }
    public bool? OneTap { get; init; }
    public bool? NoScope { get; init; }
    public bool? ThroughSmoke { get; init; }
    public bool? RoundEndingKill { get; init; }
    public bool? LastEnemyKill { get; init; }
    public int? KillerHealth { get; init; }
    public double? DistanceMeters { get; init; }
    public int? ShotsSinceLastKill { get; init; }
    public GameplayVector3? ShooterPosition { get; init; }
    public GameplayVector3? VictimPosition { get; init; }
    public GameplayVector3? HitPosition { get; init; }
    public string BulletTrajectoryStatus { get; init; } =
        "UnavailableExactImpact";
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HighlightType
{
    SoloKill,
    DoubleKill,
    TripleKill,
    QuadKill,
    Ace,
    [Obsolete("Headshot streak is represented as a tag on SoloKill or a multikill.")]
    HeadshotStreak
}

public sealed record ScoreBreakdown(
    double BaseKillScore,
    double HeadshotBonus,
    double TypeBonus,
    double FastSequenceBonus,
    double RoundWinBonus,
    double LastKillRoundEndBonus,
    double Total)
{
    public double BeautyBaseScore { get; init; }
    public double WallbangBonus { get; init; }
    public double OneTapBonus { get; init; }
    public double KnifeBonus { get; init; }
    public double ZeusBonus { get; init; }
    public double NoScopeBonus { get; init; }
    public double ThroughSmokeBonus { get; init; }
    public double LowHealthBonus { get; init; }
    public double LongDistanceBonus { get; init; }
    public double LastEnemyBonus { get; init; }
    public double WeaponSwapBonus { get; init; }
    public double CombatScore { get; init; }
    public double BeautyScore { get; init; }
}

public sealed record KillDescriptor(
    int EventIndex,
    long Tick,
    string KillerPlayerId,
    string VictimPlayerId,
    string WeaponCode,
    bool Headshot)
{
    public bool? Wallbang { get; init; }
    public bool? OneTap { get; init; }
    public bool? NoScope { get; init; }
    public bool? ThroughSmoke { get; init; }
    public bool RoundEndingKill { get; init; }
    public bool LastEnemyKill { get; init; }
    public int? KillerHealth { get; init; }
    public double? DistanceMeters { get; init; }
    public int? ShotsSinceLastKill { get; init; }
    public GameplayVector3? ShooterPosition { get; init; }
    public GameplayVector3? VictimPosition { get; init; }
    public GameplayVector3? HitPosition { get; init; }
}

public enum WeaponCategory { Rifle, Sniper, Pistol, Smg, Heavy, Knife, Equipment, Unknown }

public sealed record WeaponMetadata(
    string Code,
    string DisplayName,
    string IconPath,
    WeaponCategory Category);

public sealed record WeaponSequenceSegment(
    string WeaponCode,
    string DisplayName,
    string IconPath,
    int KillCount,
    bool SwapBefore);

public sealed record HighlightCandidate(
    string Id,
    HighlightType Type,
    string PlayerId,
    string PlayerName,
    int RoundNumber,
    long FirstKillTick,
    long LastKillTick,
    long StartTick,
    long EndTick,
    int KillCount,
    int HeadshotCount,
    double Score,
    ScoreBreakdown ScoreBreakdown,
    IReadOnlyList<int> SourceEventIndexes,
    IReadOnlyList<string> Tags)
{
    public string SourceDemoId { get; init; } = string.Empty;
    public string MapName { get; init; } = string.Empty;
    public double CombatScore { get; init; } = Score;
    public double BeautyScore { get; init; }
    public double TotalScore => Score;
    public IReadOnlyList<KillDescriptor> Kills { get; init; } = [];
    public IReadOnlyList<WeaponSequenceSegment> WeaponSequence { get; init; } = [];
    public long EstimatedDurationMilliseconds { get; init; }
    public int TickRate { get; init; }
    public long? RoundStartTick { get; init; }
    public long PrimaryKillTick { get; init; }
    public long SafeEndTick { get; init; }
    public SafeClipBounds? SafeBounds { get; init; }
}

public sealed class HighlightDetectionOptions
{
    public string? TargetPlayerId { get; init; }
    public double MaximumGapBetweenKillsSeconds { get; init; } = 6;
    public double MaximumSequenceDurationSeconds { get; init; } = 12;
    public double PreRollSeconds { get; init; } = 1;
    public double PostRollSeconds { get; init; } = 1;
    public double RoundEndHoldSeconds { get; init; } = 1;
    public double MinimumClipDurationSeconds { get; init; } = 2;
    public double MaximumClipDurationSeconds { get; init; } = 15;
    public int MinimumKills { get; init; } = 2;
    public int MinimumHeadshotsForStreak { get; init; } = 2;
    // Legacy all-or-nothing switch retained for schema 1.0 callers.
    public bool ClampToRoundBounds { get; init; }
    public bool ClampStartToRoundBounds { get; init; } = true;
    public bool ClampEndToRoundBounds { get; init; }
    public HighlightScoringOptions Scoring { get; init; } = new();
    public SoloKillDetectionOptions SoloKills { get; init; } = new();
    public BeautyScoringOptions BeautyScoring { get; init; } = new();
    public SafeClipTimingOptions SafeTiming { get; init; } = new();
}

public sealed class SafeClipTimingOptions
{
    public double SoloPostKillHoldSeconds { get; init; } = 1.0;
    public double MultikillPostKillHoldSeconds { get; init; } = 1.0;
    public double RoundEndingPostKillHoldSeconds { get; init; } = 1.0;
    public double MinimumClipDurationSeconds { get; init; } = 2.0;
    public double DeathAnimationAllowanceSeconds { get; init; } = 0.5;
    public double KillfeedAllowanceSeconds { get; init; } = 1.0;
    public double AudioTailAllowanceSeconds { get; init; } = 0.3;
}

public sealed record SafeClipBounds(
    double PlannedStartSeconds,
    double SafeStartSeconds,
    double PrimaryKillSeconds,
    double LastKillSeconds,
    double SafeEndSeconds,
    double PlannedEndSeconds);

public sealed class SoloKillDetectionOptions
{
    public double MinimumBeautyScore { get; init; } = 20;
    public bool IncludeAllSoloKills { get; init; }
    public int MaximumSoloCandidatesPerDemo { get; init; } = 30;
}

public sealed class BeautyScoringOptions
{
    public double BaseKillScore { get; init; } = 5;
    public double HeadshotBonus { get; init; } = 20;
    public double WallbangBonus { get; init; } = 25;
    public double OneTapBonus { get; init; } = 20;
    public double KnifeBonus { get; init; } = 35;
    public double ZeusBonus { get; init; } = 30;
    public double NoScopeBonus { get; init; } = 25;
    public double ThroughSmokeBonus { get; init; } = 20;
    public double RoundEndingBonus { get; init; } = 10;
    public double LastEnemyBonus { get; init; } = 10;
    public double LowHealthBonus { get; init; } = 10;
    public int LowHealthThreshold { get; init; } = 20;
    public double LongDistanceBonus { get; init; } = 10;
    public double LongDistanceThresholdMeters { get; init; } = 25;
    public double WeaponSwapBonus { get; init; } = 5;
}

public sealed class HighlightScoringOptions
{
    public double KillWeight { get; init; } = 20;
    public double HeadshotStreakBonus { get; init; } = 10;
    public double AdditionalHeadshotWeight { get; init; } = 4;
    public double TripleKillBonus { get; init; } = 15;
    public double QuadKillBonus { get; init; } = 30;
    public double AceBonus { get; init; } = 50;
    public double FastSequenceBonus { get; init; } = 10;
    public double RoundWinBonus { get; init; } = 10;
    public double LastKillRoundEndBonus { get; init; } = 5;
    public double FastSequenceSeconds { get; init; } = 5;
}

public sealed record HighlightsDocument(
    string SchemaVersion,
    string DemoFile,
    DateTimeOffset GeneratedAt,
    HighlightOptionsDocument Options,
    IReadOnlyList<HighlightCandidate> Candidates);

public sealed record HighlightOptionsDocument(
    string? TargetPlayerId,
    double MaximumGapBetweenKillsSeconds,
    double PreRollSeconds,
    double PostRollSeconds)
{
    public double RoundEndHoldSeconds { get; init; }
    public double MinimumClipDurationSeconds { get; init; }
    public double MaximumClipDurationSeconds { get; init; }
}

public sealed record BestHighlightDocument(
    string SchemaVersion,
    bool Found,
    HighlightCandidate? Highlight,
    string? Reason);

public sealed record AnalysisArtifacts(
    string DemoAnalysisPath,
    string HighlightsPath,
    string BestHighlightPath,
    string? RenderJobPath,
    HighlightCandidate? BestHighlight);

public enum AnalysisStage
{
    ValidatingInput,
    RunningDemoParser,
    ValidatingAnalysis,
    DetectingHighlights,
    SelectingBestHighlight,
    BuildingRenderJob,
    WritingArtifacts,
    Completed,
    Failed,
    Cancelled
}

public sealed record AnalysisError(
    string Code,
    string Message,
    AnalysisStage Stage,
    bool Retryable,
    Exception? Exception = null);

public sealed class AnalysisException(AnalysisError error) : Exception(error.Message, error.Exception)
{
    public AnalysisError Error { get; } = error;
}
