using Cs2Highlight.RenderAgent.Application;
using Cs2Highlight.RenderAgent.Infrastructure;
using System.Text.Json;

namespace Cs2Highlight.RenderAgent.IntegrationTests;

public sealed class WorkspaceManagerTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"workspace-manager-{Guid.NewGuid():N}");

    [Fact]
    public async Task ReplacesAStaleDemoWhenJobIdAndFileNameAreReused()
    {
        string firstSource = Source("first", [1, 2, 3]);
        string secondSource = Source("second", [9, 8, 7, 6]);
        RenderEnvironmentOptions environment = new()
        {
            WorkingRoot = Path.Combine(root, "work")
        };
        WorkspaceManager manager = new(environment);

        RenderWorkspace first = await manager.PrepareAsync(
            Job(firstSource),
            CancellationToken.None);
        Assert.Equal(
            new byte[] { 1, 2, 3 },
            await File.ReadAllBytesAsync(first.PreparedDemoPath));

        RenderWorkspace second = await manager.PrepareAsync(
            Job(secondSource),
            CancellationToken.None);

        Assert.Equal(first.PreparedDemoPath, second.PreparedDemoPath);
        Assert.Equal(
            new byte[] { 9, 8, 7, 6 },
            await File.ReadAllBytesAsync(second.PreparedDemoPath));
        Assert.Empty(Directory.EnumerateFiles(
            second.Input,
            "*.incoming",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task DeletesSuccessfulWorkspaceAfterOutputWasPersistedElsewhere()
    {
        string source = Source("success", [1, 2, 3]);
        RenderEnvironmentOptions environment = new()
        {
            WorkingRoot = Path.Combine(root, "work")
        };
        WorkspaceManager manager = new(environment);
        RenderJob job = Job(source);
        RenderWorkspace workspace = await manager.PrepareAsync(
            job,
            CancellationToken.None);
        string output = Path.Combine(job.OutputDirectory, "raw-highlight.mp4");
        await File.WriteAllBytesAsync(output, [4, 5, 6]);
        await WriteResultAsync(workspace, job.JobId, true, output);

        bool deleted = await manager.DeleteCompletedAsync(
            workspace,
            CancellationToken.None);

        Assert.True(deleted);
        Assert.False(Directory.Exists(workspace.Root));
        Assert.True(File.Exists(output));
    }

    [Fact]
    public async Task PreservesFailedWorkspaceForDiagnostics()
    {
        string source = Source("failure", [1, 2, 3]);
        RenderEnvironmentOptions environment = new()
        {
            WorkingRoot = Path.Combine(root, "work")
        };
        WorkspaceManager manager = new(environment);
        RenderJob job = Job(source);
        RenderWorkspace workspace = await manager.PrepareAsync(
            job,
            CancellationToken.None);
        await WriteResultAsync(workspace, job.JobId, false, null);

        bool deleted = await manager.DeleteCompletedAsync(
            workspace,
            CancellationToken.None);

        Assert.False(deleted);
        Assert.True(Directory.Exists(workspace.Root));
    }

    private static Task WriteResultAsync(
        RenderWorkspace workspace,
        string jobId,
        bool success,
        string? output) =>
        File.WriteAllTextAsync(
            Path.Combine(workspace.State, "render-result.json"),
            JsonSerializer.Serialize(
                new RenderResult(
                    jobId,
                    success,
                    success ? RenderState.Completed : RenderState.Failed,
                    output,
                    success ? 3 : null,
                    1,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    new ProcessIdentifiers(),
                    [],
                    success
                        ? null
                        : new RenderError(
                            "TEST_FAILURE",
                            "Expected test failure.",
                            RenderState.Failed,
                            false)),
                JsonOptions));

    private string Source(string directory, byte[] content)
    {
        string path = Path.Combine(root, directory, "demo-001.dem");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    private RenderJob Job(string demoPath)
    {
        string output = Path.Combine(root, "result");
        Directory.CreateDirectory(output);
        return new RenderJob(
            "demo-001-r01-solokill-001",
            demoPath,
            new PlayerSelector("76561199031052443", "Player"),
            new RenderSegment(100, 200) { TickRate = 64 },
            new VideoSettings(1920, 1080, 60, 90),
            output,
            30);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
