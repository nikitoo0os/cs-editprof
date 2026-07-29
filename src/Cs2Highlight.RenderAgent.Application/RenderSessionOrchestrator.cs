using System.Diagnostics;
using System.Text.Json;

namespace Cs2Highlight.RenderAgent.Application;

public sealed class RenderSessionOrchestrator(
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
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<IReadOnlyList<RenderJobOutcome>> RunAsync(
        IReadOnlyList<RenderJob> jobs,
        CancellationToken cancellationToken)
    {
        if (jobs.Count == 0)
            return [];

        RenderJobOutcome?[] outcomes = new RenderJobOutcome?[jobs.Count];
        List<PreparedRender> prepared = [];
        await using IRenderLock renderLock = lockFactory.TryAcquire();
        if (!renderLock.Acquired)
        {
            for (int index = 0; index < jobs.Count; index++)
            {
                outcomes[index] = await FailBeforeWorkspaceAsync(
                    jobs[index],
                    RenderState.EnvironmentChecking,
                    "RENDERER_BUSY",
                    "Another render job owns the global render lock.",
                    true,
                    cancellationToken);
            }
            return Complete(outcomes);
        }

        for (int index = 0; index < jobs.Count; index++)
        {
            RenderJob job = jobs[index];
            DateTimeOffset startedAt = timeProvider.GetUtcNow();
            Stopwatch stopwatch = Stopwatch.StartNew();
            List<string> warnings = [];
            ValidationReport validation = RenderJobValidator.Validate(job, environment);
            if (!validation.IsValid)
            {
                outcomes[index] = await FailBeforeWorkspaceAsync(
                    job,
                    RenderState.Validating,
                    "INVALID_RENDER_JOB",
                    string.Join(Environment.NewLine, validation.Errors),
                    false,
                    cancellationToken,
                    startedAt,
                    stopwatch);
                continue;
            }

            EnvironmentReport report = await environmentVerifier.VerifyAsync(job, cancellationToken);
            if (!report.Success)
            {
                string message = string.Join(
                    Environment.NewLine,
                    report.Checks
                        .Where(check => !check.Success)
                        .Select(check => $"{check.Name}: {check.Message}"));
                string code = report.Checks.Any(check =>
                        check.Name == "AutomationVerified" && !check.Success)
                    ? "HLAE_AUTOMATION_UNCONFIRMED"
                    : "ENVIRONMENT_INVALID";
                outcomes[index] = await FailBeforeWorkspaceAsync(
                    job,
                    RenderState.EnvironmentChecking,
                    code,
                    message,
                    false,
                    cancellationToken,
                    startedAt,
                    stopwatch);
                continue;
            }

            try
            {
                RenderWorkspace workspace =
                    await workspaceManager.PrepareAsync(job, cancellationToken);
                await stateJournal.WriteAsync(
                    workspace,
                    RenderState.PreparingWorkspace,
                    "Workspace prepared for shared CS2 render session.",
                    cancellationToken);
                DemoCompatibilityResult compatibility =
                    await demoCompatibilityRepairer.RepairAsync(workspace, cancellationToken);
                workspace = workspace with { PreparedDemoPath = compatibility.DemoPath };
                if (compatibility.Repaired)
                {
                    warnings.Add(
                        "A repaired playback copy was created for the CS2 legacy message 138 compatibility regression.");
                }
                await stateJournal.WriteAsync(
                    workspace,
                    RenderState.RepairingDemo,
                    compatibility.Message,
                    cancellationToken);
                GeneratedRenderScript script =
                    await scriptGenerator.GenerateAsync(job, workspace, cancellationToken);
                warnings.AddRange(script.Warnings);
                await stateJournal.WriteAsync(
                    workspace,
                    RenderState.GeneratingScripts,
                    $"Generated {script.Path}.",
                    cancellationToken);
                prepared.Add(new PreparedRender(
                    index,
                    job,
                    workspace,
                    script,
                    startedAt,
                    stopwatch,
                    warnings));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                outcomes[index] = await FailBeforeWorkspaceAsync(
                    job,
                    RenderState.PreparingWorkspace,
                    "RENDER_PREPARATION_FAILED",
                    exception.Message,
                    true,
                    cancellationToken,
                    startedAt,
                    stopwatch);
            }
        }

        if (prepared.Count == 0)
            return Complete(outcomes);

        PreparedRender first = prepared[0];
        int sessionTimeout = (int)Math.Min(
            86400L,
            prepared.Sum(value => (long)value.Job.TimeoutSeconds));
        RenderJob sessionJob = first.Job with { TimeoutSeconds = Math.Max(1, sessionTimeout) };
        using CancellationTokenSource rendererCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<ProcessExecutionResult> rendererTask = hlaeLauncher.LaunchAsync(
            sessionJob,
            first.Workspace,
            first.Script,
            rendererCancellation.Token);
        ProcessIdentifiers processes = new();

        try
        {
            string? loadedDemoPath = null;
            for (int preparedIndex = 0; preparedIndex < prepared.Count; preparedIndex++)
            {
                PreparedRender item = prepared[preparedIndex];
                string sourceDemoPath = Path.GetFullPath(item.Job.DemoPath);
                DemoLoadMode loadMode =
                    loadedDemoPath is not null &&
                    string.Equals(
                        loadedDemoPath,
                        sourceDemoPath,
                        StringComparison.OrdinalIgnoreCase)
                        ? DemoLoadMode.ReuseCurrent
                        : DemoLoadMode.Start;
                await stateJournal.WriteAsync(
                    item.Workspace,
                    RenderState.StartingHlae,
                    preparedIndex == 0
                        ? $"Starting one shared HLAE/CS2 session for {prepared.Count} clips."
                        : loadMode == DemoLoadMode.ReuseCurrent
                            ? $"Reusing the already loaded demo for clip {preparedIndex + 1}/{prepared.Count}; the map will not be reloaded."
                            : $"Loading a different demo in the active HLAE/CS2 session for clip {preparedIndex + 1}/{prepared.Count}.",
                    cancellationToken);
                Task controlTask = demoController.ControlAsync(
                    item.Job,
                    item.Workspace,
                    loadMode,
                    cancellationToken);
                Task firstCompleted = await Task.WhenAny(controlTask, rendererTask);
                if (firstCompleted == rendererTask)
                {
                    ProcessExecutionResult earlyExit = await rendererTask;
                    processes = processes with { HlaePid = earlyExit.ProcessId };
                    outcomes[item.Index] = await FailAsync(
                        item,
                        RenderState.StartingHlae,
                        "CS2_EXITED",
                        $"HLAE/CS2 exited during shared render session; code={earlyExit.ExitCode}, timedOut={earlyExit.TimedOut}.",
                        true,
                        processes);
                    for (int remaining = preparedIndex + 1;
                         remaining < prepared.Count;
                         remaining++)
                    {
                        outcomes[prepared[remaining].Index] = await FailAsync(
                            prepared[remaining],
                            RenderState.StartingHlae,
                            "CS2_EXITED",
                            "HLAE/CS2 exited before this clip could be recorded.",
                            true,
                            processes);
                    }
                    break;
                }

                try
                {
                    await controlTask;
                    loadedDemoPath = sourceDemoPath;
                    await stateJournal.WriteAsync(
                        item.Workspace,
                        RenderState.VerifyingOutput,
                        "Checking rendered artifact while keeping CS2 open for the next clip.",
                        cancellationToken);
                    (bool success, string? file, long size, string? error) =
                        await outputWatcher.VerifyAsync(
                            item.Job,
                            item.Workspace,
                            cancellationToken);
                    if (!success)
                    {
                        outcomes[item.Index] = await FailAsync(
                            item,
                            RenderState.VerifyingOutput,
                            "OUTPUT_INVALID",
                            error ?? "Rendered output is invalid.",
                            true,
                            processes);
                        continue;
                    }

                    RenderResult result = new(
                        item.Job.JobId,
                        true,
                        RenderState.Completed,
                        file,
                        size,
                        item.Stopwatch.ElapsedMilliseconds,
                        item.StartedAt,
                        timeProvider.GetUtcNow(),
                        processes,
                        item.Warnings,
                        null);
                    await stateJournal.WriteAsync(
                        item.Workspace,
                        RenderState.Completed,
                        "Clip completed; CS2 remains active for the next highlight.",
                        cancellationToken);
                    await PersistResultAsync(
                        item.Workspace,
                        item.Job.OutputDirectory,
                        result,
                        cancellationToken);
                    outcomes[item.Index] = new RenderJobOutcome(result, ExitCodes.Success);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    outcomes[item.Index] = await FailAsync(
                        item,
                        RenderState.Recording,
                        "DEMO_CONTROL_FAILED",
                        exception.Message,
                        true,
                        processes);
                }
            }

            if (!rendererTask.IsCompleted)
            {
                await demoController.QuitAsync(cancellationToken);
                try
                {
                    ProcessExecutionResult completed = await rendererTask.WaitAsync(
                        TimeSpan.FromSeconds(environment.ProcessShutdownTimeoutSeconds),
                        cancellationToken);
                    processes = processes with { HlaePid = completed.ProcessId };
                }
                catch (TimeoutException)
                {
                    rendererCancellation.Cancel();
                    await ObserveRendererTaskAsync(rendererTask);
                }
            }
        }
        catch (OperationCanceledException)
        {
            rendererCancellation.Cancel();
            await ObserveRendererTaskAsync(rendererTask);
            throw;
        }
        finally
        {
            if (!rendererTask.IsCompleted)
            {
                rendererCancellation.Cancel();
                await ObserveRendererTaskAsync(rendererTask);
            }
        }

        return Complete(outcomes);
    }

    private async Task<RenderJobOutcome> FailAsync(
        PreparedRender item,
        RenderState stage,
        string code,
        string message,
        bool retryable,
        ProcessIdentifiers processes)
    {
        RenderJobOutcome outcome = Failure(
            item.Job,
            item.StartedAt,
            item.Stopwatch,
            stage,
            code,
            message,
            retryable,
            processes,
            item.Warnings);
        await stateJournal.WriteAsync(
            item.Workspace,
            RenderState.Failed,
            message,
            CancellationToken.None);
        await PersistResultAsync(
            item.Workspace,
            item.Job.OutputDirectory,
            outcome.Result,
            CancellationToken.None);
        return outcome;
    }

    private async Task<RenderJobOutcome> FailBeforeWorkspaceAsync(
        RenderJob job,
        RenderState stage,
        string code,
        string message,
        bool retryable,
        CancellationToken cancellationToken,
        DateTimeOffset? startedAt = null,
        Stopwatch? stopwatch = null)
    {
        stopwatch ??= Stopwatch.StartNew();
        RenderJobOutcome outcome = Failure(
            job,
            startedAt ?? timeProvider.GetUtcNow(),
            stopwatch,
            stage,
            code,
            message,
            retryable,
            new ProcessIdentifiers(),
            []);
        Directory.CreateDirectory(job.OutputDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(job.OutputDirectory, "render-result.json"),
            JsonSerializer.Serialize(outcome.Result, JsonOptions),
            cancellationToken);
        return outcome;
    }

    private RenderJobOutcome Failure(
        RenderJob job,
        DateTimeOffset startedAt,
        Stopwatch stopwatch,
        RenderState stage,
        string code,
        string message,
        bool retryable,
        ProcessIdentifiers processes,
        IReadOnlyList<string> warnings)
    {
        RenderError error = new(code, message, stage, retryable);
        RenderResult result = new(
            job.JobId,
            false,
            RenderState.Failed,
            null,
            null,
            stopwatch.ElapsedMilliseconds,
            startedAt,
            timeProvider.GetUtcNow(),
            processes,
            warnings,
            error);
        return new RenderJobOutcome(result, ExitCodes.FromError(error));
    }

    private static RenderJobOutcome[] Complete(
        IReadOnlyList<RenderJobOutcome?> outcomes) =>
        outcomes.Select(value =>
                value ?? throw new InvalidOperationException(
                    "Shared render session did not produce an outcome for every job."))
            .ToArray();

    private static async Task PersistResultAsync(
        RenderWorkspace workspace,
        string outputDirectory,
        RenderResult result,
        CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(result, JsonOptions);
        await File.WriteAllTextAsync(
            Path.Combine(workspace.State, "render-result.json"),
            json,
            cancellationToken);
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "render-result.json"),
            json,
            cancellationToken);
    }

    private static async Task ObserveRendererTaskAsync(
        Task<ProcessExecutionResult> rendererTask)
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
        }
    }

    private sealed record PreparedRender(
        int Index,
        RenderJob Job,
        RenderWorkspace Workspace,
        GeneratedRenderScript Script,
        DateTimeOffset StartedAt,
        Stopwatch Stopwatch,
        IReadOnlyList<string> Warnings);
}
