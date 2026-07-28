using Cs2Highlight.RenderAgent.Application;
using Cs2Highlight.RenderAgent.Infrastructure;

namespace Cs2Highlight.RenderAgent.UnitTests;

public sealed class NetConsoleDemoControllerTests
{
    [Fact]
    public void PrefersPlayerNameBecauseSpecPlayerAcceptsNameOrSlot()
    {
        PlayerSelector player = new("76561198000000001", "Player One");

        Assert.Equal("Player One", NetConsoleDemoController.SelectPlayer(player));
    }

    [Fact]
    public void RejectsConsoleCommandInjection()
    {
        Assert.Throws<ArgumentException>(() =>
            NetConsoleDemoController.EscapeCommandArgument("Player;quit"));
    }

    [Fact]
    public void ResolvesFfprobeBesideFfmpegByDefault()
    {
        RenderEnvironmentOptions options = new()
        {
            FfmpegExecutablePath = @"D:\Tools\HLAE\ffmpeg\bin\ffmpeg.exe"
        };

        Assert.Equal(
            @"D:\Tools\HLAE\ffmpeg\bin\ffprobe.exe",
            RenderOutputWatcher.ResolveFfprobePath(options));
    }

    [Fact]
    public void AcceptsFfprobeJsonWithExpectedVideo()
    {
        const string json =
            """{"streams":[{"codec_type":"video","width":1920,"height":1080}],"format":{"duration":"12.5","size":"1000"}}""";

        string? error = RenderOutputWatcher.ValidateProbeJson(
            json,
            new VideoSettings(1920, 1080, 60, 90));

        Assert.Null(error);
    }

    [Fact]
    public void RejectsUnexpectedVideoDimensions()
    {
        const string json =
            """{"streams":[{"codec_type":"video","width":1280,"height":720}],"format":{"duration":"12.5","size":"1000"}}""";

        string? error = RenderOutputWatcher.ValidateProbeJson(
            json,
            new VideoSettings(1920, 1080, 60, 90));

        Assert.Contains("expected 1920x1080", error);
    }
}
