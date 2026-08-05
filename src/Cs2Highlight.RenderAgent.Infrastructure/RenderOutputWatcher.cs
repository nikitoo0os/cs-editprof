using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Cs2Highlight.RenderAgent.Application;

namespace Cs2Highlight.RenderAgent.Infrastructure;

public sealed class RenderOutputWatcher(RenderEnvironmentOptions options, TimeProvider timeProvider) : IRenderOutputWatcher
{
    private static readonly JsonSerializerOptions ReportJsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<(bool Success, string? File, long Size, string? Error)> VerifyAsync(
        RenderJob job,
        RenderWorkspace workspace,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = timeProvider.GetUtcNow().AddSeconds(Math.Min(job.TimeoutSeconds, 120));
        string? candidate = null;
        long previousSize = -1;
        DateTimeOffset stableSince = timeProvider.GetUtcNow();
        while (timeProvider.GetUtcNow() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            candidate = Directory.EnumerateFiles(workspace.Raw, "*.mp4", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (candidate is not null)
            {
                string capturedAudio = ResolveCapturedAudioPath(candidate);
                long videoSize = new FileInfo(candidate).Length;
                long audioSize = File.Exists(capturedAudio)
                    ? new FileInfo(capturedAudio).Length
                    : 0;
                long size = videoSize + audioSize;
                if (size != previousSize)
                {
                    previousSize = size;
                    stableSince = timeProvider.GetUtcNow();
                }
                else if (videoSize >= options.MinimumOutputBytes &&
                         audioSize > 44 &&
                         timeProvider.GetUtcNow() - stableSince >= TimeSpan.FromSeconds(options.OutputStableSeconds))
                {
                    MediaProbeResult probe = await ProbeAsync(
                        candidate,
                        job.Video,
                        requireAudio: false,
                        cancellationToken);
                    if (!probe.Success)
                    {
                        return (false, candidate, size, probe.Error);
                    }
                    if (options.EnableClipStartQualityGate)
                    {
                        string qualityReportPath = Path.Combine(
                            workspace.Logs,
                            "clip-artifact-quality.json");
                        ClipStartQualityResult quality = await AnalyzeClipStartAsync(
                            candidate,
                            Path.Combine(workspace.Logs, "clip-start-quality.log"),
                            cancellationToken);
                        await WriteClipQualityReportAsync(
                            qualityReportPath,
                            candidate,
                            quality,
                            cancellationToken);
                        if (!quality.Success)
                        {
                            return (
                                false,
                                candidate,
                                size,
                                $"CLIP_START_QUALITY_FAILED: {quality.Error}");
                        }
                        Directory.CreateDirectory(job.OutputDirectory);
                        File.Copy(
                            qualityReportPath,
                            Path.Combine(
                                job.OutputDirectory,
                                "clip-artifact-quality.json"),
                            overwrite: true);
                    }
                    if (options.EnableDemoUiDetection)
                    {
                        DemoUiDetectionReport ui =
                            await DemoUiDetector.AnalyzeAsync(
                                options.FfmpegExecutablePath ?? string.Empty,
                                candidate,
                                options.DemoUiDetectionSampleSeconds,
                                cancellationToken);
                        string reportPath = Path.Combine(
                            workspace.Logs,
                            "demo-ui-detection-report.json");
                        await DemoUiDetector.WriteAsync(
                            reportPath,
                            ui,
                            cancellationToken);
                        Directory.CreateDirectory(job.OutputDirectory);
                        File.Copy(
                            reportPath,
                            Path.Combine(
                                job.OutputDirectory,
                                "demo-ui-detection-report.json"),
                            overwrite: true);
                        if (!ui.Analyzed)
                        {
                            return (
                                false,
                                candidate,
                                size,
                                $"DEMO_UI_DETECTION_FAILED: {ui.Error}");
                        }
                        if (ui.DemoPlaybackStripDetected)
                        {
                            return (
                                false,
                                candidate,
                                size,
                                "DEMO_PLAYBACK_STRIP_DETECTED");
                        }
                    }

                    Directory.CreateDirectory(job.OutputDirectory);
                    string destination = Path.Combine(job.OutputDirectory, "raw-highlight.mp4");
                    string muxLog = Path.Combine(
                        job.OutputDirectory,
                        "gameplay-audio-mux.ffmpeg.log");
                    string? muxError = await MuxGameplayAudioAsync(
                        candidate,
                        capturedAudio,
                        destination,
                        muxLog,
                        cancellationToken);
                    if (muxError is not null)
                        return (false, candidate, videoSize, muxError);
                    MediaProbeResult muxedProbe = await ProbeAsync(
                        destination,
                        job.Video,
                        requireAudio: true,
                        cancellationToken);
                    if (!muxedProbe.Success)
                    {
                        return (
                            false,
                            destination,
                            new FileInfo(destination).Length,
                            muxedProbe.Error);
                    }
                    long destinationSize = new FileInfo(destination).Length;
                    return (true, destination, destinationSize, null);
                }
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250), timeProvider, cancellationToken);
        }
        string error = candidate is not null &&
            (!File.Exists(ResolveCapturedAudioPath(candidate)) ||
             new FileInfo(ResolveCapturedAudioPath(candidate)).Length <= 44)
                ? "GAMEPLAY_AUDIO_MISSING: HLAE did not produce audio.wav next to the rendered video."
                : "No stable non-empty video and gameplay-audio recording appeared before timeout.";
        return (false, candidate, previousSize < 0 ? 0 : previousSize, error);
    }

    public static string ResolveCapturedAudioPath(string videoPath) =>
        Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(videoPath))!,
            "audio.wav");

    private async Task<string?> MuxGameplayAudioAsync(
        string videoPath,
        string audioPath,
        string destination,
        string logPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.FfmpegExecutablePath) ||
            !File.Exists(options.FfmpegExecutablePath))
        {
            return "GAMEPLAY_AUDIO_MUX_FAILED: FFmpeg is not configured.";
        }
        string temporary = destination + ".mux.tmp.mp4";
        if (File.Exists(temporary)) File.Delete(temporary);
        ProcessStartInfo start = new()
        {
            FileName = Path.GetFullPath(options.FfmpegExecutablePath),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in new[]
        {
            "-y", "-hide_banner", "-loglevel", "error",
            "-i", videoPath,
            "-i", audioPath,
            "-map", "0:v:0",
            "-map", "1:a:0",
            "-c:v", "copy",
            "-af", "aresample=48000:async=1:first_pts=0,apad",
            "-c:a", "aac",
            "-ar", "48000",
            "-ac", "2",
            "-b:a", "192k",
            "-shortest",
            "-movflags", "+faststart",
            temporary
        })
            start.ArgumentList.Add(argument);
        using Process process = new() { StartInfo = start };
        if (!process.Start())
            return "GAMEPLAY_AUDIO_MUX_FAILED: FFmpeg did not start.";
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(
            cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(
            cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
        string output = await stdout;
        string error = await stderr;
        await File.WriteAllTextAsync(
            logPath,
            $"exitCode={process.ExitCode}{Environment.NewLine}" +
            $"stdout:{Environment.NewLine}{output}{Environment.NewLine}" +
            $"stderr:{Environment.NewLine}{error}",
            cancellationToken);
        if (process.ExitCode != 0 || !File.Exists(temporary))
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            return $"GAMEPLAY_AUDIO_MUX_FAILED: {error.Trim()}";
        }
        File.Move(temporary, destination, overwrite: false);
        return null;
    }

    private async Task<ClipStartQualityResult> AnalyzeClipStartAsync(
        string path,
        string logPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.FfmpegExecutablePath))
            return new ClipStartQualityResult(false, "FFmpeg path is not configured.");
        string filter = FormattableString.Invariant(
            $"blackdetect=d={options.ClipStartBlackDurationSeconds:0.###}:pix_th=0.10,freezedetect=n=0.003:d={options.ClipStartFreezeDurationSeconds:0.###}");
        ProcessStartInfo start = new()
        {
            FileName = Path.GetFullPath(options.FfmpegExecutablePath),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in new[]
        {
            "-hide_banner", "-v", "info", "-t",
            options.ClipStartSampleSeconds.ToString("0.###", CultureInfo.InvariantCulture),
            "-i", path, "-an", "-vf", filter, "-f", "null", "-"
        })
            start.ArgumentList.Add(argument);
        using Process process = new() { StartInfo = start };
        if (!process.Start())
            return new ClipStartQualityResult(false, "FFmpeg quality process did not start.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        string diagnostic = (await stdout) + Environment.NewLine + (await stderr);
        await File.WriteAllTextAsync(logPath, diagnostic, cancellationToken);
        if (process.ExitCode != 0)
            return new ClipStartQualityResult(false, $"FFmpeg exited with code {process.ExitCode}.");
        return HasStartDefect(diagnostic)
            ? new ClipStartQualityResult(false, "Black or frozen opening frames exceeded the configured threshold.")
            : new ClipStartQualityResult(true, null);
    }

    public static bool HasStartDefect(string diagnostic) =>
        diagnostic.Contains("black_start:0", StringComparison.OrdinalIgnoreCase) ||
        diagnostic.Contains("freeze_start:0", StringComparison.OrdinalIgnoreCase);

    private Task WriteClipQualityReportAsync(
        string path,
        string mediaPath,
        ClipStartQualityResult result,
        CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(
                new
                {
                    schemaVersion = "1.0",
                    mediaFile = mediaPath,
                    success = result.Success,
                    sampleSeconds = options.ClipStartSampleSeconds,
                    maximumBlackSeconds =
                        options.ClipStartBlackDurationSeconds,
                    maximumFreezeSeconds =
                        options.ClipStartFreezeDurationSeconds,
                    result.Error,
                    analyzedAt = timeProvider.GetUtcNow()
                },
                ReportJsonOptions),
            cancellationToken);

    public static string ResolveFfprobePath(RenderEnvironmentOptions environment)
    {
        if (!string.IsNullOrWhiteSpace(environment.FfprobeExecutablePath))
        {
            return Path.GetFullPath(environment.FfprobeExecutablePath);
        }
        if (string.IsNullOrWhiteSpace(environment.FfmpegExecutablePath))
        {
            return string.Empty;
        }
        string? directory = Path.GetDirectoryName(Path.GetFullPath(environment.FfmpegExecutablePath));
        return directory is null ? string.Empty : Path.Combine(directory, "ffprobe.exe");
    }

    private async Task<MediaProbeResult> ProbeAsync(
        string path,
        VideoSettings expected,
        bool requireAudio,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = ResolveFfprobePath(options),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in new[]
        {
            "-v", "error",
            "-show_entries", "format=duration,size:stream=codec_type,width,height",
            "-of", "json",
            path
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = new() { StartInfo = startInfo };
        if (!process.Start())
        {
            return new MediaProbeResult(false, "ffprobe could not be started.");
        }
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            throw;
        }

        string stdout = await stdoutTask;
        string stderr = await stderrTask;
        if (process.ExitCode != 0)
        {
            return new MediaProbeResult(false, $"ffprobe rejected the rendered MP4: {stderr.Trim()}");
        }

        try
        {
            string? validationError = ValidateProbeJson(
                stdout,
                expected,
                requireAudio);
            return new MediaProbeResult(validationError is null, validationError);
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return new MediaProbeResult(false, $"Unable to validate ffprobe JSON: {exception.Message}");
        }
    }

    public static string? ValidateProbeJson(
        string json,
        VideoSettings expected,
        bool requireAudio = false)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        JsonElement video = root.GetProperty("streams")
            .EnumerateArray()
            .First(stream =>
                stream.TryGetProperty("codec_type", out JsonElement type) &&
                type.GetString() == "video");
        int width = video.GetProperty("width").GetInt32();
        int height = video.GetProperty("height").GetInt32();
        string? durationText = root.GetProperty("format").GetProperty("duration").GetString();
        bool durationValid = double.TryParse(
            durationText,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double duration) && duration > 0;
        if (!durationValid)
        {
            return "ffprobe reported a zero or invalid video duration.";
        }
        if (width != expected.Width || height != expected.Height)
        {
            return $"Rendered dimensions are {width}x{height}; expected {expected.Width}x{expected.Height}.";
        }
        if (requireAudio && !root.GetProperty("streams")
                .EnumerateArray()
                .Any(stream =>
                    stream.TryGetProperty("codec_type", out JsonElement type) &&
                    type.GetString() == "audio"))
        {
            return "GAMEPLAY_AUDIO_MISSING: muxed render has no audio stream.";
        }
        return null;
    }

    private sealed record MediaProbeResult(bool Success, string? Error);
    private sealed record ClipStartQualityResult(bool Success, string? Error);
}
