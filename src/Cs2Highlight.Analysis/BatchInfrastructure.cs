using System.Diagnostics;
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

public sealed class ProcessRenderAgentClient(string renderAgentPath) : IRenderAgentClient
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
        using Process process = new() { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return Failure("RENDER_AGENT_START_FAILED", "Render Agent did not start.", true, -1);
            }
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
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
            string stdout = await stdoutTask;
            string stderr = await stderrTask;
            await File.WriteAllTextAsync(stdoutPath, stdout, Encoding.UTF8, cancellationToken);
            await File.WriteAllTextAsync(stderrPath, stderr, Encoding.UTF8, cancellationToken);
            string resultPath = Path.Combine(job.OutputDirectory, "render-result.json");
            if (!File.Exists(resultPath))
            {
                return Failure(
                    "RENDER_RESULT_NOT_FOUND",
                    $"Render Agent exited with code {process.ExitCode} without render-result.json.",
                    true,
                    process.ExitCode,
                    process.Id,
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
                    "RENDER_RESULT_INVALID", exception.Message, true, process.ExitCode, process.Id, resultPath);
            }
            if (result is null || !string.Equals(result.JobId, job.JobId, StringComparison.Ordinal))
            {
                return Failure(
                    "RENDER_RESULT_MISMATCH",
                    "Render result is empty or belongs to another job.",
                    false,
                    process.ExitCode,
                    process.Id,
                    resultPath);
            }
            if (!result.Success)
            {
                RenderError? error = result.Error;
                return new RenderInvocationResult(
                    process.Id,
                    process.ExitCode,
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
                    process.ExitCode,
                    process.Id,
                    resultPath,
                    result);
            }
            return new RenderInvocationResult(process.Id, process.ExitCode, result, resultPath, null);
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
