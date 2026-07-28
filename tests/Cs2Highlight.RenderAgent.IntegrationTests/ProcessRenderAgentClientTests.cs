using System.Text.Json;
using Cs2Highlight.Analysis;
using Cs2Highlight.FakeRenderAgent;
using Cs2Highlight.RenderAgent.Application;

namespace Cs2Highlight.RenderAgent.IntegrationTests;

public sealed class ProcessRenderAgentClientTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string root = Path.Combine(Path.GetTempPath(), $"process-render-client-{Guid.NewGuid():N}");

    [Fact]
    public async Task InvokesExecutableAndValidatesOutput()
    {
        Directory.CreateDirectory(root);
        string jobPath = await WriteJobAsync("fake-success");
        ProcessRenderAgentClient client = new(FakeExecutablePath());

        RenderInvocationResult result = await client.RenderAsync(jobPath, 1, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.True(result.Result?.Success);
        Assert.True(File.Exists(result.Result?.OutputFile));
    }

    [Fact]
    public async Task DetectsExitWithoutStructuredResult()
    {
        Directory.CreateDirectory(root);
        string jobPath = await WriteJobAsync("fake-no-result");
        ProcessRenderAgentClient client = new(FakeExecutablePath());

        RenderInvocationResult result = await client.RenderAsync(jobPath, 1, CancellationToken.None);

        Assert.Equal("RENDER_RESULT_NOT_FOUND", result.Error?.Code);
        Assert.True(result.Error?.Retryable);
    }

    private async Task<string> WriteJobAsync(string jobId)
    {
        string demo = Path.Combine(root, "match.dem");
        await File.WriteAllBytesAsync(demo, [1]);
        string output = Path.Combine(root, jobId);
        Directory.CreateDirectory(output);
        RenderJob job = new(
            jobId,
            demo,
            new PlayerSelector("76561198000000001", "Player"),
            new RenderSegment(10, 20),
            new VideoSettings(1920, 1080, 60, 90),
            output,
            60);
        string jobPath = Path.Combine(output, "render-job.json");
        await File.WriteAllTextAsync(
            jobPath,
            JsonSerializer.Serialize(job, JsonOptions));
        return jobPath;
    }

    private static string FakeExecutablePath()
    {
        string directory = Path.GetDirectoryName(typeof(FakeRenderAgentMarker).Assembly.Location)!;
        string path = Path.Combine(directory, "fake-render-agent.exe");
        Assert.True(File.Exists(path), $"Fake Render Agent was not built: {path}");
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
