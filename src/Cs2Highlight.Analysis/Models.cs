using System.Text.Json.Serialization;

namespace Cs2Highlight.Analysis;

public sealed record DemoAnalysis(
    string SchemaVersion,
    ParserInfo Parser,
    DemoMetadata Demo,
    IReadOnlyList<DemoPlayer> Players,
    IReadOnlyList<DemoRound> Rounds,
    IReadOnlyList<KillEvent> Kills,
    IReadOnlyList<string> Warnings);

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
    string? VictimTeam);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HighlightType
{
    DoubleKill,
    TripleKill,
    QuadKill,
    Ace,
    HeadshotStreak
}

public sealed record ScoreBreakdown(
    double BaseKillScore,
    double HeadshotBonus,
    double TypeBonus,
    double FastSequenceBonus,
    double RoundWinBonus,
    double LastKillRoundEndBonus,
    double Total);

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
    IReadOnlyList<string> Tags);

public sealed class HighlightDetectionOptions
{
    public string? TargetPlayerId { get; init; }
    public double MaximumGapBetweenKillsSeconds { get; init; } = 6;
    public double MaximumSequenceDurationSeconds { get; init; } = 12;
    public double PreRollSeconds { get; init; } = 3;
    public double PostRollSeconds { get; init; } = 3;
    public int MinimumKills { get; init; } = 2;
    public int MinimumHeadshotsForStreak { get; init; } = 2;
    public bool ClampToRoundBounds { get; init; } = true;
    public HighlightScoringOptions Scoring { get; init; } = new();
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
    double PostRollSeconds);

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
