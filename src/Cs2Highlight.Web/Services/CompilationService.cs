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
    int MinimumOutputBytes = 1024);
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

public sealed class FfmpegHighlightCompilationService(PipelineOptions options)
    : IHighlightCompilationService
{
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
            List<string> arguments = ["-y", "-hide_banner", "-loglevel", "error", "-i", input];
            if (!metadata.HasAudio)
            {
                arguments.AddRange(
                    ["-f", "lavfi", "-i", "anullsrc=channel_layout=stereo:sample_rate=48000"]);
            }
            arguments.AddRange(
            [
                "-map", "0:v:0",
                "-map", metadata.HasAudio ? "0:a:0" : "1:a:0",
                "-vf", $"scale={request.Width}:{request.Height}:force_original_aspect_ratio=decrease,pad={request.Width}:{request.Height}:(ow-iw)/2:(oh-ih)/2,fps={request.Fps},format=yuv420p",
                "-c:v", "libx264", "-preset", "medium", "-crf", "18",
                "-c:a", "aac", "-ar", "48000", "-ac", "2", "-b:a", "192k",
                "-shortest", "-movflags", "+faststart", target
            ]);
            ProcessResult normalization = await RunAsync(options.FfmpegPath, arguments, cancellationToken);
            if (normalization.ExitCode != 0 || !File.Exists(target))
            {
                skipped++;
                continue;
            }
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
        await File.WriteAllTextAsync(concatFile, concat, Encoding.UTF8, cancellationToken);
        progress?.Report(new CompilationProgress(75, "Composing final video"));
        string temporary = Path.Combine(outputDirectory, "final-highlights.tmp.mp4");
        string final = Path.Combine(outputDirectory, "final-highlights.mp4");
        if (File.Exists(temporary)) File.Delete(temporary);
        ProcessResult composition = await RunAsync(
            options.FfmpegPath,
            ["-y", "-hide_banner", "-loglevel", "error", "-f", "concat", "-safe", "0",
             "-i", concatFile, "-c", "copy", "-movflags", "+faststart", temporary],
            cancellationToken);
        if (composition.ExitCode != 0)
            return Failure($"COMPILATION_FAILED: {composition.Error}", request.ClipPaths.Count, skipped, watch.ElapsedMilliseconds);
        progress?.Report(new CompilationProgress(95, "Verifying final video"));
        MediaMetadata finalMetadata = await ProbeAsync(temporary, cancellationToken);
        FileInfo file = new(temporary);
        if (!finalMetadata.HasVideo || finalMetadata.DurationSeconds <= 0 ||
            finalMetadata.Width != request.Width || finalMetadata.Height != request.Height ||
            file.Length < request.MinimumOutputBytes)
            return Failure("FINAL_VIDEO_INVALID", request.ClipPaths.Count, skipped, watch.ElapsedMilliseconds);
        File.Move(temporary, final, true);
        CompilationResult result = new(
            "1.0", true, final, normalized.Count, skipped, watch.ElapsedMilliseconds,
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

    private static async Task<ProcessResult> RunAsync(
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
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
        try { await process.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            throw;
        }
        return new ProcessResult(process.ExitCode, await output, await error);
    }

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
        new("1.0", false, null, 0, Math.Max(skipped, total), duration, 0, null, null, error);

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
