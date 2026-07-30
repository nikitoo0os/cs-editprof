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

        HashSet<int> existingTrackedProcessIds =
            GetProcessIds(request.TrackedProcessName);
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
        Process? trackedProcess = null;
        int? trackedProcessId = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(request.TrackedProcessName))
            {
                trackedProcess = await WaitForTrackedProcessAsync(
                    request.TrackedProcessName,
                    existingTrackedProcessIds,
                    request.TrackedProcessStartupTimeout ??
                        TimeSpan.FromSeconds(30),
                    linked.Token);
                trackedProcessId = trackedProcess?.Id;
            }

            if (trackedProcess is not null)
                await trackedProcess.WaitForExitAsync(linked.Token);
            else
                await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            if (trackedProcess is not null)
                TryKill(trackedProcess);
            TryKill(process);
        }
        catch (OperationCanceledException)
        {
            if (trackedProcess is not null)
                TryKill(trackedProcess);
            TryKill(process);
            throw;
        }
        finally
        {
            trackedProcess?.Dispose();
            await Task.WhenAll(stdout, stderr);
        }

        int exitCode = trackedProcess is null && process.HasExited
            ? process.ExitCode
            : 0;
        return new ProcessExecutionResult(
            processId,
            exitCode,
            timedOut,
            stopwatch.Elapsed,
            trackedProcessId);
    }

    private static async Task<Process?> WaitForTrackedProcessAsync(
        string processName,
        HashSet<int> existingProcessIds,
        TimeSpan startupTimeout,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(startupTimeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (Process candidate in Process.GetProcessesByName(processName))
            {
                if (!existingProcessIds.Contains(candidate.Id) &&
                    !candidate.HasExited)
                {
                    return candidate;
                }
                candidate.Dispose();
            }
            await Task.Delay(100, cancellationToken);
        }

        return null;
    }

    private static HashSet<int> GetProcessIds(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
            return [];

        HashSet<int> result = [];
        foreach (Process process in Process.GetProcessesByName(processName))
        {
            result.Add(process.Id);
            process.Dispose();
        }
        return result;
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
                process.WaitForExit(5000);
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            System.ComponentModel.Win32Exception or
            NotSupportedException)
        {
        }
    }
}
