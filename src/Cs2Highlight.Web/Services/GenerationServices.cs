using System.Security.Cryptography;
using System.Threading.Channels;
using System.Collections.Concurrent;
using Cs2Highlight.Analysis;
using Cs2Highlight.Web.Domain;
using Microsoft.AspNetCore.Http;

namespace Cs2Highlight.Web.Services;

public sealed class UploadOptions
{
    public int MaximumFilesPerGeneration { get; set; } = 10;
    public long MaximumFileSizeBytes { get; set; } = 1_073_741_824;
    public long MaximumTotalSizeBytes { get; set; } = 5_368_709_120;
    public long MinimumDemoSizeBytes { get; set; } = 1024;
    public long MinimumFreeDiskSpaceBytes { get; set; } = 10_737_418_240;
    public string[] AllowedExtensions { get; set; } = [".dem"];
}

public sealed class StorageOptions
{
    public string Root { get; set; } = "storage/generations";
}

public sealed class PipelineOptions
{
    public string DemoParserPath { get; set; } = "artifacts/demo-parser/demo-parser.exe";
    public string RenderAgentPath { get; set; } =
        "src/Cs2Highlight.RenderAgent/bin/Release/net8.0/render-agent.exe";
    public string FfmpegPath { get; set; } = "ffmpeg.exe";
    public string FfprobePath { get; set; } = "ffprobe.exe";
    public int FfmpegTimeoutSeconds { get; set; } = 600;
    public DemoFailurePolicy DemoFailurePolicy { get; set; } = DemoFailurePolicy.SkipInvalidDemo;
}

public sealed class RetentionOptions
{
    public int DraftGenerationHours { get; set; } = 24;
    public int UnpaidGenerationHours { get; set; } = 24;
    public int CompletedGenerationDays { get; set; } = 7;
    public int FailedGenerationDays { get; set; } = 3;
    public int CancelledGenerationDays { get; set; } = 3;
    public int IntermediateClipHoursAfterCompletion { get; set; } = 24;
    public int LogsDays { get; set; } = 14;
    public int CleanupIntervalMinutes { get; set; } = 60;
}

public sealed class GenerationWakeSignal
{
    private readonly Channel<bool> wakeups = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });

    public void Wake() => wakeups.Writer.TryWrite(true);
    public ValueTask<bool> WaitAsync(CancellationToken cancellationToken) =>
        wakeups.Reader.ReadAsync(cancellationToken);
}

public sealed class GenerationCancellationRegistry : IDisposable
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> sources =
        new(StringComparer.Ordinal);

    public CancellationToken TokenFor(string publicId) =>
        sources.GetOrAdd(publicId, _ => new CancellationTokenSource()).Token;

    public void Cancel(string publicId)
    {
        if (sources.TryGetValue(publicId, out CancellationTokenSource? source)) source.Cancel();
        else
        {
            CancellationTokenSource created = new();
            if (sources.TryAdd(publicId, created)) created.Cancel();
            else created.Dispose();
        }
    }

    public void Complete(string publicId)
    {
        if (sources.TryRemove(publicId, out CancellationTokenSource? source)) source.Dispose();
    }

    public void Dispose()
    {
        foreach (CancellationTokenSource source in sources.Values) source.Dispose();
        sources.Clear();
    }
}

public sealed record StoredUpload(
    string OriginalFileName,
    string StoredPath,
    long Size,
    string Sha256,
    bool Duplicate);

public sealed class GenerationStorage(StorageOptions options)
{
    public string Root { get; } = Path.GetFullPath(options.Root);

    public string GenerationRoot(string publicId)
    {
        if (publicId.Length is < 20 or > 64 ||
            publicId.Any(character => !char.IsAsciiLetterOrDigit(character)))
            throw new ArgumentException("Invalid generation public ID.", nameof(publicId));
        string path = Path.GetFullPath(Path.Combine(Root, publicId));
        EnsureWithinRoot(path);
        return path;
    }

    public string EnsureDirectory(string publicId, params string[] segments)
    {
        string path = GenerationRoot(publicId);
        foreach (string segment in segments)
        {
            if (string.IsNullOrWhiteSpace(segment) || segment is "." or ".." ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException("Invalid storage segment.", nameof(segments));
            path = Path.Combine(path, segment);
        }
        path = Path.GetFullPath(path);
        EnsureWithinRoot(path);
        Directory.CreateDirectory(path);
        return path;
    }

    public void EnsureWithinRoot(string path)
    {
        string normalizedRoot = Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Storage path escaped configured root.");
    }
}

public sealed class DemoUploadService(
    GenerationStorage storage,
    UploadOptions options)
{
    public async Task<IReadOnlyList<StoredUpload>> SaveAsync(
        string publicId,
        IReadOnlyList<IFormFile> files,
        CancellationToken cancellationToken)
    {
        if (files.Count == 0) throw new InvalidOperationException("NO_FILES_UPLOADED");
        if (files.Count > options.MaximumFilesPerGeneration)
            throw new InvalidOperationException("TOO_MANY_FILES");
        if (files.Sum(file => file.Length) > options.MaximumTotalSizeBytes)
            throw new InvalidOperationException("TOTAL_UPLOAD_TOO_LARGE");
        string uploads = storage.EnsureDirectory(publicId, "uploads");
        DriveInfo drive = new(Path.GetPathRoot(uploads)!);
        if (drive.AvailableFreeSpace < options.MinimumFreeDiskSpaceBytes)
            throw new InvalidOperationException("INSUFFICIENT_DISK_SPACE");
        List<StoredUpload> result = [];
        HashSet<string> hashes = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < files.Count; index++)
        {
            IFormFile file = files[index];
            if (file.Length > options.MaximumFileSizeBytes) throw new InvalidOperationException("FILE_TOO_LARGE");
            if (file.Length < options.MinimumDemoSizeBytes) throw new InvalidOperationException("INVALID_DEMO");
            string extension = Path.GetExtension(file.FileName);
            if (!options.AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("INVALID_DEMO");
            string temporary = Path.Combine(uploads, $".upload-{Guid.NewGuid():N}.tmp");
            string destination = Path.Combine(uploads, $"demo-{index + 1:D3}.dem");
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
                    if (written > options.MaximumFileSizeBytes) throw new InvalidOperationException("FILE_TOO_LARGE");
                    hash.AppendData(buffer.AsSpan(0, read));
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                await output.FlushAsync(cancellationToken);
                await output.DisposeAsync();
                await input.DisposeAsync();
                string sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                bool duplicate = !hashes.Add(sha256);
                if (duplicate)
                {
                    File.Delete(temporary);
                }
                else
                {
                    File.Move(temporary, destination);
                }
                result.Add(new StoredUpload(
                    Path.GetFileName(file.FileName), duplicate ? string.Empty : destination,
                    written, sha256, duplicate));
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        return result;
    }
}

public sealed record GlobalHighlightCandidate(
    long SourceDemoId,
    string DemoPath,
    int DemoOrder,
    HighlightCandidate Highlight);

public sealed class GlobalHighlightSelector
{
    public IReadOnlyList<GlobalHighlightCandidate> Select(
        IEnumerable<GlobalHighlightCandidate> candidates,
        string steamId,
        int maximum,
        double minimumScore,
        OutputOrder outputOrder)
    {
        if (maximum is not (3 or 5 or 10)) throw new ArgumentOutOfRangeException(nameof(maximum));
        GlobalHighlightCandidate[] top = candidates
            .Where(value => value.Highlight.PlayerId == steamId && value.Highlight.Score >= minimumScore)
            .OrderByDescending(value => value.Highlight.Score)
            .ThenByDescending(value => value.Highlight.KillCount)
            .ThenByDescending(value => value.Highlight.HeadshotCount)
            .ThenBy(value => value.Highlight.EndTick - value.Highlight.StartTick)
            .ThenBy(value => value.DemoOrder)
            .ThenBy(value => value.Highlight.FirstKillTick)
            .ThenBy(value => value.Highlight.Id, StringComparer.Ordinal)
            .Take(maximum)
            .ToArray();
        return outputOrder == OutputOrder.ScoreDescending
            ? top
            : top.OrderBy(value => value.DemoOrder)
                .ThenBy(value => value.Highlight.FirstKillTick)
                .ThenBy(value => value.Highlight.Id, StringComparer.Ordinal)
                .ToArray();
    }
}
