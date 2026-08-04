using Cs2Highlight.Web.Data;
using Cs2Highlight.Web.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Cs2Highlight.Web.Services;

public sealed class GenerationReadinessHealthCheck(
    IDbContextFactory<GenerationDbContext> dbFactory,
    GenerationStorage storage,
    PipelineOptions pipeline,
    UploadOptions uploadOptions) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(storage.Root);
            string probe = Path.Combine(storage.Root, $".health-{Environment.ProcessId}.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            await using GenerationDbContext db =
                await dbFactory.CreateDbContextAsync(cancellationToken);
            bool sqlite = await db.Database.CanConnectAsync(cancellationToken);
            DriveInfo drive = new(Path.GetPathRoot(storage.Root)!);
            Dictionary<string, object> data = new()
            {
                ["storage"] = storage.Root,
                ["sqlite"] = sqlite,
                ["freeDiskSpaceBytes"] = drive.AvailableFreeSpace,
                ["minimumFreeDiskSpaceBytes"] = uploadOptions.MinimumFreeDiskSpaceBytes,
                ["freeDiskSpace"] = drive.AvailableFreeSpace >= uploadOptions.MinimumFreeDiskSpaceBytes,
                ["demoParser"] = PipelinePathResolver.Resolve(pipeline.DemoParserPath) is not null,
                ["musicAnalyzer"] = PipelinePathResolver.Resolve(pipeline.MusicAnalyzerPath) is not null,
                ["renderAgent"] = PipelinePathResolver.Resolve(pipeline.RenderAgentPath) is not null,
                ["ffmpeg"] = PipelinePathResolver.Resolve(pipeline.FfmpegPath) is not null,
                ["ffprobe"] = PipelinePathResolver.Resolve(pipeline.FfprobePath) is not null
            };
            bool ready = data.Values.OfType<bool>().All(value => value);
            return ready
                ? HealthCheckResult.Healthy("Pipeline dependencies are available.", data)
                : HealthCheckResult.Unhealthy(
                    "Database, disk space, or a pipeline dependency is unavailable.", data: data);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return HealthCheckResult.Unhealthy("Storage is not writable.", exception);
        }
    }

}

public sealed class GenerationCleanupService(
    IDbContextFactory<GenerationDbContext> dbFactory,
    GenerationStorage storage,
    RetentionOptions options,
    TimeProvider timeProvider,
    GenerationMetrics metrics,
    ILogger<GenerationCleanupService> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogCleanupFailure =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(4101, nameof(LogCleanupFailure)),
            "Generation cleanup failed.");
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(
            TimeSpan.FromMinutes(Math.Max(1, options.CleanupIntervalMinutes)),
            timeProvider);
        do
        {
            try { await CleanupAsync(stoppingToken); }
            catch (Exception exception) { LogCleanupFailure(logger, exception); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task CleanupAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        await using GenerationDbContext db = await dbFactory.CreateDbContextAsync(cancellationToken);
        Generation[] completed = await db.Generations.Where(value =>
                (value.Status == GenerationStatus.Completed || value.Status == GenerationStatus.CompletedWithWarnings) &&
                (value.CleanupStatus == CleanupStatus.Pending || value.CleanupStatus == CleanupStatus.Failed))
            .Take(20).ToArrayAsync(cancellationToken);
        foreach (Generation generation in completed)
            CleanupIntermediateFiles(generation, now);

        Generation[] readyForDeletion = (await db.Generations
                .Where(value => value.Status == GenerationStatus.Expired)
                .ToArrayAsync(cancellationToken))
            .Where(value =>
                value.UpdatedAt < now.AddMinutes(-Math.Max(1, options.CleanupIntervalMinutes)))
            .ToArray();
        foreach (Generation generation in readyForDeletion)
        {
            string root = storage.GenerationRoot(generation.PublicId);
            storage.EnsureWithinRoot(root);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        Generation[] expirationCandidates = await db.Generations.Where(value =>
                value.Status == GenerationStatus.Draft ||
                value.Status == GenerationStatus.AwaitingPayment ||
                value.Status == GenerationStatus.Completed ||
                value.Status == GenerationStatus.CompletedWithWarnings ||
                value.Status == GenerationStatus.Failed ||
                value.Status == GenerationStatus.Cancelled)
            .ToArrayAsync(cancellationToken);
        Generation[] expired = expirationCandidates.Where(value =>
                (value.Status == GenerationStatus.Draft &&
                 value.UpdatedAt < now.AddHours(-options.DraftGenerationHours)) ||
                (value.Status == GenerationStatus.AwaitingPayment &&
                 value.UpdatedAt < now.AddHours(-options.UnpaidGenerationHours)) ||
                (value.Status is GenerationStatus.Completed or GenerationStatus.CompletedWithWarnings &&
                 (value.ExpiresAtUtc ?? value.UpdatedAt.AddDays(options.CompletedGenerationDays)) <= now) ||
                (value.Status == GenerationStatus.Failed &&
                 value.UpdatedAt < now.AddDays(-options.FailedGenerationDays)) ||
                (value.Status == GenerationStatus.Cancelled &&
                 value.UpdatedAt < now.AddDays(-options.CancelledGenerationDays)))
            .ToArray();
        foreach (Generation generation in expired)
        {
            if (generation.Status is GenerationStatus.Completed or GenerationStatus.CompletedWithWarnings)
            {
                GenerationArtifact? final = await db.GenerationArtifacts.SingleOrDefaultAsync(
                    value => value.GenerationId == generation.Id && value.Type == ArtifactType.FinalVideo,
                    cancellationToken);
                if (final is not null && File.Exists(final.StoredPath))
                    File.Delete(final.StoredPath);
                generation.OutputDeletedAtUtc ??= now;
            }
            generation.Status = GenerationStatus.Expired;
            generation.UpdatedAt = now;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private void CleanupIntermediateFiles(Generation generation, DateTimeOffset now)
    {
        string root = storage.GenerationRoot(generation.PublicId);
        storage.EnsureWithinRoot(root);
        generation.CleanupStatus = CleanupStatus.Running;
        generation.CleanupStartedAtUtc ??= now;
        generation.CleanupAttemptCount++;
        try
        {
            long deleted = 0;
            if (Directory.Exists(root))
            {
                foreach (string directory in Directory.EnumerateDirectories(root))
                {
                    DirectoryInfo info = new(directory);
                    if (info.Name.Equals("output", StringComparison.OrdinalIgnoreCase) ||
                        info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                    deleted += DirectorySize(directory);
                    Directory.Delete(directory, recursive: true);
                }
            }
            generation.DeletedTemporaryBytes += deleted;
            metrics.CleanupDeletedBytes.Add(deleted);
            generation.CleanupStatus = CleanupStatus.Completed;
            generation.CleanupCompletedAtUtc = now;
            generation.CleanupError = null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            metrics.CleanupFailures.Add(1);
            generation.CleanupStatus = CleanupStatus.Failed;
            generation.CleanupError = exception.Message[..Math.Min(1024, exception.Message.Length)];
        }
    }

    private static long DirectorySize(string path)
    {
        long total = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                total += new FileInfo(file).Length;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return total;
    }
}
