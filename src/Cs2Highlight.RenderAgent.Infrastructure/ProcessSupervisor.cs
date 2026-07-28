using System.Diagnostics;
using Cs2Highlight.RenderAgent.Application;

namespace Cs2Highlight.RenderAgent.Infrastructure;

public sealed class ProcessSupervisor : IProcessSupervisor
{
    public async Task<ProcessExecutionResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(request.StandardOutputPath) ?? request.WorkingDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(request.StandardErrorPath) ?? request.WorkingDirectory);
        ProcessStartInfo startInfo = new()
        {
            FileName = request.FileName,
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        if (request.Environment is not null)
        {
            foreach (var variable in request.Environment)
            {
                startInfo.Environment[variable.Key] = variable.Value;
            }
        }

        using Process process = new() { StartInfo = startInfo, EnableRaisingEvents = true };
        Stopwatch stopwatch = Stopwatch.StartNew();
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start {Path.GetFileName(request.FileName)}.");
        }

        int processId = process.Id;
        Task stdout = CopyAsync(process.StandardOutput, request.StandardOutputPath, cancellationToken);
        Task stderr = CopyAsync(process.StandardError, request.StandardErrorPath, cancellationToken);
        using CancellationTokenSource timeout = new(request.Timeout);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            TryKill(process);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        finally
        {
            await Task.WhenAll(stdout, stderr);
        }

        return new ProcessExecutionResult(processId, process.HasExited ? process.ExitCode : -1, timedOut, stopwatch.Elapsed);
    }

    private static async Task CopyAsync(StreamReader reader, string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.Read, 81920, true);
        await using StreamWriter writer = new(stream);
        char[] buffer = new char[4096];
        while (true)
        {
            int read = await reader.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }
            await writer.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}
