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
                ["demoParser"] = ResolveExecutable(pipeline.DemoParserPath) is not null,
                ["renderAgent"] = ResolveExecutable(pipeline.RenderAgentPath) is not null,
                ["ffmpeg"] = ResolveExecutable(pipeline.FfmpegPath) is not null,
                ["ffprobe"] = ResolveExecutable(pipeline.FfprobePath) is not null
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

    private static string? ResolveExecutable(string configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) return null;
        string full = Path.GetFullPath(configured);
        if (File.Exists(full)) return full;
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(directory.Trim(), configured);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}

public sealed class GenerationCleanupService(
    IDbContextFactory<GenerationDbContext> dbFactory,
    GenerationStorage storage,
    RetentionOptions options,
    TimeProvider timeProvider,
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
        Generation[] readyForDeletion = await db.Generations
            .Where(value => value.Status == GenerationStatus.Expired &&
                value.UpdatedAt < now.AddMinutes(-Math.Max(1, options.CleanupIntervalMinutes)))
            .ToArrayAsync(cancellationToken);
        foreach (Generation generation in readyForDeletion)
        {
            string root = storage.GenerationRoot(generation.PublicId);
            storage.EnsureWithinRoot(root);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        Generation[] expired = await db.Generations.Where(value =>
            (value.Status == GenerationStatus.Draft &&
             value.UpdatedAt < now.AddHours(-options.DraftGenerationHours)) ||
            (value.Status == GenerationStatus.AwaitingPayment &&
             value.UpdatedAt < now.AddHours(-options.UnpaidGenerationHours)) ||
            (value.Status == GenerationStatus.Completed &&
             value.UpdatedAt < now.AddDays(-options.CompletedGenerationDays)) ||
            (value.Status == GenerationStatus.CompletedWithWarnings &&
             value.UpdatedAt < now.AddDays(-options.CompletedGenerationDays)) ||
            (value.Status == GenerationStatus.Failed &&
             value.UpdatedAt < now.AddDays(-options.FailedGenerationDays)) ||
            (value.Status == GenerationStatus.Cancelled &&
             value.UpdatedAt < now.AddDays(-options.CancelledGenerationDays)))
            .ToArrayAsync(cancellationToken);
        foreach (Generation generation in expired)
        {
            generation.Status = GenerationStatus.Expired;
            generation.UpdatedAt = now;
        }
        await db.SaveChangesAsync(cancellationToken);
    }
}
