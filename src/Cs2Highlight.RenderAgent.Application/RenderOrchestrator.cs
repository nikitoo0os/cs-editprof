using System.Diagnostics;
using System.Text.Json;

namespace Cs2Highlight.RenderAgent.Application;

public sealed class RenderOrchestrator(
    RenderEnvironmentOptions environment,
    IEnvironmentVerifier environmentVerifier,
    IWorkspaceManager workspaceManager,
    IRenderScriptGenerator scriptGenerator,
    IHlaeLauncher hlaeLauncher,
    IRenderOutputWatcher outputWatcher,
    IRenderLockFactory lockFactory,
    IStateJournal stateJournal,
    TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<(RenderResult Result, int ExitCode)> RunAsync(RenderJob job, CancellationToken cancellationToken)
    {
        DateTimeOffset startedAt = timeProvider.GetUtcNow();
        Stopwatch stopwatch = Stopwatch.StartNew();
        RenderWorkspace? workspace = null;
        ProcessIdentifiers processes = new();
        List<string> warnings = [];

        try
        {
            ValidationReport validation = RenderJobValidator.Validate(job, environment);
            if (!validation.IsValid)
            {
                return Failure(job, startedAt, stopwatch, RenderState.Validating, "INVALID_RENDER_JOB",
                    string.Join(Environment.NewLine, validation.Errors), false, processes, warnings);
            }

            await using IRenderLock renderLock = lockFactory.TryAcquire();
            if (!renderLock.Acquired)
            {
                return Failure(job, startedAt, stopwatch, RenderState.EnvironmentChecking, "RENDERER_BUSY",
                    "Another render job owns the global render lock.", true, processes, warnings);
            }

            EnvironmentReport report = await environmentVerifier.VerifyAsync(job, cancellationToken);
            if (!report.Success)
            {
                string message = string.Join(Environment.NewLine, report.Checks
                    .Where(check => !check.Success)
                    .Select(check => $"{check.Name}: {check.Message}"));
                string code = report.Checks.Any(check => check.Name == "AutomationVerified" && !check.Success)
                    ? "HLAE_AUTOMATION_UNCONFIRMED"
                    : "ENVIRONMENT_INVALID";
                return Failure(job, startedAt, stopwatch, RenderState.EnvironmentChecking, code, message, false, processes, warnings);
            }

            workspace = await workspaceManager.PrepareAsync(job, cancellationToken);
            await stateJournal.WriteAsync(workspace, RenderState.PreparingWorkspace, "Workspace prepared.", cancellationToken);
            GeneratedRenderScript script = await scriptGenerator.GenerateAsync(job, workspace, cancellationToken);
            warnings.AddRange(script.Warnings);
            await stateJournal.WriteAsync(workspace, RenderState.GeneratingScripts, $"Generated {script.Path}.", cancellationToken);

            ProcessExecutionResult hlae = await hlaeLauncher.LaunchAsync(workspace, script, cancellationToken);
            processes = processes with { HlaePid = hlae.ProcessId };
            if (hlae.TimedOut || hlae.ExitCode != 0)
            {
                return await PersistFailureAsync(job, workspace, startedAt, stopwatch, RenderState.StartingHlae,
                    "HLAE_LAUNCH_FAILED", $"HLAE exited with code {hlae.ExitCode}; timedOut={hlae.TimedOut}.", true, processes, warnings);
            }

            await stateJournal.WriteAsync(workspace, RenderState.VerifyingOutput, "Checking rendered artifact.", cancellationToken);
            var output = await outputWatcher.VerifyAsync(job, workspace, cancellationToken);
            if (!output.Success)
            {
                return await PersistFailureAsync(job, workspace, startedAt, stopwatch, RenderState.VerifyingOutput,
                    "OUTPUT_INVALID", output.Error ?? "Rendered output is invalid.", true, processes, warnings);
            }

            RenderResult success = new(job.JobId, true, RenderState.Completed, output.File, output.Size,
                stopwatch.ElapsedMilliseconds, startedAt, timeProvider.GetUtcNow(), processes, warnings, null);
            await stateJournal.WriteAsync(workspace, RenderState.Completed, "Render completed.", cancellationToken);
            await PersistResultAsync(workspace, job.OutputDirectory, success, cancellationToken);
            return (success, ExitCodes.Success);
        }
        catch (OperationCanceledException)
        {
            var cancelled = Failure(job, startedAt, stopwatch, RenderState.Cancelled, "CANCELLED",
                "Render job was cancelled.", true, processes, warnings);
            if (workspace is not null)
            {
                await PersistResultAsync(workspace, job.OutputDirectory, cancelled.Result, CancellationToken.None);
            }
            return cancelled;
        }
        catch (Exception exception)
        {
            var unexpected = Failure(job, startedAt, stopwatch, RenderState.Failed, "INTERNAL_ERROR",
                exception.Message, false, processes, warnings, exception);
            if (workspace is not null)
            {
                await PersistResultAsync(workspace, job.OutputDirectory, unexpected.Result, CancellationToken.None);
            }
            return unexpected;
        }
    }

    private async Task<(RenderResult Result, int ExitCode)> PersistFailureAsync(
        RenderJob job,
        RenderWorkspace workspace,
        DateTimeOffset startedAt,
        Stopwatch stopwatch,
        RenderState stage,
        string code,
        string message,
        bool retryable,
        ProcessIdentifiers processes,
        IReadOnlyList<string> warnings)
    {
        var failure = Failure(job, startedAt, stopwatch, stage, code, message, retryable, processes, warnings);
        await stateJournal.WriteAsync(workspace, RenderState.Failed, message, CancellationToken.None);
        await PersistResultAsync(workspace, job.OutputDirectory, failure.Result, CancellationToken.None);
        return failure;
    }

    private (RenderResult Result, int ExitCode) Failure(
        RenderJob job,
        DateTimeOffset startedAt,
        Stopwatch stopwatch,
        RenderState stage,
        string code,
        string message,
        bool retryable,
        ProcessIdentifiers processes,
        IReadOnlyList<string> warnings,
        Exception? exception = null)
    {
        RenderError error = new(code, message, stage, retryable, exception?.ToString());
        RenderResult result = new(job.JobId, false, stage == RenderState.Cancelled ? RenderState.Cancelled : RenderState.Failed,
            null, null, stopwatch.ElapsedMilliseconds, startedAt, timeProvider.GetUtcNow(), processes, warnings, error);
        return (result, ExitCodes.FromError(error));
    }

    private static async Task PersistResultAsync(
        RenderWorkspace workspace,
        string outputDirectory,
        RenderResult result,
        CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(result, JsonOptions);
        await File.WriteAllTextAsync(Path.Combine(workspace.State, "render-result.json"), json, cancellationToken);
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "render-result.json"), json, cancellationToken);
    }
}
