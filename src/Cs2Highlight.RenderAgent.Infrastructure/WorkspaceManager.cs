using Cs2Highlight.RenderAgent.Application;

namespace Cs2Highlight.RenderAgent.Infrastructure;

public sealed class WorkspaceManager(RenderEnvironmentOptions options) : IWorkspaceManager
{
    public async Task<RenderWorkspace> PrepareAsync(RenderJob job, CancellationToken cancellationToken)
    {
        string rootBase = Path.GetFullPath(options.WorkingRoot);
        string root = Path.GetFullPath(Path.Combine(rootBase, job.JobId));
        if (!root.StartsWith(rootBase.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Workspace path escaped WorkingRoot.");
        }

        string input = Create(root, "input");
        string config = Create(root, "config");
        string raw = Create(root, "raw");
        string output = Create(root, "output");
        string logs = Create(root, "logs");
        string state = Create(root, "state");
        string demo = Path.Combine(input, Path.GetFileName(job.DemoPath));
        if (!File.Exists(demo))
        {
            await using FileStream source = File.OpenRead(job.DemoPath);
            await using FileStream target = new(demo, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            await source.CopyToAsync(target, cancellationToken);
        }

        return new RenderWorkspace(root, input, config, raw, output, logs, state, demo);
    }

    private static string Create(string root, string name)
    {
        string path = Path.Combine(root, name);
        Directory.CreateDirectory(path);
        return path;
    }
}
