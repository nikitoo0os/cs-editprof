using System.Text.Json.Serialization;
using Cs2Highlight.Analysis;

namespace Cs2Highlight.Music;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MusicSectionType
{
    Intro,
    Calm,
    Verse,
    BuildUp,
    PreDrop,
    Chorus,
    Drop,
    HighEnergy,
    Breakdown,
    Outro,
    Unknown
}

public sealed record MusicFrame
{
    public required double TimeSeconds { get; init; }
    public required double Energy { get; init; }
    public required double BassEnergy { get; init; }
    public required double OnsetStrength { get; init; }
    public required double SpectralFlux { get; init; }
    public required double SpectralBrightness { get; init; }
    public required double Novelty { get; init; }
    public required double RhythmicDensity { get; init; }
    public required double HarmonicChange { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MusicalPeakType
{
    Beat,
    StrongBeat,
    Downbeat,
    PhraseStart,
    SectionStart,
    ChorusStart,
    DropStart,
    BassImpact,
    EnergyPeak
}

public sealed record MusicalPeak
{
    public required string Id { get; init; }
    public required MusicalPeakType Type { get; init; }
    public required double TimeSeconds { get; init; }
    public required double Strength { get; init; }
    public required double Confidence { get; init; }
    public required string SectionId { get; init; }
}

public sealed record MusicNarrative
{
    public required double DurationSeconds { get; init; }
    public required IReadOnlyList<MusicSection> Sections { get; init; }
    public required IReadOnlyList<MusicalPeak> Peaks { get; init; }
    public required IReadOnlyList<MusicFrame> Frames { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
}

public enum MovieDurationSelection
{
    Auto,
    Seconds15,
    Seconds30,
    Seconds45
}

public enum CinematicEditIntensity
{
    Calm,
    Balanced,
    Dynamic
}

public sealed class MovieDurationOptions
{
    public double ShortHighlightThresholdSeconds { get; init; } = 15;
    public double MaximumShortMovieDurationSeconds { get; init; } = 30;
    public double MaximumBrollToHighlightRatio { get; init; } = 1.0;
    public double MaximumIntroSeconds { get; init; } = 6;
    public double MaximumOutroSeconds { get; init; } = 4;
    public double MaximumMovieDurationSeconds { get; init; } = 60;
    public MovieDurationSelection Selection { get; init; } = MovieDurationSelection.Auto;
}

public sealed record MovieDurationBudget(
    double HighlightDurationSeconds,
    double MaximumBrollSeconds,
    double MaximumTotalSeconds,
    double TargetSeconds);

public sealed record MusicExcerptPlan
{
    public required double StartSeconds { get; init; }
    public required double EndSeconds { get; init; }
    public double DurationSeconds => EndSeconds - StartSeconds;
    public required IReadOnlyList<string> SectionIds { get; init; }
    public required IReadOnlyList<MusicalPeak> Peaks { get; init; }
    public required int RequiredPeakCount { get; init; }
    public required int UsablePeakCount { get; init; }
    public required double Score { get; init; }
    public required bool IsValid { get; init; }
    public required IReadOnlyDictionary<string, double> ScoreBreakdown { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BrollCandidateType
{
    EstablishingShot,
    PlayerApproach,
    PlayerRotation,
    SideMovement,
    RearMovement,
    UtilityPreparation,
    UtilityThrow,
    WeaponDraw,
    WeaponReload,
    WeaponSwitch,
    ScopePreparation,
    BombApproach,
    BombPlant,
    BombDefuse,
    PreFightSetup,
    PostFightExit,
    TeamMovement,
    TeamSetup,
    PovContinuity,
    EnvironmentShot
}

public sealed record BrollCandidate
{
    public required string Id { get; init; }
    public required string DemoId { get; init; }
    public required int RoundNumber { get; init; }
    public required BrollCandidateType Type { get; init; }
    public required long StartTick { get; init; }
    public required long EndTick { get; init; }
    public required double DurationSeconds { get; init; }
    public required double MovementScore { get; init; }
    public required double CinematicScore { get; init; }
    public required double ActionDensity { get; init; }
    public required PlayerTrajectory Trajectory { get; init; }
    public required IReadOnlyList<string> Tags { get; init; }
    public IReadOnlyList<string> SubjectIds { get; init; } = [];
    public IReadOnlyDictionary<string, PlayerTrajectory> SubjectTrajectories
        { get; init; } = new Dictionary<string, PlayerTrajectory>(
            StringComparer.Ordinal);
}

public sealed record GameplayInterval(long StartTick, long EndTick);

public sealed record BrollDetectionContext
{
    public required string DemoId { get; init; }
    public required string PlayerId { get; init; }
    public required int TickRate { get; init; }
    public required IReadOnlyList<GameplayTimelineFrame> Frames { get; init; }
    public required IReadOnlyList<GameplayInterval> ExcludedIntervals { get; init; }
    public double MinimumDurationSeconds { get; init; } = 1.5;
    public double MaximumDurationSeconds { get; init; } = 4;
    public double MinimumMovementSpeed { get; init; } = 20;
    public double MaximumIdleActionDensity { get; init; } = 0.08;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CameraShotFamily
{
    PlayerPov,
    StaticTripod,
    SideTracking,
    RearTracking,
    FrontTracking,
    GroupWide,
    Orbit,
    WeaponDetail,
    BulletPath,
    EnvironmentReveal
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CameraShotType
{
    PlayerPov,
    StaticEstablishing,
    DollyIn,
    DollyOut,
    SideTracking,
    RearTracking,
    FrontApproach,
    ElevatedTracking,
    LowAngleTracking,
    LinearCampath,
    CurvedCampath,
    EnvironmentReveal,
    StaticTripod,
    FrontTracking,
    GroupWide,
    Orbit,
    WeaponDetail,
    BulletPath
}

public sealed record CameraKeyframe
{
    public required double TimeSeconds { get; init; }
    public required GameplayVector3 Position { get; init; }
    public required GameplayVector3 Rotation { get; init; }
    public required double Fov { get; init; }
}

public sealed record CameraTargetPoint(
    double TimeSeconds,
    GameplayVector3 Position,
    IReadOnlyList<string> SubjectIds);

public sealed record CameraFovPoint(
    double TimeSeconds,
    double Fov);

public sealed record CameraShotSignature
{
    public required CameraShotFamily Family { get; init; }
    public required string MapName { get; init; }
    public required string SourceInterval { get; init; }
    public required IReadOnlyList<string> SubjectIds { get; init; }
    public required string ApproximateStartCell { get; init; }
    public required string ApproximateEndCell { get; init; }
    public required string MovementVector { get; init; }
    public required string FovRange { get; init; }
    public required string OrbitDirection { get; init; }
    public required string FramingClass { get; init; }
    public required string DeterministicHash { get; init; }
}

public sealed record CameraShotPlan
{
    public required string Id { get; init; }
    public required CameraShotType Type { get; init; }
    public required string DemoId { get; init; }
    public required long StartTick { get; init; }
    public required long EndTick { get; init; }
    public required double TargetDurationSeconds { get; init; }
    public required IReadOnlyList<CameraKeyframe> Keyframes { get; init; }
    public required double FovStart { get; init; }
    public required double FovEnd { get; init; }
    public required bool RequiresHighFpsCapture { get; init; }
    public required string FallbackShotId { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public CameraShotFamily Family { get; init; } = CameraShotFamily.PlayerPov;
    public IReadOnlyList<string> SubjectIds { get; init; } = [];
    public IReadOnlyList<CameraTargetPoint> TargetPoints { get; init; } = [];
    public IReadOnlyList<CameraFovPoint> FovCurve { get; init; } = [];
    public string FramingIntent { get; init; } = "POV continuity";
    public GameplayVector3 MovementDirection { get; init; } =
        GameplayVector3.Zero;
    public SafeCameraVolume? SafetyVolume { get; init; }
    public bool PreviewRequired { get; init; }
    public string? VerifiedPresetId { get; init; }
    public IReadOnlyList<CameraShotFamily> FallbackChain { get; init; } =
        [CameraShotFamily.PlayerPov];
    public CameraShotSignature? Signature { get; init; }
}

public sealed record SafeCameraVolume(
    GameplayVector3 Minimum,
    GameplayVector3 Maximum)
{
    public bool Contains(GameplayVector3 point) =>
        point.X >= Minimum.X && point.X <= Maximum.X &&
        point.Y >= Minimum.Y && point.Y <= Maximum.Y &&
        point.Z >= Minimum.Z && point.Z <= Maximum.Z;
}

public sealed record RestrictedCameraVolume(
    GameplayVector3 Minimum,
    GameplayVector3 Maximum);

public sealed record EstablishingCameraPreset(
    string Id,
    IReadOnlyList<CameraKeyframe> Keyframes);

public sealed record MapCameraProfile
{
    public required string MapName { get; init; }
    public required IReadOnlyList<SafeCameraVolume> SafeVolumes { get; init; }
    public required IReadOnlyList<EstablishingCameraPreset> EstablishingShots { get; init; }
    public required IReadOnlyList<RestrictedCameraVolume> RestrictedVolumes { get; init; }
    public bool ManuallyVerified { get; init; }
}

public sealed record HlaeCameraCapabilities
{
    public required bool Available { get; init; }
    public string? Version { get; init; }
    public bool SupportsCampath { get; init; }
    public bool SupportsInput { get; init; }
    public bool SupportsFov { get; init; }
    public bool SupportsHighFpsCapture { get; init; }
    public bool ManualSpikeVerified { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
}

public sealed record CameraPlanningContext
{
    public required string MapName { get; init; }
    public MapCameraProfile? Profile { get; init; }
    public required HlaeCameraCapabilities Capabilities { get; init; }
    public double CameraDistance { get; init; } = 96;
    public double CameraHeight { get; init; } = 28;
    public double MinimumFov { get; init; } = 70;
    public double MaximumFov { get; init; } = 100;
}

public sealed class CinematicCaptureOptions
{
    public int StandardCaptureFps { get; init; } = 60;
    public int SlowMotionCaptureFps { get; init; } = 120;
    public int HeroShotCaptureFps { get; init; } = 240;
    public int OutputFps { get; init; } = 60;
    public int MaximumHighFpsShotsPerMovie { get; init; } = 3;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CinematicSequenceRole
{
    Intro,
    CalmBroll,
    BuildUp,
    PreKill,
    Highlight,
    PeakHighlight,
    Breakdown,
    Resolution,
    Outro
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MotivatedEffectReason
{
    MusicPeak,
    CameraTransition,
    TimeRamp,
    FinalKill,
    SectionChange,
    BassImpact
}

public sealed record MotivatedEffectDirective(
    string EffectType,
    MotivatedEffectReason Reason,
    double StartSeconds,
    double EndSeconds,
    double Intensity);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EffectRarityTier
{
    Common,
    Occasional,
    Rare
}

public sealed record EffectRarityEntry(
    string SegmentId,
    string EffectType,
    EffectRarityTier Tier,
    double DurationMilliseconds,
    bool Accepted,
    string Decision);

public sealed record EffectRarityReport(
    string SchemaVersion,
    int RareEffectCount,
    int LensWarpCount,
    IReadOnlyList<EffectRarityEntry> Entries,
    IReadOnlyList<string> Violations);

public sealed record CinematicSequenceSegment
{
    public required string Id { get; init; }
    public required CinematicSequenceRole Role { get; init; }
    public required double OutputStartSeconds { get; init; }
    public required double OutputEndSeconds { get; init; }
    public required string MusicSectionId { get; init; }
    public string? HighlightId { get; init; }
    public string? BrollCandidateId { get; init; }
    public required CameraShotPlan Camera { get; init; }
    public required TimeWarpPlan TimeWarp { get; init; }
    public required IReadOnlyList<MotivatedEffectDirective> Effects { get; init; }
}

public sealed record HighlightPeakMatch
{
    public required string HighlightId { get; init; }
    public required MusicalPeak Peak { get; init; }
    public required double HighlightImportance { get; init; }
    public required double PlannedPeakSeconds { get; init; }
    public required double PlannedKillSeconds { get; init; }
    public required double AlignmentErrorMilliseconds { get; init; }
    public required double Score { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
}

public sealed record HighlightPeakMatchPlan
{
    public required IReadOnlyList<HighlightPeakMatch> Matches { get; init; }
    public required IReadOnlyList<string> UnmatchedHighlightIds { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
}

public sealed class HighlightPeakMatchingOptions
{
    public double MinimumPeakStrength { get; init; } = 0.45;
    public double MinimumPeakConfidence { get; init; } = 0.40;
    public double MinimumPeakGapSeconds { get; init; } = 0.35;
    public bool AllowBuildUpEndingFallback { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CameraPreviewStatus
{
    NotAttempted,
    Rendering,
    Passed,
    Failed,
    PovFallback
}

public sealed record CameraPreviewMetrics(
    double DurationSeconds,
    double AverageBrightness,
    double BlackFrameRatio,
    double FrameVariance,
    double MotionScore,
    double JumpScore,
    double StaticRatio,
    bool HasVideo)
{
    public double? SubjectVisibleRatio { get; init; }
    public double? SubjectCenterDistance { get; init; }
    public double? HeadRoom { get; init; }
    public double? LeadRoom { get; init; }
    public double? SubjectScale { get; init; }
    public double? SubjectClippingRatio { get; init; }
    public double? SubjectLossDurationSeconds { get; init; }
    public double? GroupCoverageRatio { get; init; }
    public int WallIntersectionCount { get; init; }
    public bool CameraInsideGeometry { get; init; }
    public double? MaximumAngularVelocity { get; init; }
    public double? MaximumFovVelocity { get; init; }
    public double? RepeatedCompositionScore { get; init; }
    public double? ExcessiveMotionRatio { get; init; }
    public int CameraTeleportCount { get; init; }
    public double? ModelClippingRatio { get; init; }
    public bool DemoPlaybackStripDetected { get; init; }
    public bool UnexpectedHandsOnlyPresentation { get; init; }
}

public sealed record CameraPreviewResult
{
    public required string CameraShotId { get; init; }
    public required CameraPreviewStatus Status { get; init; }
    public string? PreviewPath { get; init; }
    public CameraPreviewMetrics? Metrics { get; init; }
    public required CameraShotPlan EffectiveShot { get; init; }
    public required int Attempt { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
}

public sealed record SoundDesignSection(
    string MusicSectionId,
    double GameplayGainDb,
    double MusicGainDb,
    bool EmphasizeFootsteps,
    bool DuckOnKill);

public sealed record SoundDesignPlan(
    IReadOnlyList<SoundDesignSection> Sections,
    bool PreservePostKillTail,
    IReadOnlyList<string> Warnings);

public sealed record ColorNarrativeSection(
    string MusicSectionId,
    double ContrastMultiplier,
    double ExposureOffset);

public sealed record ColorNarrativePlan(
    ColorGradePreset BaseGrade,
    IReadOnlyList<ColorNarrativeSection> Sections,
    IReadOnlyList<string> Warnings);

public sealed record CinematicMoviePlan
{
    public required string SchemaVersion { get; init; }
    public required string PlannerVersion { get; init; }
    public required string GenerationId { get; init; }
    public required MusicExcerptPlan MusicExcerpt { get; init; }
    public required double TargetDurationSeconds { get; init; }
    public required IReadOnlyList<CinematicSequenceSegment> Segments { get; init; }
    public required IReadOnlyList<HighlightPeakMatch> HighlightMatches { get; init; }
    public required SoundDesignPlan SoundDesign { get; init; }
    public required ColorNarrativePlan Color { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public EffectRarityReport? EffectRarity { get; init; }
    public ShotDiversityReport? CameraDiversity { get; init; }
}

public sealed record CinematicAlignmentReport
{
    public required IReadOnlyList<HighlightPeakMatch> HighlightMatches { get; init; }
    public required double MaximumAlignmentErrorMilliseconds { get; init; }
    public required double AverageAlignmentErrorMilliseconds { get; init; }
    public required int KillsOutsideHighEnergySections { get; init; }
    public required bool VerifiedFromRenderedMedia { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
}

public sealed class CinematicTimeWarpOptions
{
    public double MinimumBaseSpeed { get; init; } = 0.88;
    public double MaximumBaseSpeed { get; init; } = 1.12;
    public double MinimumLocalSpeed { get; init; } = 0.65;
    public double MaximumLocalSpeed { get; init; } = 1.30;
    public double MaximumRampDurationSeconds { get; init; } = 1.5;
    public double MaximumPostKillAcceleration { get; init; } = 1.05;
}

public sealed class CinematicEffectPolicy
{
    public int MaximumVisibleFilterEffectsPerHighlight { get; init; } = 1;
    public int MaximumFlashEffectsPerMovie { get; init; } = 2;
    public int MaximumRgbSplitEffectsPerMovie { get; init; } = 1;
    public int MaximumLensWarpEffectsPerMovie { get; init; } = 2;
    public bool PreferCameraMotionOverFilterEffects { get; init; } = true;
}

public sealed record CinematicDirectorOptions
{
    public required string GenerationId { get; init; }
    public required string MapName { get; init; }
    public required MovieDurationOptions Duration { get; init; }
    public required CameraPlanningContext Camera { get; init; }
    public CinematicTimeWarpOptions TimeWarp { get; init; } = new();
    public CinematicEffectPolicy Effects { get; init; } = new();
    public CinematicCaptureOptions Capture { get; init; } = new();
    public bool CompactTimelineWhenMaterialIsInsufficient { get; init; }
    public ColorGradePreset ColorGrade { get; init; } = ColorGradePreset.CinematicCool;
}
