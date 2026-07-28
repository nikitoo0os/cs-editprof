namespace Cs2Highlight.RenderAgent.Application;

public interface IEnvironmentVerifier
{
    Task<EnvironmentReport> VerifyAsync(RenderJob job, CancellationToken cancellationToken);
}

public interface IWorkspaceManager
{
    Task<RenderWorkspace> PrepareAsync(RenderJob job, CancellationToken cancellationToken);
}

public interface IRenderScriptGenerator
{
    Task<GeneratedRenderScript> GenerateAsync(RenderJob job, RenderWorkspace workspace, CancellationToken cancellationToken);
}

public interface IProcessSupervisor
{
    Task<ProcessExecutionResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken);
}

public interface IHlaeLauncher
{
    Task<ProcessExecutionResult> LaunchAsync(RenderWorkspace workspace, GeneratedRenderScript script, CancellationToken cancellationToken);
}

public interface IRenderOutputWatcher
{
    Task<(bool Success, string? File, long Size, string? Error)> VerifyAsync(
        RenderJob job,
        RenderWorkspace workspace,
        CancellationToken cancellationToken);
}

public interface IRenderLock : IAsyncDisposable
{
    bool Acquired { get; }
}

public interface IRenderLockFactory
{
    IRenderLock TryAcquire();
}

public interface IStateJournal
{
    Task WriteAsync(RenderWorkspace workspace, RenderState state, string message, CancellationToken cancellationToken);
}
