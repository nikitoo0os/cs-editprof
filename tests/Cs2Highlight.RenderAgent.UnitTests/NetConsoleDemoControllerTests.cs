using Cs2Highlight.RenderAgent.Application;
using Cs2Highlight.RenderAgent.Infrastructure;

namespace Cs2Highlight.RenderAgent.UnitTests;

public sealed class NetConsoleDemoControllerTests
{
    [Fact]
    public void AcceptsIndividualSteamId64()
    {
        PlayerSelector player = new("76561199031052443", "Player One");

        Assert.Equal(76561199031052443UL, NetConsoleDemoController.GetSteamId64(player));
        Assert.Equal(1070786715U, NetConsoleDemoController.GetAccountId(76561199031052443UL));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-steam-id")]
    [InlineData("123")]
    public void RejectsInvalidSteamId64(string? steamId)
    {
        Assert.Throws<InvalidOperationException>(
            () => NetConsoleDemoController.GetSteamId64(new PlayerSelector(steamId, "Player")));
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

    [Fact]
    public void MuxedRenderRequiresGameplayAudioStream()
    {
        const string videoOnly =
            """{"streams":[{"codec_type":"video","width":1920,"height":1080}],"format":{"duration":"12.5","size":"1000"}}""";
        const string videoAndAudio =
            """{"streams":[{"codec_type":"video","width":1920,"height":1080},{"codec_type":"audio"}],"format":{"duration":"12.5","size":"1000"}}""";
        VideoSettings expected = new(1920, 1080, 60, 90);

        Assert.StartsWith(
            "GAMEPLAY_AUDIO_MISSING",
            RenderOutputWatcher.ValidateProbeJson(
                videoOnly,
                expected,
                requireAudio: true));
        Assert.Null(RenderOutputWatcher.ValidateProbeJson(
            videoAndAudio,
            expected,
            requireAudio: true));
    }

    [Fact]
    public void ResolvesHlaeAudioNextToVideoStream()
    {
        string video = Path.Combine("take0000", "video.mp4");

        Assert.Equal(
            Path.GetFullPath(Path.Combine("take0000", "audio.wav")),
            RenderOutputWatcher.ResolveCapturedAudioPath(video));
    }
}
