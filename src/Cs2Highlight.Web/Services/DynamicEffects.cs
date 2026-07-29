using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Cs2Highlight.Music;
using Cs2Highlight.Web.Domain;

namespace Cs2Highlight.Web.Services;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VideoEffectType
{
    None,
    SmoothZoom,
    PunchZoom,
    CrashZoom,
    ZoomPulse,
    OffsetZoom,
    MicroShake,
    RecoilShake,
    DirectionalMotionBlur,
    ZoomBlur,
    FrameEcho,
    RgbSplit,
    HitStop,
    LensWarpPulse,
    RollBurst,
    FlashAccent,
    VignettePulse,
    SpeedRamp,
    HardCut,
    FadeTransition,
    FlashCut,
    WhipPan,
    WhipZoom
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VideoEffectCategory
{
    Zoom,
    Motion,
    Blur,
    Distortion,
    Temporal,
    Color,
    Time,
    Transition,
    Accent
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EffectRole { Primary, Accent, Transition, Audio }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ZoomVariant
{
    Center,
    LeftBias,
    RightBias,
    UpperBias,
    LowerBias,
    Pulse,
    DoublePulse
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EffectRenderCost { Low, Medium, High }

public sealed record EffectCue
{
    public required string Id { get; init; }
    public required VideoEffectType Type { get; init; }
    public required VideoEffectCategory Category { get; init; }
    public required EffectRole Role { get; init; }
    public required double StartSeconds { get; init; }
    public required double EndSeconds { get; init; }
    public required double Intensity { get; init; }
    public required int Priority { get; init; }
    public required int Seed { get; init; }
    public required IReadOnlyDictionary<string, double> Parameters { get; init; }
    public string? SourceKillEventId { get; init; }
    public string? SourceMusicalAnchorId { get; init; }
    public string? Reason { get; init; }
    public EffectRenderCost RenderCost { get; init; } = EffectRenderCost.Low;
}

public sealed record RejectedEffectCue(
    VideoEffectType Type,
    string Reason,
    string? SourceKillEventId);

public sealed record DynamicEffectPlan
{
    public required string SchemaVersion { get; init; }
    public required string PlannerVersion { get; init; }
    public required string GenerationId { get; init; }
    public required string HighlightId { get; init; }
    public required string ClipId { get; init; }
    public required MovieStyle Style { get; init; }
    public required EffectIntensity Intensity { get; init; }
    public required int DeterministicSeed { get; init; }
    public required IReadOnlyList<EffectCue> Effects { get; init; }
    public required IReadOnlyList<RejectedEffectCue> RejectedEffects { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public IReadOnlyList<EffectSelectionScore> Scores { get; init; } = [];
}

public sealed record EffectSelectionScore(
    string SourceKillEventId,
    VideoEffectType Effect,
    double Score,
    IReadOnlyDictionary<string, double> Breakdown);

public sealed record PlannedEffect(EffectCue Cue);

public sealed record EffectCompatibilityResult(
    bool Allowed,
    string? RejectionReason = null,
    double IntensityMultiplier = 1);

public interface IEffectSeedProvider
{
    int CreateSeed(
        string generationId,
        string highlightId,
        int killEventIndex,
        string plannerVersion);
}

public sealed class Sha256EffectSeedProvider : IEffectSeedProvider
{
    public int CreateSeed(
        string generationId,
        string highlightId,
        int killEventIndex,
        string plannerVersion)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, generationId);
        Append(hash, highlightId);
        Append(hash, killEventIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(hash, plannerVersion);
        Span<byte> digest = stackalloc byte[32];
        if (!hash.TryGetHashAndReset(digest, out int written) || written < sizeof(int))
            throw new CryptographicException("Could not create deterministic effect seed.");
        return BinaryPrimitives.ReadInt32LittleEndian(digest) & int.MaxValue;
    }

    private static void Append(IncrementalHash hash, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }
}

public sealed class DeterministicEffectRandom(int seed)
{
    private uint state = unchecked((uint)seed) + 0x9E3779B9u;

    public double NextUnit()
    {
        uint value = NextUInt();
        return value / ((double)uint.MaxValue + 1);
    }

    public int Next(int minimumInclusive, int maximumExclusive)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            maximumExclusive,
            minimumInclusive);
        return minimumInclusive +
            (int)Math.Floor(NextUnit() * (maximumExclusive - minimumInclusive));
    }

    public double Next(double minimum, double maximum) =>
        minimum + NextUnit() * (maximum - minimum);

    private uint NextUInt()
    {
        uint value = state;
        value ^= value << 13;
        value ^= value >> 17;
        value ^= value << 5;
        state = value;
        return value;
    }
}

public interface IEffectCompatibilityPolicy
{
    EffectCompatibilityResult Evaluate(
        PlannedEffect candidate,
        IReadOnlyList<PlannedEffect> accepted);
}

public sealed class EffectCompatibilityPolicy : IEffectCompatibilityPolicy
{
    public EffectCompatibilityResult Evaluate(
        PlannedEffect candidate,
        IReadOnlyList<PlannedEffect> accepted)
    {
        EffectCue cue = candidate.Cue;
        IEnumerable<EffectCue> sameKill = accepted
            .Select(value => value.Cue)
            .Where(value =>
                cue.SourceKillEventId is not null &&
                value.SourceKillEventId == cue.SourceKillEventId);
        if (cue.Role == EffectRole.Primary &&
            sameKill.Any(value => value.Role == EffectRole.Primary))
            return Reject("PRIMARY_EFFECT_BUDGET_EXCEEDED");
        if (cue.Role == EffectRole.Accent &&
            sameKill.Any(value => value.Role == EffectRole.Accent))
            return Reject("ACCENT_EFFECT_BUDGET_EXCEEDED");
        if (cue.Role == EffectRole.Transition &&
            accepted.Any(value => value.Cue.Role == EffectRole.Transition))
            return Reject("CONFLICT_WITH_TRANSITION");
        foreach (EffectCue existing in accepted.Select(value => value.Cue)
                     .Where(value => Overlaps(value, cue)))
        {
            if (Pair(existing.Type, cue.Type, VideoEffectType.CrashZoom, VideoEffectType.PunchZoom))
                return Reject("CONFLICTING_PRIMARY_ZOOMS");
            if (Pair(existing.Type, cue.Type, VideoEffectType.HitStop, VideoEffectType.DirectionalMotionBlur) &&
                Math.Max(existing.Intensity, cue.Intensity) > 0.55)
                return Reject("CONFLICT_WITH_HIT_STOP");
            if (Pair(existing.Type, cue.Type, VideoEffectType.HitStop, VideoEffectType.FrameEcho) &&
                Math.Max(existing.Intensity, cue.Intensity) > 0.4)
                return Reject("CONFLICT_WITH_HIT_STOP");
            if (Pair(existing.Type, cue.Type, VideoEffectType.RollBurst, VideoEffectType.WhipZoom))
                return Reject("CONFLICT_WITH_TRANSITION");
        }
        bool rgbFlash = accepted.Select(value => value.Cue)
            .Where(value => Overlaps(value, cue))
            .Any(value => Pair(
                value.Type,
                cue.Type,
                VideoEffectType.RgbSplit,
                VideoEffectType.FlashAccent));
        return new EffectCompatibilityResult(true, null, rgbFlash ? 0.65 : 1);
    }

    private static EffectCompatibilityResult Reject(string reason) =>
        new(false, reason);

    private static bool Pair(
        VideoEffectType left,
        VideoEffectType right,
        VideoEffectType first,
        VideoEffectType second) =>
        left == first && right == second || left == second && right == first;

    private static bool Overlaps(EffectCue left, EffectCue right) =>
        left.StartSeconds < right.EndSeconds && right.StartSeconds < left.EndSeconds;
}

public sealed class EffectBudgetOptions
{
    public int MaximumPrimaryEffectsPerKill { get; init; } = 1;
    public int MaximumAccentEffectsPerKill { get; init; } = 1;
    public int MaximumTransitionsPerClip { get; init; } = 1;
    public int MaximumStrongEffectsPerClip { get; init; } = 3;
    public int MaximumTotalEffectsPerClip { get; init; } = 12;
    public double MinimumStrongEffectGapSeconds { get; init; } = 0.65;
    public double MaximumAffectedClipRatio { get; init; } = 0.30;
}

public sealed class EffectPerformanceOptions
{
    public int MaximumHighCostEffectsPerClip { get; init; } = 2;
    public bool AllowHighCostEffects { get; init; } = true;
    public bool EnablePerformanceFallback { get; init; } = true;
}

public interface IEffectBudgetPolicy
{
    string? Validate(
        EffectCue candidate,
        IReadOnlyList<EffectCue> accepted,
        double clipDurationSeconds);
}

public sealed class EffectBudgetPolicy(
    EffectBudgetOptions? options = null,
    EffectPerformanceOptions? performance = null) : IEffectBudgetPolicy
{
    private readonly EffectBudgetOptions options = options ?? new();
    private readonly EffectPerformanceOptions performance = performance ?? new();

    public string? Validate(
        EffectCue candidate,
        IReadOnlyList<EffectCue> accepted,
        double clipDurationSeconds)
    {
        if (candidate.StartSeconds < 0 ||
            candidate.EndSeconds <= candidate.StartSeconds ||
            candidate.EndSeconds > clipDurationSeconds + 0.000001)
            return "EFFECT_SAFE_BOUNDARY_VIOLATION";
        if (accepted.Count >= options.MaximumTotalEffectsPerClip)
            return "EFFECT_BUDGET_EXCEEDED";
        IEnumerable<EffectCue> sameKill = accepted.Where(value =>
            candidate.SourceKillEventId is not null &&
            value.SourceKillEventId == candidate.SourceKillEventId);
        if (candidate.Role == EffectRole.Primary &&
            sameKill.Count(value => value.Role == EffectRole.Primary) >=
            options.MaximumPrimaryEffectsPerKill)
            return "PRIMARY_EFFECT_BUDGET_EXCEEDED";
        if (candidate.Role == EffectRole.Accent &&
            sameKill.Count(value => value.Role == EffectRole.Accent) >=
            options.MaximumAccentEffectsPerKill)
            return "ACCENT_EFFECT_BUDGET_EXCEEDED";
        if (candidate.Role == EffectRole.Transition &&
            accepted.Count(value => value.Role == EffectRole.Transition) >=
            options.MaximumTransitionsPerClip)
            return "EFFECT_BUDGET_EXCEEDED";
        bool strong = candidate.Intensity >= 0.7;
        if (strong && accepted.Count(value => value.Intensity >= 0.7) >=
            options.MaximumStrongEffectsPerClip)
            return "EFFECT_BUDGET_EXCEEDED";
        if (strong && accepted.Any(value =>
                value.Intensity >= 0.7 &&
                Math.Abs(value.StartSeconds - candidate.StartSeconds) <
                options.MinimumStrongEffectGapSeconds))
            return "EFFECT_COOLDOWN_ACTIVE";
        if (candidate.RenderCost == EffectRenderCost.High &&
            (!performance.AllowHighCostEffects ||
             accepted.Count(value => value.RenderCost == EffectRenderCost.High) >=
             performance.MaximumHighCostEffectsPerClip))
            return "EFFECT_PERFORMANCE_LIMIT_EXCEEDED";
        double affected = UnionDuration(accepted
            .Where(IsNoticeable)
            .Append(candidate)
            .Where(IsNoticeable));
        if (clipDurationSeconds > 0 &&
            affected / clipDurationSeconds > options.MaximumAffectedClipRatio + 0.000001)
            return "EFFECT_BUDGET_EXCEEDED";
        return null;
    }

    private static bool IsNoticeable(EffectCue cue) =>
        cue.Intensity >= 0.35 && cue.Role != EffectRole.Transition;

    private static double UnionDuration(IEnumerable<EffectCue> cues)
    {
        (double Start, double End)[] intervals = cues
            .Select(value => (value.StartSeconds, value.EndSeconds))
            .OrderBy(value => value.StartSeconds)
            .ToArray();
        if (intervals.Length == 0)
            return 0;
        double result = 0;
        double start = intervals[0].Start;
        double end = intervals[0].End;
        foreach ((double nextStart, double nextEnd) in intervals.Skip(1))
        {
            if (nextStart <= end)
                end = Math.Max(end, nextEnd);
            else
            {
                result += end - start;
                start = nextStart;
                end = nextEnd;
            }
        }
        return result + end - start;
    }
}

public sealed class EffectVarietyOptions
{
    public int RecentEffectHistorySize { get; init; } = 5;
    public double SameEffectPenalty { get; init; } = 20;
    public double SameCategoryPenalty { get; init; } = 8;
    public int MaximumSamePrimaryEffectInRow { get; init; } = 2;
    public bool PreferVariantRotation { get; init; } = true;
}

public interface IEffectVarietyPolicy
{
    double Penalty(
        VideoEffectType candidate,
        VideoEffectCategory category,
        IReadOnlyList<EffectCue> history);
    bool ExceedsConsecutiveLimit(
        VideoEffectType candidate,
        IReadOnlyList<EffectCue> history);
}

public sealed class EffectVarietyPolicy(
    EffectVarietyOptions? options = null) : IEffectVarietyPolicy
{
    private readonly EffectVarietyOptions options = options ?? new();

    public double Penalty(
        VideoEffectType candidate,
        VideoEffectCategory category,
        IReadOnlyList<EffectCue> history)
    {
        EffectCue[] recent = history
            .Where(value => value.Role == EffectRole.Primary)
            .TakeLast(options.RecentEffectHistorySize)
            .ToArray();
        double sameEffect = recent.Count(value => value.Type == candidate) *
            options.SameEffectPenalty;
        double sameCategory = recent.Count(value => value.Category == category) *
            options.SameCategoryPenalty;
        return sameEffect + sameCategory;
    }

    public bool ExceedsConsecutiveLimit(
        VideoEffectType candidate,
        IReadOnlyList<EffectCue> history) =>
        history
            .Where(value => value.Role == EffectRole.Primary)
            .Reverse()
            .TakeWhile(value => value.Type == candidate)
            .Take(options.MaximumSamePrimaryEffectInRow)
            .Count() >= options.MaximumSamePrimaryEffectInRow;
}

public sealed record EffectTimeMapping(
    double SourceStartSeconds,
    double SourceEndSeconds,
    double ProcessedStartSeconds,
    double ProcessedEndSeconds);

public interface IEffectTimeMapper
{
    EffectTimeMapping Map(
        double sourceStartSeconds,
        double sourceEndSeconds,
        TimeWarpPlan timeWarpPlan);
}

public sealed class EffectTimeMapper : IEffectTimeMapper
{
    public EffectTimeMapping Map(
        double sourceStartSeconds,
        double sourceEndSeconds,
        TimeWarpPlan timeWarpPlan)
    {
        if (sourceStartSeconds < 0 || sourceEndSeconds <= sourceStartSeconds)
            throw new InvalidOperationException("EFFECT_TIME_MAPPING_FAILED");
        double start = TimeWarpMath.MapSourceTime(timeWarpPlan, sourceStartSeconds);
        double end = TimeWarpMath.MapSourceTime(timeWarpPlan, sourceEndSeconds);
        if (!double.IsFinite(start) || !double.IsFinite(end) || end <= start)
            throw new InvalidOperationException("EFFECT_TIME_MAPPING_FAILED");
        return new EffectTimeMapping(
            sourceStartSeconds,
            sourceEndSeconds,
            start,
            end);
    }
}

public sealed record EffectCapabilityRequirement(
    IReadOnlyList<string> RequiredFilters,
    IReadOnlyList<string> OptionalFilters);

public sealed record FfmpegCapabilities(
    string SchemaVersion,
    bool Available,
    string Executable,
    string? Version,
    IReadOnlySet<string> Filters,
    DateTimeOffset ScannedAt,
    IReadOnlyList<string> Warnings)
{
    public bool Supports(string filter) => Filters.Contains(filter);
}

public interface IFfmpegCapabilityScanner
{
    Task<FfmpegCapabilities> ScanAsync(CancellationToken cancellationToken);
    Task WriteAsync(
        FfmpegCapabilities capabilities,
        string path,
        CancellationToken cancellationToken);
}

public sealed partial class FfmpegCapabilityScanner(
    PipelineOptions options,
    TimeProvider timeProvider) : IFfmpegCapabilityScanner
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<FfmpegCapabilities> ScanAsync(CancellationToken cancellationToken)
    {
        string executable = PipelinePathResolver.Resolve(options.FfmpegPath) ??
            options.FfmpegPath;
        try
        {
            ProcessCapture version = await RunAsync(
                executable,
                ["-hide_banner", "-version"],
                cancellationToken);
            ProcessCapture filters = await RunAsync(
                executable,
                ["-hide_banner", "-filters"],
                cancellationToken);
            if (version.ExitCode != 0 || filters.ExitCode != 0)
            {
                return Unavailable(
                    executable,
                    $"FFmpeg capability scan failed: {version.Error} {filters.Error}".Trim());
            }
            string? versionLine = version.Output
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            HashSet<string> names = FilterPattern()
                .Matches(filters.Output)
                .Select(match => match.Groups["name"].Value)
                .ToHashSet(StringComparer.Ordinal);
            return new FfmpegCapabilities(
                "1.0",
                true,
                executable,
                versionLine,
                names,
                timeProvider.GetUtcNow(),
                []);
        }
        catch (Exception exception) when (
            exception is Win32Exception or IOException or InvalidOperationException)
        {
            return Unavailable(executable, exception.Message);
        }
    }

    public async Task WriteAsync(
        FfmpegCapabilities capabilities,
        string path,
        CancellationToken cancellationToken)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporary = fullPath + ".tmp";
        if (File.Exists(temporary))
            File.Delete(temporary);
        await File.WriteAllTextAsync(
            temporary,
            JsonSerializer.Serialize(capabilities, JsonOptions),
            cancellationToken);
        File.Move(temporary, fullPath, true);
    }

    private FfmpegCapabilities Unavailable(string executable, string warning) =>
        new(
            "1.0",
            false,
            executable,
            null,
            new HashSet<string>(StringComparer.Ordinal),
            timeProvider.GetUtcNow(),
            [warning]);

    private static async Task<ProcessCapture> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);
        using Process process = new() { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException("FFmpeg process did not start.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new ProcessCapture(
            process.ExitCode,
            await output + Environment.NewLine + await error,
            await error);
    }

    private sealed record ProcessCapture(int ExitCode, string Output, string Error);

    [GeneratedRegex(
        @"(?m)^\s*[TSC\.]{2,3}\s+(?<name>[a-zA-Z0-9_]+)\s+",
        RegexOptions.CultureInvariant)]
    private static partial Regex FilterPattern();
}
