using System.Text.Json.Serialization;

namespace Cs2Highlight.RenderAgent.Application;

public sealed record RenderJob(
    string JobId,
    string DemoPath,
    PlayerSelector Player,
    RenderSegment Segment,
    VideoSettings Video,
    string OutputDirectory,
    int TimeoutSeconds = 600)
{
    public CaptureUiProfile CaptureUi { get; init; } = CaptureUiProfile.Gameplay;
    public CapturePresentationMode? PresentationMode { get; init; }
    public RenderCameraPlan Camera { get; init; } = RenderCameraPlan.PlayerPov;
    public bool ContainsFirstPersonWeaponFire { get; init; } =
        Segment.PrimaryKillTick.HasValue;

    [JsonIgnore]
    public CapturePresentationMode EffectivePresentationMode =>
        PresentationMode ?? CaptureUi switch
        {
            CaptureUiProfile.Gameplay => CapturePresentationMode.PovCombat,
            CaptureUiProfile.Minimal => CapturePresentationMode.EstablishingShot,
            CaptureUiProfile.Cinematic => CapturePresentationMode.CinematicBroll,
            _ => throw new ArgumentOutOfRangeException(nameof(CaptureUi))
        };
}

public sealed record RenderBatchManifest(IReadOnlyList<string> RenderJobPaths);
public sealed record RenderJobOutcome(RenderResult Result, int ExitCode);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CaptureUiProfile
{
    Gameplay,
    Minimal,
    Cinematic
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CapturePresentationMode
{
    CinematicBroll,
    PovCombat,
    ThirdPersonAction,
    EstablishingShot
}

public sealed record PresentationStateVerification(
    bool DemoTimelineHidden,
    bool DemoControlsHidden,
    bool SpectatorUiHidden,
    bool DebugUiHidden,
    bool MouseCursorHidden,
    bool WeaponStateValid)
{
    public bool IsValid =>
        DemoTimelineHidden &&
        DemoControlsHidden &&
        SpectatorUiHidden &&
        DebugUiHidden &&
        MouseCursorHidden &&
        WeaponStateValid;
}

public sealed record PresentationStateReport(
    CapturePresentationMode Mode,
    PresentationStateVerification State,
    bool CommandStateVerified,
    bool PixelStateVerified,
    IReadOnlyList<string> Issues);

public sealed record PlayerSelector(string? SteamId, string? Name);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RenderCameraMode
{
    PlayerPov,
    Static,
    Campath
}

public sealed record RenderVector3(double X, double Y, double Z)
{
    [JsonIgnore]
    public bool IsFinite =>
        double.IsFinite(X) &&
        double.IsFinite(Y) &&
        double.IsFinite(Z);
}

public sealed record RenderCameraKeyframe(
    long Tick,
    RenderVector3 Position,
    RenderVector3 Rotation,
    double Fov);

public sealed record RenderCameraBounds(
    RenderVector3 Minimum,
    RenderVector3 Maximum)
{
    public bool Contains(RenderVector3 point) =>
        point.X >= Minimum.X && point.X <= Maximum.X &&
        point.Y >= Minimum.Y && point.Y <= Maximum.Y &&
        point.Z >= Minimum.Z && point.Z <= Maximum.Z;
}

public sealed record RenderCameraPlan
{
    public static RenderCameraPlan PlayerPov { get; } = new();

    public RenderCameraMode Mode { get; init; } = RenderCameraMode.PlayerPov;
    public string MapName { get; init; } = string.Empty;
    public IReadOnlyList<RenderCameraKeyframe> Keyframes { get; init; } = [];
    public RenderCameraBounds? SafeVolume { get; init; }
    public bool ManualSpikeVerified { get; init; }
    public bool CalibrationSpike { get; init; }
    public string VerificationId { get; init; } = string.Empty;
    public string HlaeVersionPrefix { get; init; } = string.Empty;
}

public sealed record RenderSegment(long StartTick, long EndTick)
{
    public int? TickRate { get; init; }
    public long? RoundStartTick { get; init; }
    public long? PrimaryKillTick { get; init; }
    public long? LastKillTick { get; init; }
    public long? SafeEndTick { get; init; }
}
public sealed record VideoSettings(int Width, int Height, int Fps, double Fov);

public sealed class RenderWarmupOptions
{
    public double WarmupGameSeconds { get; set; } = 3;
    public double MinimumWallClockStabilizationSeconds { get; set; } = 1;
    public double MaximumGameplayReadyWaitSeconds { get; set; } = 15;
    public bool ReapplyCaptureProfileAfterWarmup { get; set; } = true;
}

public sealed class RenderEnvironmentOptions
{
    public string HlaeExecutablePath { get; set; } = string.Empty;
    public string Cs2ExecutablePath { get; set; } = string.Empty;
    public string SteamExecutablePath { get; set; } = string.Empty;
    public string? FfmpegExecutablePath { get; set; }
    public string? FfprobeExecutablePath { get; set; }
    public string DemoRepairExecutablePath { get; set; } = string.Empty;
    public string WorkingRoot { get; set; } = string.Empty;
    public string HlaeArguments { get; set; } = string.Empty;
    public bool AutomationVerified { get; set; }
    public int ProcessStartupTimeoutSeconds { get; set; } = 90;
    public int DemoLoadTimeoutSeconds { get; set; } = 120;
    public double DemoInitializationStabilizationSeconds { get; set; } = 2;
    public int ProcessShutdownTimeoutSeconds { get; set; } = 15;
    public int NetConPort { get; set; } = 32123;
    public int OutputStableSeconds { get; set; } = 3;
    public long MinimumOutputBytes { get; set; } = 1024;
    public bool KillProcessTreeOnFailure { get; set; } = true;
    public RenderWarmupOptions Warmup { get; set; } = new();
    public bool EnableClipStartQualityGate { get; set; } = true;
    public double ClipStartSampleSeconds { get; set; } = 2;
    public double ClipStartBlackDurationSeconds { get; set; } = 0.75;
    public double ClipStartFreezeDurationSeconds { get; set; } = 1;
    public bool EnableDemoUiDetection { get; set; } = true;
    public double DemoUiDetectionSampleSeconds { get; set; } = 12;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RenderState
{
    Created,
    Validating,
    EnvironmentChecking,
    PreparingWorkspace,
    RepairingDemo,
    GeneratingScripts,
    StartingHlae,
    WaitingForCs2,
    LoadingDemo,
    Seeking,
    SeekingToWarmup,
    WarmingUp,
    WaitingForGameplayReady,
    SelectingPlayer,
    ApplyingCaptureProfile,
    ApplyingCameraPlan,
    VerifyingCaptureProfile,
    VerifyingCameraPlan,
    StabilizingCaptureProfile,
    AdvancingToStartTick,
    Recording,
    RecordingSafeTail,
    StoppingRecording,
    VerifyingOutput,
    Completed,
    Failed,
    Cancelled
}

public sealed record RenderError(
    string Code,
    string Message,
    RenderState Stage,
    bool Retryable,
    string? Exception = null);

public sealed record ProcessIdentifiers(int? HlaePid = null, int? Cs2Pid = null);

public sealed record RenderResult(
    string JobId,
    bool Success,
    RenderState FinalState,
    string? OutputFile,
    long? OutputSizeBytes,
    long DurationMilliseconds,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    ProcessIdentifiers Processes,
    IReadOnlyList<string> Warnings,
    RenderError? Error);

public sealed record ValidationReport(IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

public sealed record EnvironmentCheck(string Name, bool Success, string Message);

public sealed record EnvironmentReport(IReadOnlyList<EnvironmentCheck> Checks)
{
    public bool Success => Checks.All(check => check.Success);
}

public sealed record RenderWorkspace(
    string Root,
    string Input,
    string Config,
    string Raw,
    string Output,
    string Logs,
    string State,
    string PreparedDemoPath);

public sealed record DemoCompatibilityResult(string DemoPath, bool Repaired, string Message);

public sealed record GeneratedRenderScript(string Path, int Width, int Height, IReadOnlyList<string> Warnings);

public sealed record ProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    string StandardOutputPath,
    string StandardErrorPath,
    TimeSpan Timeout,
    IReadOnlyDictionary<string, string?>? Environment = null,
    string? TrackedProcessName = null,
    TimeSpan? TrackedProcessStartupTimeout = null);

public sealed record ProcessExecutionResult(
    int ProcessId,
    int ExitCode,
    bool TimedOut,
    TimeSpan Duration,
    int? TrackedProcessId = null);

public enum DemoLoadMode
{
    Start,
    ReuseCurrent
}

public static class ExitCodes
{
    public const int Success = 0;
    public const int InvalidArguments = 2;
    public const int InvalidRenderJob = 10;
    public const int EnvironmentValidationFailed = 20;
    public const int HlaeLaunchFailed = 30;
    public const int Cs2LaunchTimeout = 31;
    public const int Cs2ExitedUnexpectedly = 32;
    public const int DemoControlFailed = 40;
    public const int RecordingFailed = 50;
    public const int OutputVerificationFailed = 60;
    public const int Cancelled = 70;
    public const int Unexpected = 99;

    public static int FromError(RenderError error) => error.Code switch
    {
        "INVALID_RENDER_JOB" => InvalidRenderJob,
        "ENVIRONMENT_INVALID" or "RENDERER_BUSY" or "HLAE_AUTOMATION_UNCONFIRMED" => EnvironmentValidationFailed,
        "HLAE_LAUNCH_FAILED" => HlaeLaunchFailed,
        "CS2_START_TIMEOUT" => Cs2LaunchTimeout,
        "CS2_EXITED" => Cs2ExitedUnexpectedly,
        "DEMO_CONTROL_FAILED" or "DEMO_COMPATIBILITY_REPAIR_FAILED" or
            "DEMO_NETWORK_VERSION_INCOMPATIBLE" => DemoControlFailed,
        "RECORDING_FAILED" => RecordingFailed,
        "OUTPUT_INVALID" => OutputVerificationFailed,
        "CANCELLED" => Cancelled,
        _ => Unexpected
    };
}
