using System.Text.Json.Serialization;
using Cs2Highlight.Analysis;

namespace Cs2Highlight.Music;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MusicalAnchorType { Beat, StrongBeat, Downbeat, Onset, SectionBoundary, Drop }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MusicSyncIntensity { Soft, Expressive, Aggressive }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MovieStyle { Clean, Dynamic, Cinematic, Aggressive, CinematicDirector }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ColorGradePreset
{
    None, Natural, Competitive, CinematicCool, CinematicWarm, HighContrast, Neon
}
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MusicDurationPolicy { TrimMusicToVideo, ExtendVideoWithinLimits, FitHighlightsToMusicSection }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MultikillAnchorPolicy { FirstKill, LastKill, HighestBeautyKill }

public sealed record MusicAnalysis(
    string SchemaVersion,
    MusicAnalyzerInfo Analyzer,
    MusicAudioInfo Audio,
    IReadOnlyList<MusicBeat> Beats,
    IReadOnlyList<MusicBeat> Downbeats,
    IReadOnlyList<MusicOnset> Onsets,
    IReadOnlyList<MusicSection> Sections,
    IReadOnlyList<MusicDropCandidate> DropCandidates,
    IReadOnlyList<string> Warnings)
{
    public double FrameHopSeconds { get; init; } = 0.04;
    public IReadOnlyList<MusicFrame> Frames { get; init; } = [];
}

public sealed record MusicAnalyzerInfo(string Name, string Version, string Engine);
public sealed record MusicAudioInfo(
    string FileName,
    double DurationSeconds,
    int SampleRate,
    int Channels,
    double? TempoBpm,
    double? TempoConfidence,
    double? IntegratedLoudnessLufs);
public sealed record MusicBeat(int Index, double TimeSeconds, double Strength, double? Confidence);
public sealed record MusicOnset(int Index, double TimeSeconds, double Strength);
public sealed record MusicSection(
    int Index, double StartSeconds, double EndSeconds, string Label, double Energy)
{
    public string Id { get; init; } = $"section-{Index:D3}";
    public MusicSectionType Type { get; init; } = MusicSectionType.Unknown;
    public double RhythmicDensity { get; init; }
    public double BassEnergy { get; init; }
    public double SpectralBrightness { get; init; }
    public double DynamicContrast { get; init; }
    public double Confidence { get; init; }
    public IReadOnlyList<MusicalAnchor> Anchors { get; init; } = [];
    public IReadOnlyDictionary<string, double> ScoreBreakdown { get; init; } =
        new Dictionary<string, double>(StringComparer.Ordinal);
}
public sealed record MusicDropCandidate(
    int Index,
    double TimeSeconds,
    double Score,
    double EnergyChange,
    double OnsetStrength,
    double LowFrequencyImpact,
    double? Confidence);

public sealed record MusicalAnchor(
    string Id,
    MusicalAnchorType Type,
    double TimeSeconds,
    double Strength,
    double Confidence);

public sealed record HighlightImportance(
    double Total,
    IReadOnlyDictionary<string, double> Breakdown);

public sealed record SelectedHighlight(
    string Id,
    HighlightCandidate Highlight,
    SafeClipBounds Bounds,
    int SelectionOrder);

public sealed record TimeWarpSegment(
    double SourceStartSeconds,
    double SourceEndSeconds,
    double Speed);
public sealed record TimeWarpPlan(
    double BaseSpeedFactor,
    IReadOnlyList<TimeWarpSegment> Segments,
    bool UsesLocalRamp,
    IReadOnlyList<string> Warnings);

public sealed record MusicEditScoreBreakdown(
    double ImportanceAnchorScore,
    double CompatibilityBonus,
    double ChronologyBonus,
    double SpeedPenalty,
    double WeakAnchorPenalty,
    double Total);

public sealed record MusicEditSegment(
    int Index,
    string HighlightId,
    HighlightType HighlightType,
    double HighlightImportance,
    double SourceStartSeconds,
    double SourceEndSeconds,
    double PrimaryKillSourceTimeSeconds,
    MusicalAnchor? TargetMusicAnchor,
    double OutputStartSeconds,
    double PrimaryKillOutputTimeSeconds,
    TimeWarpPlan TimeWarp,
    string TransitionIn,
    string TransitionOut,
    MusicEditScoreBreakdown ScoreBreakdown,
    IReadOnlyList<string> Warnings);

public sealed record MusicEditPlan(
    string SchemaVersion,
    string GenerationId,
    string MusicFile,
    double MusicDurationSeconds,
    MovieStyle Style,
    MusicSyncIntensity SyncIntensity,
    IReadOnlyList<MusicEditSegment> Segments,
    IReadOnlyList<string> Warnings)
{
    public double MusicStartSeconds { get; init; }
}

public sealed class MusicEditOptions
{
    public MovieStyle Style { get; init; } = MovieStyle.Dynamic;
    public MusicSyncIntensity SyncIntensity { get; init; } = MusicSyncIntensity.Expressive;
    public MultikillAnchorPolicy MultikillAnchorPolicy { get; init; } = MultikillAnchorPolicy.LastKill;
    public int BeamWidth { get; init; } = 64;
    public int MaximumAnchorsPerStep { get; init; } = 24;
    public double SpeedAdjustmentPenaltyWeight { get; init; } = 4;
    public double WeakAnchorPenaltyWeight { get; init; } = 1;
    public double ChronologyBonus { get; init; } = 0.25;
}

public sealed class TimeWarpOptions
{
    public double SoftMinimumSpeed { get; init; } = 0.95;
    public double SoftMaximumSpeed { get; init; } = 1.05;
    public double ExpressiveMinimumBaseSpeed { get; init; } = 0.90;
    public double ExpressiveMaximumBaseSpeed { get; init; } = 1.10;
    public double ExpressiveMinimumRampSpeed { get; init; } = 0.75;
    public double ExpressiveMaximumRampSpeed { get; init; } = 1.20;
    public double AggressiveMinimumRampSpeed { get; init; } = 0.70;
    public double AggressiveMaximumRampSpeed { get; init; } = 1.30;
    public double MaximumRampDurationSeconds { get; init; } = 1.2;
    public double MinimumConstantSegmentSeconds { get; init; } = 0.15;
}

public sealed class DropDetectionOptions
{
    public double OnsetStrengthWeight { get; init; } = 0.25;
    public double EnergyChangeWeight { get; init; } = 0.30;
    public double LowFrequencyWeight { get; init; } = 0.20;
    public double SectionBoundaryWeight { get; init; } = 0.15;
    public double DownbeatWeight { get; init; } = 0.10;
    public double MinimumDropScore { get; init; } = 0.65;
    public double MinimumDropGapSeconds { get; init; } = 4;
}

public sealed class AudioMixOptions
{
    public double MusicGainDb { get; init; } = -3;
    public double GameplayBaseGainDb { get; init; } = -16;
    public double GameplayKillAccentGainDb { get; init; } = -7;
    public double KillAccentAttackMilliseconds { get; init; } = 50;
    public double KillAccentHoldMilliseconds { get; init; } = 250;
    public double KillAccentReleaseMilliseconds { get; init; } = 400;
    public double MusicDuckOnKillDb { get; init; } = -3;
    public bool EnableLimiter { get; init; } = true;
    public double OutputTruePeakDb { get; init; } = -1;
}
