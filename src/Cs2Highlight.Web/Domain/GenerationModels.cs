using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Cs2Highlight.Web.Domain;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GenerationStatus
{
    Draft, Uploading, Uploaded, QueuedForAnalysis, Analyzing,
    AwaitingPlayerSelection, AwaitingPayment, PaymentProcessing, Paid,
    QueuedForGeneration, SelectingHighlights, RenderingClips, ComposingVideo,
    VerifyingOutput, Completed, CompletedWithWarnings, Cancelling, Cancelled,
    Failed, Expired
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PaymentStatus { NotCreated, Pending, Succeeded, Failed, Cancelled, Refunded }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DemoAnalysisStatus { Pending, Analyzing, Succeeded, Skipped, Failed }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OutputOrder { Chronological, ScoreDescending }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TransitionType { Cut, Fade }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DemoFailurePolicy { FailGeneration, SkipInvalidDemo }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ArtifactType
{
    UploadedDemo, DemoAnalysis, Highlights, GenerationPlan, BatchPlan, BatchState,
    BatchReport, IntermediateClip, CompilationResult, GenerationReport, FinalVideo, Log
}

public sealed class Generation
{
    public long Id { get; set; }
    [MaxLength(64)] public string PublicId { get; set; } = string.Empty;
    public GenerationStatus Status { get; set; } = GenerationStatus.Draft;
    [MaxLength(17)] public string? SelectedSteamId { get; set; }
    [MaxLength(128)] public string? SelectedPlayerName { get; set; }
    public int MaximumHighlights { get; set; } = 5;
    public double MinimumScore { get; set; }
    public OutputOrder OutputOrder { get; set; } = OutputOrder.Chronological;
    [MaxLength(8)] public string AspectRatio { get; set; } = "16:9";
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public int Fps { get; set; } = 60;
    public TransitionType TransitionType { get; set; } = TransitionType.Fade;
    public int TransitionDurationMilliseconds { get; set; } = 300;
    public long PriceAmountMinor { get; set; } = 100;
    [MaxLength(3)] public string PriceCurrency { get; set; } = "USD";
    public PaymentStatus PaymentStatus { get; set; }
    [MaxLength(128)] public string? PaymentId { get; set; }
    [MaxLength(128)] public string? PaymentIdempotencyKey { get; set; }
    public int ProgressPercent { get; set; }
    [MaxLength(128)] public string CurrentStage { get; set; } = "Draft";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    public DateTimeOffset? GenerationStartedAt { get; set; }
    public DateTimeOffset? GenerationCompletedAt { get; set; }
    public long? FinalVideoArtifactId { get; set; }
    [MaxLength(64)] public string? ErrorCode { get; set; }
    [MaxLength(1024)] public string? ErrorMessage { get; set; }
    [ConcurrencyCheck] public int Version { get; set; }
    public List<GenerationDemo> Demos { get; set; } = [];
    public List<GenerationPlayer> Players { get; set; } = [];
    public List<GenerationHighlight> Highlights { get; set; } = [];
    public List<GenerationArtifact> Artifacts { get; set; } = [];
    public List<Payment> Payments { get; set; } = [];
    public List<GenerationEvent> Events { get; set; } = [];
}

public sealed class GenerationDemo
{
    public long Id { get; set; }
    public long GenerationId { get; set; }
    public Generation Generation { get; set; } = null!;
    [MaxLength(260)] public string OriginalFileName { get; set; } = string.Empty;
    [MaxLength(1024)] public string StoredPath { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    [MaxLength(64)] public string Sha256 { get; set; } = string.Empty;
    public int UploadOrder { get; set; }
    public DemoAnalysisStatus AnalysisStatus { get; set; }
    [MaxLength(128)] public string? MapName { get; set; }
    public int? TickRate { get; set; }
    public long? DurationTicks { get; set; }
    [MaxLength(64)] public string? ErrorCode { get; set; }
    [MaxLength(1024)] public string? ErrorMessage { get; set; }
}

public sealed class GenerationPlayer
{
    public long Id { get; set; }
    public long GenerationId { get; set; }
    public Generation Generation { get; set; } = null!;
    [MaxLength(17)] public string SteamId { get; set; } = string.Empty;
    [MaxLength(128)] public string DisplayName { get; set; } = string.Empty;
    public int DemoCount { get; set; }
    public int TotalKills { get; set; }
    public int CandidateCount { get; set; }
    public bool IsSelected { get; set; }
}

public sealed class GenerationHighlight
{
    public long Id { get; set; }
    public long GenerationId { get; set; }
    public Generation Generation { get; set; } = null!;
    public long GenerationDemoId { get; set; }
    [MaxLength(256)] public string HighlightId { get; set; } = string.Empty;
    [MaxLength(17)] public string SteamId { get; set; } = string.Empty;
    [MaxLength(32)] public string Type { get; set; } = string.Empty;
    public double Score { get; set; }
    public int RoundNumber { get; set; }
    public long StartTick { get; set; }
    public long EndTick { get; set; }
    public long FirstKillTick { get; set; }
    public long LastKillTick { get; set; }
    public int KillCount { get; set; }
    public int HeadshotCount { get; set; }
    public bool SelectedForCompilation { get; set; }
    public int? CompilationOrder { get; set; }
}

public sealed class GenerationArtifact
{
    public long Id { get; set; }
    public long GenerationId { get; set; }
    public Generation Generation { get; set; } = null!;
    public ArtifactType Type { get; set; }
    [MaxLength(260)] public string FileName { get; set; } = string.Empty;
    [MaxLength(1024)] public string StoredPath { get; set; } = string.Empty;
    [MaxLength(128)] public string ContentType { get; set; } = "application/octet-stream";
    public long FileSizeBytes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class Payment
{
    public long Id { get; set; }
    public long GenerationId { get; set; }
    public Generation Generation { get; set; } = null!;
    [MaxLength(32)] public string Provider { get; set; } = "Test";
    [MaxLength(128)] public string ProviderPaymentId { get; set; } = string.Empty;
    [MaxLength(128)] public string IdempotencyKey { get; set; } = string.Empty;
    public PaymentStatus Status { get; set; }
    public long AmountMinor { get; set; }
    [MaxLength(3)] public string Currency { get; set; } = "USD";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? SucceededAt { get; set; }
    [MaxLength(64)] public string? FailureCode { get; set; }
}

public sealed class GenerationEvent
{
    public long Id { get; set; }
    public long GenerationId { get; set; }
    public Generation Generation { get; set; } = null!;
    [MaxLength(16)] public string Level { get; set; } = "Information";
    [MaxLength(128)] public string Stage { get; set; } = string.Empty;
    [MaxLength(1024)] public string Message { get; set; } = string.Empty;
    public int ProgressPercent { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
