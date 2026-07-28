using Cs2Highlight.RenderAgent.Application;

namespace Cs2Highlight.RenderAgent.Infrastructure;

public sealed class RenderOutputWatcher(RenderEnvironmentOptions options, TimeProvider timeProvider) : IRenderOutputWatcher
{
    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4",
        ".avi",
        ".mov"
    };

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
            candidate = Directory.EnumerateFiles(workspace.Raw, "*.*", SearchOption.AllDirectories)
                .Where(path => MediaExtensions.Contains(Path.GetExtension(path)))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (candidate is not null)
            {
                long size = new FileInfo(candidate).Length;
                if (size != previousSize)
                {
                    previousSize = size;
                    stableSince = timeProvider.GetUtcNow();
                }
                else if (size >= options.MinimumOutputBytes &&
                         timeProvider.GetUtcNow() - stableSince >= TimeSpan.FromSeconds(options.OutputStableSeconds))
                {
                    Directory.CreateDirectory(job.OutputDirectory);
                    string destination = Path.Combine(job.OutputDirectory, "raw-highlight" + Path.GetExtension(candidate));
                    File.Copy(candidate, destination, overwrite: false);
                    return (true, destination, size, null);
                }
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250), timeProvider, cancellationToken);
        }
        return (false, candidate, previousSize < 0 ? 0 : previousSize, "No stable non-empty rendered media file appeared before timeout.");
    }
}
