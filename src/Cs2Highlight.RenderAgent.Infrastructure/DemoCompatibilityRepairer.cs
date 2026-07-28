using System.Diagnostics;
using System.Text;
using Cs2Highlight.RenderAgent.Application;

namespace Cs2Highlight.RenderAgent.Infrastructure;

public sealed class DemoCompatibilityRepairer(RenderEnvironmentOptions options) : IDemoCompatibilityRepairer
{
    public const string BundledExecutableName = "cs2-demo-playback-fix.exe";

    public async Task<DemoCompatibilityResult> RepairAsync(
        RenderWorkspace workspace,
        CancellationToken cancellationToken)
    {
        string executable = ResolveExecutablePath(options);
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("The bundled CS2 demo compatibility repair tool was not found.", executable);
        }

        string input = Path.GetFullPath(workspace.PreparedDemoPath);
        string output = GetOutputPath(input);
        if (File.Exists(output))
        {
            File.Delete(output);
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = executable,
            WorkingDirectory = workspace.Input,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--output");
        startInfo.ArgumentList.Add(output);
        startInfo.ArgumentList.Add(input);

        using Process process = new() { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start the CS2 demo compatibility repair tool.");
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
                process.Kill(entireProcessTree: true);
            }
            throw;
        }
        string stdout = (await stdoutTask).Trim();
        string stderr = (await stderrTask).Trim();
        await WriteLogAsync(workspace, stdout, stderr, cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"CS2 demo compatibility repair failed with exit code {process.ExitCode}: {stderr}");
        }

        if (File.Exists(output))
        {
            return new DemoCompatibilityResult(output, true, stdout);
        }

        if (stdout.StartsWith("CLEAN ", StringComparison.Ordinal))
        {
            return new DemoCompatibilityResult(input, false, stdout);
        }

        throw new InvalidOperationException(
            "CS2 demo compatibility repair completed without producing a repaired file or a CLEAN result.");
    }

    public static string ResolveExecutablePath(RenderEnvironmentOptions environment) =>
        string.IsNullOrWhiteSpace(environment.DemoRepairExecutablePath)
            ? Path.Combine(AppContext.BaseDirectory, "tools", BundledExecutableName)
            : Path.GetFullPath(environment.DemoRepairExecutablePath);

    public static string GetOutputPath(string input)
    {
        string? directory = Path.GetDirectoryName(input);
        string stem = Path.GetFileNameWithoutExtension(input);
        return Path.Combine(directory ?? string.Empty, $"{stem}_safe138.dem");
    }

    private static Task WriteLogAsync(
        RenderWorkspace workspace,
        string stdout,
        string stderr,
        CancellationToken cancellationToken)
    {
        StringBuilder log = new();
        if (!string.IsNullOrWhiteSpace(stdout))
        {
            log.AppendLine(stdout);
        }
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            log.AppendLine(stderr);
        }
        return File.WriteAllTextAsync(
            Path.Combine(workspace.Logs, "demo-compatibility-repair.log"),
            log.ToString(),
            new UTF8Encoding(false),
            cancellationToken);
    }
}
