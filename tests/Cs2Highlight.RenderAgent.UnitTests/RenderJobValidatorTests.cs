using Cs2Highlight.RenderAgent.Application;

namespace Cs2Highlight.RenderAgent.UnitTests;

public sealed class RenderJobValidatorTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"render-agent-tests-{Guid.NewGuid():N}");
    private readonly string demo;

    public RenderJobValidatorTests()
    {
        Directory.CreateDirectory(root);
        demo = Path.Combine(root, "match.dem");
        File.WriteAllBytes(demo, [1, 2, 3]);
    }

    [Fact]
    public void ValidJobPasses()
    {
        ValidationReport result = RenderJobValidator.Validate(ValidJob(), new RenderEnvironmentOptions());
        Assert.True(result.IsValid, string.Join(", ", result.Errors));
    }

    [Fact]
    public void InvalidTickRangeIsReported()
    {
        RenderJob job = ValidJob() with { Segment = new RenderSegment(10, 10) };
        ValidationReport result = RenderJobValidator.Validate(job, new RenderEnvironmentOptions());
        Assert.Contains(result.Errors, error => error.Contains("endTick", StringComparison.Ordinal));
    }

    [Fact]
    public void UnsafeJobIdIsReported()
    {
        RenderJob job = ValidJob() with { JobId = "../escape" };
        Assert.False(RenderJobValidator.Validate(job, new RenderEnvironmentOptions()).IsValid);
    }

    [Fact]
    public void MissingPlayerSteamIdIsReported()
    {
        RenderJob job = ValidJob() with
        {
            Player = new PlayerSelector(null, "Player")
        };

        ValidationReport result = RenderJobValidator.Validate(job, new RenderEnvironmentOptions());

        Assert.Contains(result.Errors, error => error.Contains("player.steamId", StringComparison.Ordinal));
    }

    [Fact]
    public void InvalidPlayerSteamIdIsReported()
    {
        RenderJob job = ValidJob() with
        {
            Player = new PlayerSelector("123", "Player")
        };

        ValidationReport result = RenderJobValidator.Validate(job, new RenderEnvironmentOptions());

        Assert.Contains(result.Errors, error => error.Contains("valid individual SteamID64", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingPlayerNameIsReported()
    {
        RenderJob job = ValidJob() with
        {
            Player = new PlayerSelector("76561198000000001", null)
        };

        ValidationReport result = RenderJobValidator.Validate(job, new RenderEnvironmentOptions());

        Assert.Contains(result.Errors, error => error.Contains("player.name", StringComparison.Ordinal));
    }

    private RenderJob ValidJob() => new(
        "job-1", demo, new PlayerSelector("76561198000000001", "Player"),
        new RenderSegment(10, 20), new VideoSettings(1920, 1080, 60, 90),
        Path.Combine(root, "output"), 60);

    public void Dispose() => Directory.Delete(root, true);
}
