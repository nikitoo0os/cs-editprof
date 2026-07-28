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
        string config = Recreate(root, "config");
        string raw = Recreate(root, "raw");
        string output = Recreate(root, "output");
        string logs = Recreate(root, "logs");
        string state = Recreate(root, "state");
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

    private static string Recreate(string root, string name)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        string path = Path.GetFullPath(Path.Combine(fullRoot, name));
        if (!path.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Generated workspace path escaped the job root.");
        }
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        Directory.CreateDirectory(path);
        return path;
    }
}
