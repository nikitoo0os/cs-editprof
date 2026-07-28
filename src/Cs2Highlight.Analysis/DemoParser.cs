using System.Diagnostics;
using System.Text.Json;

namespace Cs2Highlight.Analysis;

public interface IDemoParser
{
    Task<DemoAnalysis> AnalyzeAsync(
        string demoPath,
        string outputPath,
        CancellationToken cancellationToken);
}

public sealed class GoCliDemoParser(
    string executablePath,
    TimeSpan timeout) : IDemoParser
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<DemoAnalysis> AnalyzeAsync(
        string demoPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        string parserPath = Path.GetFullPath(executablePath);
        if (!File.Exists(parserPath))
        {
            throw Error("PARSER_NOT_FOUND", $"Demo parser was not found: {parserPath}", false);
        }

        ProcessStartInfo startInfo = new(parserPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(parserPath)!
        };
        startInfo.ArgumentList.Add("analyze");
        startInfo.ArgumentList.Add("--input");
        startInfo.ArgumentList.Add(Path.GetFullPath(demoPath));
        startInfo.ArgumentList.Add("--output");
        startInfo.ArgumentList.Add(Path.GetFullPath(outputPath));
        startInfo.ArgumentList.Add("--pretty");
        startInfo.ArgumentList.Add("--log-file");
        startInfo.ArgumentList.Add(Path.Combine(Path.GetDirectoryName(outputPath)!, "logs", "demo-parser.log"));

        using Process process = new() { StartInfo = startInfo };
        if (!process.Start())
        {
            throw Error("PARSER_FAILED", "Demo parser process did not start.", true);
        }
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using CancellationTokenSource timeoutSource = new(timeout);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested)
        {
            Kill(process);
            throw Error("PARSER_TIMEOUT", $"Demo parser exceeded timeout {timeout}.", true);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }

        string stdout = await stdoutTask;
        string stderr = await stderrTask;
        if (process.ExitCode != 0)
        {
            throw Error(
                "PARSER_FAILED",
                $"Demo parser exited with code {process.ExitCode}: {Trim(stderr)}",
                process.ExitCode is 12 or 20 or 99);
        }
        if (!File.Exists(outputPath))
        {
            throw Error("PARSER_FAILED", $"Demo parser succeeded without creating {outputPath}.", true);
        }

        try
        {
            await using FileStream stream = File.OpenRead(outputPath);
            DemoAnalysis? analysis = await JsonSerializer.DeserializeAsync<DemoAnalysis>(
                stream,
                JsonOptions,
                cancellationToken);
            return analysis ?? throw new JsonException("Analysis JSON was empty.");
        }
        catch (JsonException exception)
        {
            throw Error(
                "INVALID_ANALYSIS_JSON",
                $"Parser output is invalid JSON. stdout={Trim(stdout)}",
                false,
                exception);
        }
    }

    private static AnalysisException Error(
        string code,
        string message,
        bool retryable,
        Exception? exception = null) =>
        new(new AnalysisError(code, message, AnalysisStage.RunningDemoParser, retryable, exception));

    private static void Kill(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }
    }

    private static string Trim(string value) =>
        value.Length <= 2000 ? value.Trim() : value[..2000].Trim() + "…";
}
