using System.Diagnostics;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Cs2Highlight.Web.Services;

public sealed record CompilationRequest(
    IReadOnlyList<string> ClipPaths,
    string OutputDirectory,
    int Width,
    int Height,
    int Fps,
    int MinimumOutputBytes = 1024,
    IReadOnlyList<HighlightEffectPlan>? EffectPlans = null);
public sealed record CompilationProgress(int Percent, string Stage);
public sealed record CompilationVideo(int Width, int Height, int Fps, string Codec);
public sealed record CompilationAudio(string? Codec, int? SampleRate);
public sealed record CompilationResult(
    string SchemaVersion,
    bool Success,
    string? OutputFile,
    int IncludedClips,
    int SkippedClips,
    long DurationMilliseconds,
    long FileSizeBytes,
    CompilationVideo? Video,
    CompilationAudio? Audio,
    string? Error);

public interface IHighlightCompilationService
{
    Task<CompilationResult> ComposeAsync(
        CompilationRequest request,
        IProgress<CompilationProgress>? progress,
        CancellationToken cancellationToken);
}

public sealed record VideoOutputOptions(int Width, int Height, int Fps);
public sealed record FfmpegFilterGraph(string Video, string Audio);

public interface IEffectFilterGraphBuilder
{
    FfmpegFilterGraph Build(
        double durationSeconds,
        HighlightEffectPlan? plan,
        VideoOutputOptions output);
}

public sealed class FfmpegEffectFilterGraphBuilder : IEffectFilterGraphBuilder
{
    public FfmpegFilterGraph Build(
        double durationSeconds,
        HighlightEffectPlan? plan,
        VideoOutputOptions output) =>
        new(
            FfmpegEffectFilterBuilder.Build(
                output.Width,
                output.Height,
                output.Fps,
                durationSeconds,
                plan),
            FfmpegEffectFilterBuilder.BuildAudio(durationSeconds, plan));
}

public sealed class FfmpegHighlightCompilationService(
    PipelineOptions options,
    IEffectFilterGraphBuilder filterGraphs)
    : IHighlightCompilationService
{
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(false);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<CompilationResult> ComposeAsync(
        CompilationRequest request,
        IProgress<CompilationProgress>? progress,
        CancellationToken cancellationToken)
    {
        Stopwatch watch = Stopwatch.StartNew();
        string outputDirectory = Path.GetFullPath(request.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);
        string normalizedDirectory = Path.Combine(outputDirectory, ".normalized");
        Directory.CreateDirectory(normalizedDirectory);
        List<string> normalized = [];
        List<string> probeErrors = [];
        int skipped = 0;
        for (int index = 0; index < request.ClipPaths.Count; index++)
        {
            string input = Path.GetFullPath(request.ClipPaths[index]);
            if (!File.Exists(input) || new FileInfo(input).Length == 0)
            {
                skipped++;
                continue;
            }
            MediaMetadata metadata = await ProbeAsync(input, cancellationToken);
            if (metadata.Error is not null)
            {
                probeErrors.Add($"{Path.GetFileName(input)}: {metadata.Error}");
                skipped++;
                continue;
            }
            if (!metadata.HasVideo || metadata.DurationSeconds <= 0)
            {
                skipped++;
                continue;
            }
            progress?.Report(new CompilationProgress(
                5 + (int)(60d * index / Math.Max(1, request.ClipPaths.Count)),
                $"Normalizing clip {index + 1}/{request.ClipPaths.Count}"));
            string target = Path.Combine(normalizedDirectory, $"clip-{index + 1:D3}.mp4");
            if (File.Exists(target))
            {
                MediaMetadata persisted = await ProbeAsync(target, cancellationToken);
                if (persisted.Error is null &&
                    persisted.HasVideo &&
                    persisted.DurationSeconds > 0 &&
                    persisted.Width == request.Width &&
                    persisted.Height == request.Height)
                {
                    normalized.Add(target);
                    continue;
                }
                File.Delete(target);
            }
            string temporaryTarget = Path.Combine(
                normalizedDirectory,
                $"clip-{index + 1:D3}.tmp.mp4");
            if (File.Exists(temporaryTarget)) File.Delete(temporaryTarget);
            List<string> arguments = ["-y", "-hide_banner", "-loglevel", "error", "-i", input];
            if (!metadata.HasAudio)
            {
                arguments.AddRange(
                    ["-f", "lavfi", "-i", "anullsrc=channel_layout=stereo:sample_rate=48000"]);
            }
            HighlightEffectPlan? effectPlan =
                request.EffectPlans is not null && index < request.EffectPlans.Count
                    ? request.EffectPlans[index]
                    : null;
            FfmpegFilterGraph graph = filterGraphs.Build(
                metadata.DurationSeconds,
                effectPlan,
                new VideoOutputOptions(request.Width, request.Height, request.Fps));
            arguments.AddRange(
            [
                "-map", "0:v:0",
                "-map", metadata.HasAudio ? "0:a:0" : "1:a:0",
                "-vf", graph.Video,
                "-af", graph.Audio,
                "-c:v", "libx264", "-preset", "medium", "-crf", "18",
                "-c:a", "aac", "-ar", "48000", "-ac", "2", "-b:a", "192k",
                "-shortest", "-movflags", "+faststart", temporaryTarget
            ]);
            ProcessResult normalization = await RunAsync(options.FfmpegPath, arguments, cancellationToken);
            await WriteProcessLogAsync(
                Path.Combine(normalizedDirectory, $"clip-{index + 1:D3}.ffmpeg.log"),
                normalization,
                cancellationToken);
            if (normalization.ExitCode != 0 || !File.Exists(temporaryTarget))
            {
                if (File.Exists(temporaryTarget)) File.Delete(temporaryTarget);
                skipped++;
                continue;
            }
            MediaMetadata normalizedMetadata =
                await ProbeAsync(temporaryTarget, cancellationToken);
            if (normalizedMetadata.Error is not null ||
                !normalizedMetadata.HasVideo ||
                normalizedMetadata.DurationSeconds <= 0 ||
                normalizedMetadata.Width != request.Width ||
                normalizedMetadata.Height != request.Height)
            {
                File.Delete(temporaryTarget);
                skipped++;
                continue;
            }
            File.Move(temporaryTarget, target, true);
            normalized.Add(target);
        }
        if (normalized.Count == 0)
            return Failure(
                probeErrors.Count > 0
                    ? $"CLIP_PROBE_FAILED: {string.Join(" | ", probeErrors)}"
                    : "NO_CLIPS_RENDERED",
                request.ClipPaths.Count,
                skipped,
                watch.ElapsedMilliseconds);

        string concatFile = Path.Combine(normalizedDirectory, "concat.txt");
        string concat = string.Join(
            Environment.NewLine,
            normalized.Select(path => $"file '{path.Replace("'", "'\\''", StringComparison.Ordinal)}'"));
        await File.WriteAllTextAsync(concatFile, concat, Utf8WithoutBom, cancellationToken);
        progress?.Report(new CompilationProgress(75, "Composing final video"));
        string temporary = Path.Combine(outputDirectory, "final-highlights.tmp.mp4");
        string final = Path.Combine(outputDirectory, "final-highlights.mp4");
        if (File.Exists(temporary)) File.Delete(temporary);
        ProcessResult composition = await RunAsync(
            options.FfmpegPath,
            ["-y", "-hide_banner", "-loglevel", "error", "-f", "concat", "-safe", "0",
             "-i", concatFile, "-c", "copy", "-movflags", "+faststart", temporary],
            cancellationToken);
        await WriteProcessLogAsync(
            Path.Combine(outputDirectory, "composition.ffmpeg.log"),
            composition,
            cancellationToken);
        if (composition.ExitCode != 0)
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            return Failure($"COMPILATION_FAILED: {composition.Error}", request.ClipPaths.Count, skipped, watch.ElapsedMilliseconds);
        }
        progress?.Report(new CompilationProgress(95, "Verifying final video"));
        MediaMetadata finalMetadata = await ProbeAsync(temporary, cancellationToken);
        FileInfo file = new(temporary);
        if (!finalMetadata.HasVideo || finalMetadata.DurationSeconds <= 0 ||
            finalMetadata.Width != request.Width || finalMetadata.Height != request.Height ||
            file.Length < request.MinimumOutputBytes)
            return Failure("FINAL_VIDEO_INVALID", request.ClipPaths.Count, skipped, watch.ElapsedMilliseconds);
        File.Move(temporary, final, true);
        CompilationResult result = new(
            "1.1", true, final, normalized.Count, skipped, watch.ElapsedMilliseconds,
            new FileInfo(final).Length,
            new CompilationVideo(request.Width, request.Height, request.Fps, finalMetadata.VideoCodec ?? "h264"),
            new CompilationAudio(finalMetadata.AudioCodec, finalMetadata.AudioSampleRate),
            null);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "compilation-result.json"),
            JsonSerializer.Serialize(result, JsonOptions),
            cancellationToken);
        progress?.Report(new CompilationProgress(100, "Completed"));
        return result;
    }

    private async Task<MediaMetadata> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        ProcessResult result = await RunAsync(
            options.FfprobePath,
            ["-v", "error", "-show_entries",
             "format=duration:stream=codec_type,codec_name,width,height,sample_rate",
             "-of", "json", path],
            cancellationToken);
        if (result.ExitCode != 0)
            return new MediaMetadata
            {
                Error = string.IsNullOrWhiteSpace(result.Error)
                    ? $"FFprobe exited with code {result.ExitCode}."
                    : result.Error.Trim()
            };
        using JsonDocument document = JsonDocument.Parse(result.Output);
        JsonElement[] streams = document.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        JsonElement? video = streams.Cast<JsonElement?>().FirstOrDefault(value =>
            value!.Value.GetProperty("codec_type").GetString() == "video");
        JsonElement? audio = streams.Cast<JsonElement?>().FirstOrDefault(value =>
            value!.Value.GetProperty("codec_type").GetString() == "audio");
        double.TryParse(
            document.RootElement.GetProperty("format").GetProperty("duration").GetString(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double duration);
        int? sampleRate = null;
        if (audio?.TryGetProperty("sample_rate", out JsonElement rate) == true &&
            int.TryParse(rate.GetString(), CultureInfo.InvariantCulture, out int parsedRate))
            sampleRate = parsedRate;
        return new MediaMetadata
        {
            HasVideo = video.HasValue,
            HasAudio = audio.HasValue,
            DurationSeconds = duration,
            Width = video?.GetProperty("width").GetInt32(),
            Height = video?.GetProperty("height").GetInt32(),
            VideoCodec = video?.GetProperty("codec_name").GetString(),
            AudioCodec = audio?.GetProperty("codec_name").GetString(),
            AudioSampleRate = sampleRate
        };
    }

    private async Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo start = new()
        {
            FileName = ResolveExecutable(executable),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = new() { StartInfo = start };
        try
        {
            if (!process.Start()) return new ProcessResult(-1, string.Empty, "Process did not start.");
        }
        catch (Exception exception) when (
            exception is Win32Exception or IOException or UnauthorizedAccessException)
        {
            return new ProcessResult(-1, string.Empty, exception.Message);
        }
        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(
            Math.Max(1, options.FfmpegTimeoutSeconds)));
        Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            if (!cancellationToken.IsCancellationRequested)
                return new ProcessResult(
                    -1,
                    string.Empty,
                    $"Process timed out after {options.FfmpegTimeoutSeconds} seconds.");
            throw;
        }
        return new ProcessResult(process.ExitCode, await output, await error);
    }

    private static Task WriteProcessLogAsync(
        string path,
        ProcessResult result,
        CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(
            path,
            $"exitCode={result.ExitCode}{Environment.NewLine}" +
            $"stdout:{Environment.NewLine}{result.Output}{Environment.NewLine}" +
            $"stderr:{Environment.NewLine}{result.Error}",
            Utf8WithoutBom,
            cancellationToken);

    private static string ResolveExecutable(string configured)
    {
        string full = Path.GetFullPath(configured);
        if (File.Exists(full)) return full;
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(directory.Trim(), configured);
            if (File.Exists(candidate)) return candidate;
        }
        return full;
    }

    private static CompilationResult Failure(string error, int total, int skipped, long duration) =>
        new("1.1", false, null, 0, Math.Max(skipped, total), duration, 0, null, null, error);

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
    private sealed class MediaMetadata
    {
        public bool HasVideo { get; init; }
        public bool HasAudio { get; init; }
        public double DurationSeconds { get; init; }
        public int? Width { get; init; }
        public int? Height { get; init; }
        public string? VideoCodec { get; init; }
        public string? AudioCodec { get; init; }
        public int? AudioSampleRate { get; init; }
        public string? Error { get; init; }
    }
}

public static class FfmpegEffectFilterBuilder
{
    public static string Build(
        int width,
        int height,
        int fps,
        double durationSeconds,
        HighlightEffectPlan? plan)
    {
        List<string> filters =
        [
            $"scale={width}:{height}:force_original_aspect_ratio=decrease",
            $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2",
            $"fps={fps}"
        ];
        if (plan?.Preset is Domain.EffectPreset.Clean or Domain.EffectPreset.Dynamic)
        {
            filters.Add("eq=saturation=1.02:contrast=1.01");
            filters.Add("fade=t=in:st=0:d=0.15");
            filters.Add(FormattableString.Invariant(
                $"fade=t=out:st={Math.Max(0, durationSeconds - 0.3):0.###}:d=0.3"));
        }
        if (plan?.Preset == Domain.EffectPreset.Dynamic)
        {
            EffectTimelineEvent[] zooms = plan.Events
                .Where(value => value.Type == EffectType.SmoothZoom)
                .ToArray();
            if (zooms.Length > 0)
            {
                string activity = zooms
                    .Select(value => Pulse(value))
                    .Aggregate((left, right) => $"max({left}\\,{right})");
                double intensity = zooms.Max(value => value.Intensity);
                string factor = FormattableString.Invariant($"1+{intensity:0.####}*{activity}");
                filters.Add($"scale=w='{width}*({factor})':h='{height}*({factor})':eval=frame");
                filters.Add($"crop={width}:{height}:(iw-ow)/2:(ih-oh)/2");
            }
            foreach (EffectTimelineEvent flash in plan.Events.Where(value =>
                         value.Type == EffectType.HeadshotFlash))
            {
                filters.Add(FormattableString.Invariant(
                    $"eq=brightness={flash.Intensity:0.####}:enable='{Between(flash)}'"));
            }
            foreach (EffectTimelineEvent vignette in plan.Events.Where(value =>
                         value.Type == EffectType.VignettePulse))
            {
                filters.Add(
                    $"vignette=PI/12:eval=frame:enable='{Between(vignette)}'");
            }
        }
        filters.Add("format=yuv420p");
        return string.Join(',', filters);
    }

    public static string BuildAudio(
        double durationSeconds,
        HighlightEffectPlan? plan)
    {
        List<string> filters = ["aresample=48000"];
        if (plan?.Preset is Domain.EffectPreset.Clean or Domain.EffectPreset.Dynamic)
        {
            filters.Add("loudnorm=I=-16:TP=-1.5:LRA=11");
            filters.Add("afade=t=in:st=0:d=0.15");
            filters.Add(FormattableString.Invariant(
                $"afade=t=out:st={Math.Max(0, durationSeconds - 0.3):0.###}:d=0.3"));
        }
        return string.Join(',', filters);
    }

    private static string Between(EffectTimelineEvent value)
    {
        double start = value.StartMilliseconds / 1000d;
        double end = (value.StartMilliseconds + value.DurationMilliseconds) / 1000d;
        return FormattableString.Invariant($"between(t\\,{start:0.###}\\,{end:0.###})");
    }

    private static string Pulse(EffectTimelineEvent value)
    {
        double start = value.StartMilliseconds / 1000d;
        double duration = Math.Max(0.001, value.DurationMilliseconds / 1000d);
        double end = start + duration;
        double rise = value.PeakOffsetMilliseconds > 0
            ? Math.Min(duration, value.PeakOffsetMilliseconds / 1000d)
            : duration / 2;
        rise = Math.Max(0.001, rise);
        double fall = Math.Max(0.001, duration - rise);
        double peak = start + rise;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"if(between(t\\,{start:0.###}\\,{end:0.###})\\,if(lt(t\\,{peak:0.###})\\,(t-{start:0.###})/{rise:0.###}\\,({end:0.###}-t)/{fall:0.###})\\,0)");
    }
}
