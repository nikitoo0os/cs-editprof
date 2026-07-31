using Cs2Highlight.RenderAgent.Application;
using System.Text.Json;

namespace Cs2Highlight.RenderAgent.Infrastructure;

public sealed class WorkspaceManager(RenderEnvironmentOptions options) : IWorkspaceManager
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };

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
        await SynchronizeDemoAsync(job.DemoPath, demo, cancellationToken);

        return new RenderWorkspace(root, input, config, raw, output, logs, state, demo);
    }

    public async Task<bool> DeleteCompletedAsync(
        RenderWorkspace workspace,
        CancellationToken cancellationToken)
    {
        string rootBase = Path.GetFullPath(options.WorkingRoot)
            .TrimEnd(Path.DirectorySeparatorChar);
        string root = Path.GetFullPath(workspace.Root)
            .TrimEnd(Path.DirectorySeparatorChar);
        if (!root.StartsWith(
                rootBase + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Workspace cleanup path escaped WorkingRoot.");
        }

        string resultPath = Path.Combine(root, "state", "render-result.json");
        if (!File.Exists(resultPath))
            return false;
        RenderResult? result;
        await using (FileStream stream = new(
                         resultPath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         16 * 1024,
                         FileOptions.Asynchronous |
                         FileOptions.SequentialScan))
        {
            result = await JsonSerializer.DeserializeAsync<RenderResult>(
                stream,
                JsonOptions,
                cancellationToken);
        }
        if (result?.Success != true ||
            string.IsNullOrWhiteSpace(result.OutputFile) ||
            !File.Exists(result.OutputFile))
        {
            return false;
        }

        string persistedOutput = Path.GetFullPath(result.OutputFile);
        if (persistedOutput.StartsWith(
                root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Directory.Delete(root, recursive: true);
        return true;
    }

    private static async Task SynchronizeDemoAsync(
        string sourcePath,
        string preparedPath,
        CancellationToken cancellationToken)
    {
        string source = Path.GetFullPath(sourcePath);
        string prepared = Path.GetFullPath(preparedPath);
        if (string.Equals(
                source,
                prepared,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string directory = Path.GetDirectoryName(prepared) ??
            throw new InvalidOperationException(
                "Prepared demo path does not have a directory.");
        string staged = Path.Combine(
            directory,
            $".{Path.GetFileName(prepared)}.{Guid.NewGuid():N}.incoming");
        long sourceLength;
        try
        {
            await using (FileStream input = new(
                source,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous |
                FileOptions.SequentialScan))
            {
                sourceLength = input.Length;
                await using FileStream output = new(
                    staged,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    1024 * 1024,
                    FileOptions.Asynchronous |
                    FileOptions.SequentialScan);
                await input.CopyToAsync(
                    output,
                    1024 * 1024,
                    cancellationToken);
                await output.FlushAsync(cancellationToken);
            }

            long stagedLength = new FileInfo(staged).Length;
            if (stagedLength != sourceLength)
            {
                throw new IOException(
                    $"Prepared demo copy is incomplete: expected " +
                    $"{sourceLength} bytes, copied {stagedLength} bytes.");
            }

            File.Move(staged, prepared, overwrite: true);
            long preparedLength = new FileInfo(prepared).Length;
            if (preparedLength != sourceLength)
            {
                throw new IOException(
                    $"Prepared demo verification failed: expected " +
                    $"{sourceLength} bytes, found {preparedLength} bytes.");
            }
        }
        finally
        {
            if (File.Exists(staged))
                File.Delete(staged);
        }
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
