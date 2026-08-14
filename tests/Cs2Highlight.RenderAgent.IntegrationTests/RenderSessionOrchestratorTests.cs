using Cs2Highlight.RenderAgent.Application;

namespace Cs2Highlight.RenderAgent.IntegrationTests;

public sealed class RenderSessionOrchestratorTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"render-session-{Guid.NewGuid():N}");

    [Fact]
    public async Task LaunchesAndQuitsCs2OnceForMultipleJobs()
    {
        Directory.CreateDirectory(root);
        string demo = Path.Combine(root, "match.dem");
        await File.WriteAllBytesAsync(demo, [1, 2, 3]);
        RenderEnvironmentOptions environment = new()
        {
            WorkingRoot = Path.Combine(root, "work"),
            ProcessShutdownTimeoutSeconds = 2
        };
        FakeSessionRuntime runtime = new();
        FakeWorkspaceManager workspaceManager = new(environment);
        RenderSessionOrchestrator orchestrator = new(
            environment,
            new SuccessfulEnvironmentVerifier(),
            workspaceManager,
            new PassThroughRepairer(),
            new FakeScriptGenerator(),
            runtime,
            runtime,
            new SuccessfulOutputWatcher(),
            new FakeLockFactory(),
            new NullStateJournal(),
            TimeProvider.System);
        RenderJob[] jobs =
        [
            Job("clip-1", demo, 100, 200),
            Job("clip-2", demo, 300, 400),
            Job("clip-3", demo, 500, 600)
        ];

        IReadOnlyList<RenderJobOutcome> outcomes =
            await orchestrator.RunAsync(jobs, CancellationToken.None);

        Assert.All(outcomes, outcome => Assert.True(outcome.Result.Success));
        Assert.Equal(1, runtime.LaunchCount);
        Assert.Equal(3, runtime.ControlCount);
        Assert.Equal(
            [DemoLoadMode.Start, DemoLoadMode.ReuseCurrent, DemoLoadMode.ReuseCurrent],
            runtime.LoadModes);
        Assert.Equal(1, runtime.QuitCount);
        Assert.Equal(3, workspaceManager.DeletedCount);
        Assert.All(
            outcomes,
            outcome =>
            {
                Assert.Equal(123, outcome.Result.Processes.HlaePid);
                Assert.Equal(456, outcome.Result.Processes.Cs2Pid);
            });
    }

    [Fact]
    public async Task StopsSharedSessionAfterIncompatibleDemoIsDetected()
    {
        Directory.CreateDirectory(root);
        string demo = Path.Combine(root, "old-match.dem");
        await File.WriteAllBytesAsync(demo, [1, 2, 3]);
        RenderEnvironmentOptions environment = new()
        {
            WorkingRoot = Path.Combine(root, "work"),
            ProcessShutdownTimeoutSeconds = 2
        };
        IncompatibleSessionRuntime runtime = new();
        RenderSessionOrchestrator orchestrator = new(
            environment,
            new SuccessfulEnvironmentVerifier(),
            new FakeWorkspaceManager(environment),
            new PassThroughRepairer(),
            new FakeScriptGenerator(),
            runtime,
            runtime,
            new SuccessfulOutputWatcher(),
            new FakeLockFactory(),
            new NullStateJournal(),
            TimeProvider.System);

        IReadOnlyList<RenderJobOutcome> outcomes = await orchestrator.RunAsync(
            [Job("clip-1", demo, 100, 200), Job("clip-2", demo, 300, 400)],
            CancellationToken.None);

        Assert.Equal(1, runtime.ControlCount);
        Assert.Equal(1, runtime.QuitCount);
        Assert.All(
            outcomes,
            outcome =>
            {
                Assert.False(outcome.Result.Success);
                Assert.Equal(
                    "DEMO_NETWORK_VERSION_INCOMPATIBLE",
                    outcome.Result.Error?.Code);
                Assert.False(outcome.Result.Error?.Retryable);
            });
    }

    private RenderJob Job(
        string id,
        string demo,
        long startTick,
        long endTick)
    {
        string output = Path.Combine(root, "output", id);
        Directory.CreateDirectory(output);
        return new RenderJob(
            id,
            demo,
            new PlayerSelector("76561198000000001", "Player"),
            new RenderSegment(startTick, endTick) { TickRate = 64 },
            new VideoSettings(1920, 1080, 60, 90),
            output,
            30);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, true);
    }

    private sealed class SuccessfulEnvironmentVerifier : IEnvironmentVerifier
    {
        public Task<EnvironmentReport> VerifyAsync(
            RenderJob job,
            CancellationToken cancellationToken) =>
            Task.FromResult(new EnvironmentReport([]));
    }

    private sealed class FakeWorkspaceManager(
        RenderEnvironmentOptions environment) : IWorkspaceManager
    {
        public int DeletedCount { get; private set; }

        public Task<RenderWorkspace> PrepareAsync(
            RenderJob job,
            CancellationToken cancellationToken)
        {
            string jobRoot = Path.Combine(environment.WorkingRoot, job.JobId);
            string input = Create(jobRoot, "input");
            string config = Create(jobRoot, "config");
            string raw = Create(jobRoot, "raw");
            string output = Create(jobRoot, "output");
            string logs = Create(jobRoot, "logs");
            string state = Create(jobRoot, "state");
            string demo = Path.Combine(input, "match.dem");
            File.Copy(job.DemoPath, demo, true);
            return Task.FromResult(new RenderWorkspace(
                jobRoot,
                input,
                config,
                raw,
                output,
                logs,
                state,
                demo));
        }

        public Task<bool> DeleteCompletedAsync(
            RenderWorkspace workspace,
            CancellationToken cancellationToken)
        {
            if (Directory.Exists(workspace.Root))
                Directory.Delete(workspace.Root, recursive: true);
            DeletedCount++;
            return Task.FromResult(true);
        }

        private static string Create(string root, string name)
        {
            string path = Path.Combine(root, name);
            Directory.CreateDirectory(path);
            return path;
        }
    }

    private sealed class PassThroughRepairer : IDemoCompatibilityRepairer
    {
        public Task<DemoCompatibilityResult> RepairAsync(
            RenderWorkspace workspace,
            CancellationToken cancellationToken) =>
            Task.FromResult(new DemoCompatibilityResult(
                workspace.PreparedDemoPath,
                false,
                "Demo is compatible."));
    }

    private sealed class FakeScriptGenerator : IRenderScriptGenerator
    {
        public Task<GeneratedRenderScript> GenerateAsync(
            RenderJob job,
            RenderWorkspace workspace,
            CancellationToken cancellationToken) =>
            Task.FromResult(new GeneratedRenderScript(
                Path.Combine(workspace.Config, "render.cfg"),
                job.Video.Width,
                job.Video.Height,
                []));
    }

    private sealed class FakeSessionRuntime : IHlaeLauncher, IDemoController
    {
        private readonly TaskCompletionSource<ProcessExecutionResult> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int LaunchCount { get; private set; }
        public int ControlCount { get; private set; }
        public int QuitCount { get; private set; }
        public List<DemoLoadMode> LoadModes { get; } = [];

        public Task<ProcessExecutionResult> LaunchAsync(
            RenderJob job,
            RenderWorkspace workspace,
            GeneratedRenderScript script,
            CancellationToken cancellationToken)
        {
            LaunchCount++;
            cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            return completion.Task;
        }

        public Task ControlAsync(
            RenderJob job,
            RenderWorkspace workspace,
            DemoLoadMode loadMode,
            CancellationToken cancellationToken)
        {
            ControlCount++;
            LoadModes.Add(loadMode);
            return Task.CompletedTask;
        }

        public Task QuitAsync(CancellationToken cancellationToken)
        {
            QuitCount++;
            completion.TrySetResult(new ProcessExecutionResult(
                123,
                0,
                false,
                TimeSpan.FromSeconds(1),
                456));
            return Task.CompletedTask;
        }
    }

    private sealed class IncompatibleSessionRuntime : IHlaeLauncher, IDemoController
    {
        private readonly TaskCompletionSource<ProcessExecutionResult> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ControlCount { get; private set; }
        public int QuitCount { get; private set; }

        public Task<ProcessExecutionResult> LaunchAsync(
            RenderJob job,
            RenderWorkspace workspace,
            GeneratedRenderScript script,
            CancellationToken cancellationToken)
        {
            cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            return completion.Task;
        }

        public Task ControlAsync(
            RenderJob job,
            RenderWorkspace workspace,
            DemoLoadMode loadMode,
            CancellationToken cancellationToken)
        {
            ControlCount++;
            return Task.FromException(
                new DemoPlaybackIncompatibleException(
                    "DEMO_NETWORK_VERSION_INCOMPATIBLE: test"));
        }

        public Task QuitAsync(CancellationToken cancellationToken)
        {
            QuitCount++;
            completion.TrySetResult(new ProcessExecutionResult(
                123,
                0,
                false,
                TimeSpan.FromSeconds(1),
                456));
            return Task.CompletedTask;
        }
    }

    private sealed class SuccessfulOutputWatcher : IRenderOutputWatcher
    {
        public async Task<(bool Success, string? File, long Size, string? Error)>
            VerifyAsync(
                RenderJob job,
                RenderWorkspace workspace,
                CancellationToken cancellationToken)
        {
            string output = Path.Combine(job.OutputDirectory, "raw-highlight.mp4");
            await File.WriteAllBytesAsync(output, [1, 2, 3], cancellationToken);
            return (true, output, 3, null);
        }
    }

    private sealed class FakeLockFactory : IRenderLockFactory
    {
        public IRenderLock TryAcquire() => new FakeLock();
    }

    private sealed class FakeLock : IRenderLock
    {
        public bool Acquired => true;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NullStateJournal : IStateJournal
    {
        public Task WriteAsync(
            RenderWorkspace workspace,
            RenderState state,
            string message,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
