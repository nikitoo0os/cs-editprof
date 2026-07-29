using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Cs2Highlight.RenderAgent.Application;

namespace Cs2Highlight.Analysis;

public sealed class JsonBatchStateStore : IBatchStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public async Task SaveAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporaryPath = $"{fullPath}.{Environment.ProcessId}.tmp";
        try
        {
            await using (FileStream stream = new(
                             temporaryPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporaryPath, fullPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public async Task<T> LoadAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            Path.GetFullPath(path),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken) ??
            throw new InvalidDataException($"JSON document is empty: {path}");
    }
}

public sealed class ProcessRenderAgentClient(string renderAgentPath) : ISessionRenderAgentClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<RenderInvocationResult> RenderAsync(
        string renderJobPath,
        int attempt,
        CancellationToken cancellationToken)
    {
        string executable = Path.GetFullPath(renderAgentPath);
        if (!File.Exists(executable))
        {
            return Failure("RENDER_AGENT_NOT_FOUND", $"Render Agent was not found: {executable}", false, -1);
        }
        RenderJob job;
        try
        {
            await using FileStream jobStream = File.OpenRead(renderJobPath);
            job = await JsonSerializer.DeserializeAsync<RenderJob>(
                jobStream,
                JsonOptions,
                cancellationToken) ?? throw new JsonException("Render job is empty.");
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return Failure("INVALID_RENDER_JOB", exception.Message, false, -1);
        }

        string logs = Path.Combine(job.OutputDirectory, "logs");
        Directory.CreateDirectory(logs);
        if (attempt > 1)
        {
            ArchivePreviousAttempt(job.OutputDirectory, logs, attempt - 1);
        }
        string stdoutPath = Path.Combine(logs, $"render-agent-attempt-{attempt:D2}.stdout.log");
        string stderrPath = Path.Combine(logs, $"render-agent-attempt-{attempt:D2}.stderr.log");
        ProcessStartInfo startInfo = new()
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("render");
        startInfo.ArgumentList.Add("--job");
        startInfo.ArgumentList.Add(Path.GetFullPath(renderJobPath));
        if (attempt > 1)
        {
            startInfo.Environment[
                "CS2RENDER_RenderEnvironment__Warmup__WarmupGameSeconds"] =
                (3 + (attempt - 1) * 2).ToString(CultureInfo.InvariantCulture);
        }
        using Process process = new() { StartInfo = startInfo };
        try
        {
            Console.WriteLine(
                $"[RenderAgent] Starting job {job.JobId}, attempt {attempt}: " +
                $"ticks {job.Segment.StartTick}-{job.Segment.EndTick}");
            if (!process.Start())
            {
                return Failure("RENDER_AGENT_START_FAILED", "Render Agent did not start.", true, -1);
            }
            Task stdoutTask = PumpOutputAsync(
                process.StandardOutput,
                stdoutPath,
                Console.Out,
                "[RenderAgent] ",
                cancellationToken);
            Task stderrTask = PumpOutputAsync(
                process.StandardError,
                stderrPath,
                Console.Error,
                "[RenderAgent:stderr] ",
                cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
                throw;
            }
            await Task.WhenAll(stdoutTask, stderrTask);
            Console.WriteLine(
                $"[RenderAgent] Job {job.JobId}, attempt {attempt} exited with code {process.ExitCode}.");
            return await ReadResultAsync(
                job,
                process.Id,
                process.ExitCode,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Failure("TEMPORARY_PROCESS_FAILURE", exception.Message, true, -1);
        }
    }

    public async Task<IReadOnlyList<RenderInvocationResult>> RenderBatchAsync(
        IReadOnlyList<RenderBatchItemRequest> items,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
            return [];
        string executable = Path.GetFullPath(renderAgentPath);
        if (!File.Exists(executable))
        {
            return items.Select(_ => Failure(
                    "RENDER_AGENT_NOT_FOUND",
                    $"Render Agent was not found: {executable}",
                    false,
                    -1))
                .ToArray();
        }

        List<RenderJob> jobs = new(items.Count);
        try
        {
            foreach (RenderBatchItemRequest item in items)
            {
                await using FileStream jobStream = File.OpenRead(item.RenderJobPath);
                RenderJob job = await JsonSerializer.DeserializeAsync<RenderJob>(
                        jobStream,
                        JsonOptions,
                        cancellationToken) ??
                    throw new JsonException($"Render job is empty: {item.RenderJobPath}");
                jobs.Add(job);
                string logs = Path.Combine(job.OutputDirectory, "logs");
                Directory.CreateDirectory(logs);
                if (item.Attempt > 1)
                    ArchivePreviousAttempt(job.OutputDirectory, logs, item.Attempt - 1);
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return items.Select(_ =>
                    Failure("INVALID_RENDER_JOB", exception.Message, false, -1))
                .ToArray();
        }

        string sessionId = Guid.NewGuid().ToString("N");
        string sessionLogs = Path.Combine(
            Path.GetDirectoryName(jobs[0].OutputDirectory)!,
            "logs");
        Directory.CreateDirectory(sessionLogs);
        string manifestPath = Path.Combine(
            sessionLogs,
            $"render-session-{sessionId}.json");
        string stdoutPath = Path.Combine(
            sessionLogs,
            $"render-session-{sessionId}.stdout.log");
        string stderrPath = Path.Combine(
            sessionLogs,
            $"render-session-{sessionId}.stderr.log");
        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(
                new RenderBatchManifest(
                    items.Select(value =>
                            Path.GetFullPath(value.RenderJobPath))
                        .ToArray()),
                JsonOptions),
            new UTF8Encoding(false),
            cancellationToken);

        ProcessStartInfo startInfo = new()
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("render-batch");
        startInfo.ArgumentList.Add("--manifest");
        startInfo.ArgumentList.Add(manifestPath);
        int maximumAttempt = items.Max(value => value.Attempt);
        if (maximumAttempt > 1)
        {
            startInfo.Environment[
                "CS2RENDER_RenderEnvironment__Warmup__WarmupGameSeconds"] =
                (3 + (maximumAttempt - 1) * 2)
                .ToString(CultureInfo.InvariantCulture);
        }
        using Process process = new() { StartInfo = startInfo };
        try
        {
            Console.WriteLine(
                $"[RenderAgent] Starting one CS2 session for {jobs.Count} highlight clips.");
            if (!process.Start())
            {
                return jobs.Select(_ => Failure(
                        "RENDER_AGENT_START_FAILED",
                        "Render Agent did not start.",
                        true,
                        -1))
                    .ToArray();
            }
            Task stdoutTask = PumpOutputAsync(
                process.StandardOutput,
                stdoutPath,
                Console.Out,
                "[RenderSession] ",
                cancellationToken);
            Task stderrTask = PumpOutputAsync(
                process.StandardError,
                stderrPath,
                Console.Error,
                "[RenderSession:stderr] ",
                cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
                throw;
            }
            await Task.WhenAll(stdoutTask, stderrTask);
            Console.WriteLine(
                $"[RenderAgent] Shared session exited with code {process.ExitCode}.");
            List<RenderInvocationResult> results = new(jobs.Count);
            foreach (RenderJob job in jobs)
            {
                results.Add(await ReadResultAsync(
                    job,
                    process.Id,
                    process.ExitCode,
                    cancellationToken));
            }
            return results;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return jobs.Select(_ => Failure(
                    "TEMPORARY_PROCESS_FAILURE",
                    exception.Message,
                    true,
                    -1))
                .ToArray();
        }
    }

    private static async Task<RenderInvocationResult> ReadResultAsync(
        RenderJob job,
        int processId,
        int exitCode,
        CancellationToken cancellationToken)
    {
        string resultPath = Path.Combine(job.OutputDirectory, "render-result.json");
        if (!File.Exists(resultPath))
        {
            return Failure(
                "RENDER_RESULT_NOT_FOUND",
                $"Render Agent exited with code {exitCode} without render-result.json.",
                true,
                exitCode,
                processId,
                resultPath);
        }
        RenderResult? result;
        try
        {
            await using FileStream resultStream = File.OpenRead(resultPath);
            result = await JsonSerializer.DeserializeAsync<RenderResult>(
                resultStream,
                JsonOptions,
                cancellationToken);
        }
        catch (JsonException exception)
        {
            return Failure(
                "RENDER_RESULT_INVALID",
                exception.Message,
                true,
                exitCode,
                processId,
                resultPath);
        }
        if (result is null ||
            !string.Equals(result.JobId, job.JobId, StringComparison.Ordinal))
        {
            return Failure(
                "RENDER_RESULT_MISMATCH",
                "Render result is empty or belongs to another job.",
                false,
                exitCode,
                processId,
                resultPath);
        }
        if (!result.Success)
        {
            RenderError? error = result.Error;
            return new RenderInvocationResult(
                processId,
                exitCode,
                result,
                resultPath,
                new BatchItemError(
                    error?.Code ?? "RENDER_FAILED",
                    error?.Message ?? "Render Agent reported failure.",
                    error?.Retryable ?? false));
        }
        if (string.IsNullOrWhiteSpace(result.OutputFile) ||
            !File.Exists(result.OutputFile) ||
            new FileInfo(result.OutputFile).Length == 0)
        {
            return Failure(
                "RENDER_OUTPUT_INVALID",
                "Render result does not reference a non-empty output file.",
                true,
                exitCode,
                processId,
                resultPath,
                result);
        }
        return new RenderInvocationResult(
            processId,
            exitCode,
            result,
            resultPath,
            null);
    }

    private static async Task PumpOutputAsync(
        StreamReader reader,
        string path,
        TextWriter terminal,
        string prefix,
        CancellationToken cancellationToken)
    {
        await using StreamWriter log = new(
            new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                16 * 1024,
                FileOptions.Asynchronous),
            new UTF8Encoding(false))
        {
            AutoFlush = true
        };
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            await log.WriteLineAsync(line.AsMemory(), cancellationToken);
            await terminal.WriteLineAsync($"{prefix}{line}");
        }
    }

    private static RenderInvocationResult Failure(
        string code,
        string message,
        bool retryable,
        int exitCode,
        int? processId = null,
        string renderResultPath = "",
        RenderResult? result = null) =>
        new(processId, exitCode, result, renderResultPath, new BatchItemError(code, message, retryable));

    private static void ArchivePreviousAttempt(string outputDirectory, string logs, int attempt)
    {
        string resultPath = Path.Combine(outputDirectory, "render-result.json");
        if (File.Exists(resultPath))
        {
            File.Move(
                resultPath,
                Path.Combine(logs, $"render-result-attempt-{attempt:D2}.json"),
                true);
        }
        string outputPath = Path.Combine(outputDirectory, "raw-highlight.mp4");
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }
    }
}

public sealed class BatchRenderLock : IAsyncDisposable
{
    private readonly Semaphore semaphore = new(1, 1, @"Local\Cs2Highlight.BatchRenderer");
    public bool Acquired { get; }

    public BatchRenderLock() => Acquired = semaphore.WaitOne(TimeSpan.Zero);

    public ValueTask DisposeAsync()
    {
        if (Acquired) semaphore.Release();
        semaphore.Dispose();
        return ValueTask.CompletedTask;
    }
}
