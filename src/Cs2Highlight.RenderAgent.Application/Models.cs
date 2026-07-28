using System.Text.Json.Serialization;

namespace Cs2Highlight.RenderAgent.Application;

public sealed record RenderJob(
    string JobId,
    string DemoPath,
    PlayerSelector Player,
    RenderSegment Segment,
    VideoSettings Video,
    string OutputDirectory,
    int TimeoutSeconds = 600);

public sealed record PlayerSelector(string? SteamId, string? Name);
public sealed record RenderSegment(long StartTick, long EndTick);
public sealed record VideoSettings(int Width, int Height, int Fps, double Fov);

public sealed class RenderEnvironmentOptions
{
    public string HlaeExecutablePath { get; set; } = string.Empty;
    public string Cs2ExecutablePath { get; set; } = string.Empty;
    public string SteamExecutablePath { get; set; } = string.Empty;
    public string? FfmpegExecutablePath { get; set; }
    public string WorkingRoot { get; set; } = string.Empty;
    public string HlaeArguments { get; set; } = string.Empty;
    public bool AutomationVerified { get; set; }
    public int ProcessStartupTimeoutSeconds { get; set; } = 90;
    public int OutputStableSeconds { get; set; } = 3;
    public long MinimumOutputBytes { get; set; } = 1024;
    public bool KillProcessTreeOnFailure { get; set; } = true;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RenderState
{
    Created,
    Validating,
    EnvironmentChecking,
    PreparingWorkspace,
    GeneratingScripts,
    StartingHlae,
    WaitingForCs2,
    LoadingDemo,
    Seeking,
    SelectingPlayer,
    Recording,
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

public sealed record GeneratedRenderScript(string Path, int Width, int Height, IReadOnlyList<string> Warnings);

public sealed record ProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    string StandardOutputPath,
    string StandardErrorPath,
    TimeSpan Timeout,
    IReadOnlyDictionary<string, string?>? Environment = null);

public sealed record ProcessExecutionResult(
    int ProcessId,
    int ExitCode,
    bool TimedOut,
    TimeSpan Duration);

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
        "DEMO_CONTROL_FAILED" => DemoControlFailed,
        "RECORDING_FAILED" => RecordingFailed,
        "OUTPUT_INVALID" => OutputVerificationFailed,
        "CANCELLED" => Cancelled,
        _ => Unexpected
    };
}
