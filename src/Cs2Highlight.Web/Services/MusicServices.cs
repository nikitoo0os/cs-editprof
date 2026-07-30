using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Cs2Highlight.Music;
using Microsoft.AspNetCore.Http;

namespace Cs2Highlight.Web.Services;

public sealed class MusicUploadOptions
{
    public long MaximumFileSizeBytes { get; set; } = 209_715_200;
    public double MinimumDurationSeconds { get; set; } = 15;
    public double MaximumDurationSeconds { get; set; } = 600;
    public long MinimumFreeDiskSpaceBytes { get; set; } = 2_147_483_648;
    public string[] AllowedExtensions { get; set; } =
        [".mp3", ".wav", ".flac", ".m4a", ".aac"];
}

public sealed class TrustedLutOptions
{
    public string Root { get; set; } = "assets/luts";
    public Dictionary<string, string> Assets { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class TrustedLutCatalog(TrustedLutOptions options)
{
    public IReadOnlyList<string> Keys => options.Assets.Keys
        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public string? Resolve(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return null;
        if (!options.Assets.TryGetValue(key, out string? configured) ||
            string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException("UNKNOWN_LUT_ASSET");
        string root = Path.GetFullPath(options.Root);
        string path = Path.GetFullPath(Path.Combine(root, configured));
        string rootPrefix = root.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(path), ".cube", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("UNTRUSTED_LUT_ASSET");
        if (!File.Exists(path))
            throw new InvalidOperationException("LUT_ASSET_MISSING");
        return path;
    }
}

public sealed record MusicMediaMetadata(
    double DurationSeconds,
    int SampleRate,
    int Channels,
    string Codec);

public sealed record StoredMusicUpload(
    string OriginalFileName,
    string StoredPath,
    long Size,
    string Sha256,
    string ContentType,
    MusicMediaMetadata Metadata);

public interface IMusicMediaValidator
{
    Task<MusicMediaMetadata> ValidateAsync(string path, CancellationToken cancellationToken);
}

public sealed class FfprobeMusicMediaValidator(
    PipelineOptions pipeline,
    MusicUploadOptions options) : IMusicMediaValidator
{
    public async Task<MusicMediaMetadata> ValidateAsync(
        string path,
        CancellationToken cancellationToken)
    {
        ProcessResult probe = await RunAsync(
            pipeline.FfprobePath,
            ["-v", "error", "-show_entries",
             "format=duration:stream=codec_type,codec_name,sample_rate,channels",
             "-of", "json", path],
            cancellationToken);
        if (probe.ExitCode != 0)
            throw new InvalidOperationException("MUSIC_FFPROBE_FAILED");
        try
        {
            using JsonDocument document = JsonDocument.Parse(probe.Output);
            JsonElement audio = document.RootElement.GetProperty("streams")
                .EnumerateArray()
                .First(value => value.GetProperty("codec_type").GetString() == "audio");
            double duration = double.Parse(
                document.RootElement.GetProperty("format").GetProperty("duration").GetString()!,
                CultureInfo.InvariantCulture);
            if (duration < options.MinimumDurationSeconds)
                throw new InvalidOperationException("MUSIC_TOO_SHORT");
            if (duration > options.MaximumDurationSeconds)
                throw new InvalidOperationException("MUSIC_TOO_LONG");
            int sampleRate = int.Parse(audio.GetProperty("sample_rate").GetString()!, CultureInfo.InvariantCulture);
            int channels = audio.GetProperty("channels").GetInt32();
            string codec = audio.GetProperty("codec_name").GetString() ?? "unknown";
            ProcessResult decode = await RunAsync(
                pipeline.FfmpegPath,
                ["-v", "error", "-t", "3", "-i", path, "-map", "0:a:0", "-f", "null", "-"],
                cancellationToken);
            if (decode.ExitCode != 0)
                throw new InvalidOperationException("MUSIC_DECODING_FAILED");
            return new MusicMediaMetadata(duration, sampleRate, channels, codec);
        }
        catch (InvalidOperationException) { throw; }
        catch (Exception exception) when (
            exception is JsonException or KeyNotFoundException or FormatException)
        {
            throw new InvalidOperationException("MUSIC_NO_AUDIO_STREAM", exception);
        }
    }

    private static async Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        string? resolvedExecutable = PipelinePathResolver.Resolve(executable);
        if (resolvedExecutable is null)
            throw new InvalidOperationException("MEDIA_PROCESS_START_FAILED");
        ProcessStartInfo start = new()
        {
            FileName = resolvedExecutable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = new() { StartInfo = start };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("MEDIA_PROCESS_START_FAILED");
        }
        catch (Exception exception) when (
            exception is Win32Exception or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("MEDIA_PROCESS_START_FAILED", exception);
        }
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        string stderr = await error;
        return new ProcessResult(
            process.ExitCode,
            await output,
            stderr.Length > 16_384 ? stderr[..16_384] : stderr);
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}

public sealed class MusicUploadService(
    GenerationStorage storage,
    MusicUploadOptions options,
    IMusicMediaValidator mediaValidator)
{
    public async Task<StoredMusicUpload> SaveAsync(
        string publicId,
        IFormFile file,
        bool rightsConfirmed,
        CancellationToken cancellationToken)
    {
        if (!rightsConfirmed) throw new InvalidOperationException("MUSIC_RIGHTS_CONFIRMATION_REQUIRED");
        if (file.Length <= 0) throw new InvalidOperationException("MUSIC_FILE_EMPTY");
        if (file.Length > options.MaximumFileSizeBytes)
            throw new InvalidOperationException("MUSIC_FILE_TOO_LARGE");
        string extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!options.AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("MUSIC_UNSUPPORTED_FORMAT");
        string directory = storage.EnsureDirectory(publicId, "uploads", "music");
        DriveInfo drive = new(Path.GetPathRoot(directory)!);
        if (drive.AvailableFreeSpace < options.MinimumFreeDiskSpaceBytes)
            throw new InvalidOperationException("INSUFFICIENT_DISK_SPACE");
        string temporary = Path.Combine(directory, $".upload-{Guid.NewGuid():N}.tmp");
        string destination = Path.Combine(directory, $"track{extension}");
        try
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using Stream input = file.OpenReadStream();
            await using FileStream output = new(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] buffer = new byte[128 * 1024];
            long written = 0;
            while (true)
            {
                int read = await input.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                written += read;
                if (written > options.MaximumFileSizeBytes)
                    throw new InvalidOperationException("MUSIC_FILE_TOO_LARGE");
                hash.AppendData(buffer.AsSpan(0, read));
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            await output.FlushAsync(cancellationToken);
            await output.DisposeAsync();
            await input.DisposeAsync();
            string sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            MusicMediaMetadata metadata =
                await mediaValidator.ValidateAsync(temporary, cancellationToken);
            if (File.Exists(destination))
                throw new InvalidOperationException("MUSIC_ALREADY_UPLOADED");
            File.Move(temporary, destination);
            return new StoredMusicUpload(
                Path.GetFileName(file.FileName),
                destination,
                written,
                sha256,
                string.IsNullOrWhiteSpace(file.ContentType)
                    ? "application/octet-stream"
                    : file.ContentType[..Math.Min(128, file.ContentType.Length)],
                metadata);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

public interface IMusicAnalyzerClient
{
    Task<MusicAnalysis> AnalyzeAsync(
        string inputPath,
        string outputPath,
        string logPath,
        CancellationToken cancellationToken);
}

public sealed class ProcessMusicAnalyzerClient(PipelineOptions pipeline) : IMusicAnalyzerClient
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task<MusicAnalysis> AnalyzeAsync(
        string inputPath,
        string outputPath,
        string logPath,
        CancellationToken cancellationToken)
    {
        string executable = PipelinePathResolver.Resolve(
            pipeline.MusicAnalyzerPath) ??
            throw new InvalidOperationException(
                $"MUSIC_ANALYZER_NOT_FOUND: {pipeline.MusicAnalyzerPath}");
        ProcessStartInfo start = new()
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in new[]
        {
            "analyze", "--input", inputPath, "--output", outputPath
        })
            start.ArgumentList.Add(argument);
        using Process process = new() { StartInfo = start };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("MUSIC_ANALYZER_START_FAILED");
        }
        catch (Exception exception) when (
            exception is Win32Exception or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                "MUSIC_ANALYZER_START_FAILED",
                exception);
        }
        using CancellationTokenSource timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(
            Math.Max(1, pipeline.MusicAnalyzerTimeoutSeconds)));
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("MUSIC_ANALYZER_TIMEOUT");
        }
        string output = await stdout;
        string error = await stderr;
        await File.WriteAllTextAsync(
            logPath,
            $"exitCode={process.ExitCode}\nstdout:\n{output}\nstderr:\n{error}",
            cancellationToken);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"MUSIC_ANALYSIS_FAILED: {error}");
        await using FileStream stream = File.OpenRead(outputPath);
        MusicAnalysis analysis =
            await JsonSerializer.DeserializeAsync<MusicAnalysis>(
                stream, JsonOptions, cancellationToken) ??
            throw new InvalidOperationException("MUSIC_ANALYSIS_INVALID");
        if (analysis.SchemaVersion is not ("1.0" or "2.0") ||
            analysis.Audio.DurationSeconds <= 0 ||
            analysis.Audio.SampleRate <= 0 ||
            analysis.Audio.Channels <= 0 ||
            (analysis.SchemaVersion == "2.0" &&
                (analysis.FrameHopSeconds is < 0.02 or > 0.05 ||
                 analysis.Frames.Count == 0)) ||
            analysis.Beats.Zip(analysis.Beats.Skip(1))
                .Any(pair => pair.First.TimeSeconds > pair.Second.TimeSeconds) ||
            analysis.Frames.Zip(analysis.Frames.Skip(1))
                .Any(pair => pair.First.TimeSeconds > pair.Second.TimeSeconds) ||
            analysis.Frames.Any(value =>
                value.Energy is < 0 or > 1 ||
                value.BassEnergy is < 0 or > 1 ||
                value.OnsetStrength is < 0 or > 1 ||
                value.SpectralFlux is < 0 or > 1 ||
                value.SpectralBrightness is < 0 or > 1 ||
                value.Novelty is < 0 or > 1 ||
                value.RhythmicDensity is < 0 or > 1 ||
                value.HarmonicChange is < 0 or > 1) ||
            analysis.Sections.Any(value =>
                value.StartSeconds < 0 ||
                value.EndSeconds <= value.StartSeconds ||
                value.EndSeconds > analysis.Audio.DurationSeconds + 0.05 ||
                value.Energy is < 0 or > 1 ||
                value.RhythmicDensity is < 0 or > 1 ||
                value.BassEnergy is < 0 or > 1 ||
                value.SpectralBrightness is < 0 or > 1 ||
                value.DynamicContrast is < 0 or > 1 ||
                value.Confidence is < 0 or > 1))
            throw new InvalidOperationException("MUSIC_ANALYSIS_INVALID");
        return analysis;
    }
}
