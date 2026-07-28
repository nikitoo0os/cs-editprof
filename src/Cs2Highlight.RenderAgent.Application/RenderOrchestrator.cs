using System.Diagnostics;
using System.Text.Json;

namespace Cs2Highlight.RenderAgent.Application;

public sealed class RenderOrchestrator(
    RenderEnvironmentOptions environment,
    IEnvironmentVerifier environmentVerifier,
    IWorkspaceManager workspaceManager,
    IDemoCompatibilityRepairer demoCompatibilityRepairer,
    IRenderScriptGenerator scriptGenerator,
    IHlaeLauncher hlaeLauncher,
    IDemoController demoController,
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
        CancellationTokenSource? rendererCancellation = null;
        Task<ProcessExecutionResult>? rendererTask = null;

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
            DemoCompatibilityResult compatibility;
            try
            {
                compatibility = await demoCompatibilityRepairer.RepairAsync(workspace, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return await PersistFailureAsync(
                    job,
                    workspace,
                    startedAt,
                    stopwatch,
                    RenderState.RepairingDemo,
                    "DEMO_COMPATIBILITY_REPAIR_FAILED",
                    exception.Message,
                    false,
                    processes,
                    warnings);
            }
            workspace = workspace with { PreparedDemoPath = compatibility.DemoPath };
            if (compatibility.Repaired)
            {
                warnings.Add("A repaired playback copy was created for the CS2 legacy message 138 compatibility regression.");
            }
            await stateJournal.WriteAsync(
                workspace,
                RenderState.RepairingDemo,
                compatibility.Message,
                cancellationToken);
            GeneratedRenderScript script = await scriptGenerator.GenerateAsync(job, workspace, cancellationToken);
            warnings.AddRange(script.Warnings);
            await stateJournal.WriteAsync(workspace, RenderState.GeneratingScripts, $"Generated {script.Path}.", cancellationToken);

            rendererCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            rendererTask = hlaeLauncher.LaunchAsync(job, workspace, script, rendererCancellation.Token);
            await stateJournal.WriteAsync(
                workspace,
                RenderState.StartingHlae,
                $"Starting HLAE and waiting for CS2 NetCon on port {environment.NetConPort}.",
                cancellationToken);

            Task controlTask = demoController.ControlAsync(job, workspace, cancellationToken);
            Task first = await Task.WhenAny(controlTask, rendererTask);
            if (first == rendererTask)
            {
                ProcessExecutionResult earlyExit = await rendererTask;
                processes = processes with { HlaePid = earlyExit.ProcessId };
                return await PersistFailureAsync(job, workspace, startedAt, stopwatch, RenderState.StartingHlae,
                    "CS2_EXITED",
                    $"HLAE/CS2 exited before demo control completed; code={earlyExit.ExitCode}, timedOut={earlyExit.TimedOut}.",
                    true,
                    processes,
                    warnings);
            }
            try
            {
                await controlTask;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return await PersistFailureAsync(
                    job,
                    workspace,
                    startedAt,
                    stopwatch,
                    RenderState.Recording,
                    "DEMO_CONTROL_FAILED",
                    exception.Message,
                    true,
                    processes,
                    warnings);
            }

            await stateJournal.WriteAsync(workspace, RenderState.VerifyingOutput, "Checking rendered artifact.", cancellationToken);
            var output = await outputWatcher.VerifyAsync(job, workspace, cancellationToken);
            if (!output.Success)
            {
                return await PersistFailureAsync(job, workspace, startedAt, stopwatch, RenderState.VerifyingOutput,
                    "OUTPUT_INVALID", output.Error ?? "Rendered output is invalid.", true, processes, warnings);
            }

            await demoController.QuitAsync(cancellationToken);
            try
            {
                ProcessExecutionResult completed = await rendererTask.WaitAsync(
                    TimeSpan.FromSeconds(environment.ProcessShutdownTimeoutSeconds),
                    cancellationToken);
                processes = processes with { HlaePid = completed.ProcessId };
                if (completed.TimedOut)
                {
                    warnings.Add("HLAE/CS2 required forced cleanup after recording.");
                }
            }
            catch (TimeoutException)
            {
                rendererCancellation.Cancel();
                await ObserveRendererTaskAsync(rendererTask);
                warnings.Add("HLAE/CS2 did not exit after the quit command and was forcefully cleaned up.");
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
        finally
        {
            if (rendererTask is not null && !rendererTask.IsCompleted)
            {
                rendererCancellation?.Cancel();
                await ObserveRendererTaskAsync(rendererTask);
            }
            rendererCancellation?.Dispose();
        }
    }

    private static async Task ObserveRendererTaskAsync(Task<ProcessExecutionResult> rendererTask)
    {
        try
        {
            await rendererTask;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // Cleanup must not replace the original render result with a secondary process-observation error.
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
