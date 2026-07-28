using System.Text.Json;
using Cs2Highlight.RenderAgent.Application;

namespace Cs2Highlight.FakeRenderAgent;

public static class FakeRenderAgentMarker;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 3 || args[0] != "render" || args[1] != "--job") return 2;
        await using FileStream input = File.OpenRead(args[2]);
        RenderJob job = await JsonSerializer.DeserializeAsync<RenderJob>(input, JsonOptions) ??
            throw new InvalidDataException("Missing job.");
        Directory.CreateDirectory(job.OutputDirectory);
        if (job.JobId.Contains("no-result", StringComparison.Ordinal)) return 0;

        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool fail = job.JobId.Contains("failure", StringComparison.Ordinal);
        string? output = null;
        if (!fail)
        {
            output = Path.Combine(job.OutputDirectory, "raw-highlight.mp4");
            await File.WriteAllBytesAsync(output, [1, 2, 3, 4]);
        }
        RenderResult result = new(
            job.JobId,
            !fail,
            fail ? RenderState.Failed : RenderState.Completed,
            output,
            fail ? null : 4,
            10,
            now,
            now,
            new ProcessIdentifiers(),
            [],
            fail
                ? new RenderError(
                    "CS2_START_TIMEOUT",
                    "Controlled fake failure.",
                    RenderState.StartingHlae,
                    true)
                : null);
        await File.WriteAllTextAsync(
            Path.Combine(job.OutputDirectory, "render-result.json"),
            JsonSerializer.Serialize(result, JsonOptions));
        Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
        return fail ? 31 : 0;
    }
}
