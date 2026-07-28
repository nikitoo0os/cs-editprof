using Cs2Highlight.RenderAgent.Application;
using Cs2Highlight.RenderAgent.Infrastructure;

namespace Cs2Highlight.RenderAgent.IntegrationTests;

public sealed class ProcessSupervisorTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"process-supervisor-{Guid.NewGuid():N}");

    public ProcessSupervisorTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task CapturesStdoutStderrAndExitCode()
    {
        string stdout = Path.Combine(root, "stdout.log");
        string stderr = Path.Combine(root, "stderr.log");
        ProcessRequest request = new(
            "powershell.exe",
            ["-NoProfile", "-Command", "[Console]::Out.Write('out'); [Console]::Error.Write('err'); exit 7"],
            root, stdout, stderr, TimeSpan.FromSeconds(10));
        ProcessExecutionResult result = await new ProcessSupervisor().RunAsync(request, CancellationToken.None);
        Assert.Equal(7, result.ExitCode);
        Assert.Equal("out", await File.ReadAllTextAsync(stdout));
        Assert.Equal("err", await File.ReadAllTextAsync(stderr));
    }

    [Fact]
    public async Task KillsProcessOnTimeout()
    {
        ProcessRequest request = new(
            "powershell.exe",
            ["-NoProfile", "-Command", "Start-Sleep -Seconds 10"],
            root, Path.Combine(root, "out.log"), Path.Combine(root, "err.log"), TimeSpan.FromMilliseconds(200));
        ProcessExecutionResult result = await new ProcessSupervisor().RunAsync(request, CancellationToken.None);
        Assert.True(result.TimedOut);
    }

    public void Dispose() => Directory.Delete(root, true);
}
