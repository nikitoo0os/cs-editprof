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

    [Theory]
    [InlineData("# 2 7 \"Player One\" 76561199031052443 00:10 10 0 active", 7)]
    [InlineData("# 2 7 \"Player One\" [U:1:1070786715] 00:10 10 0 active", 7)]
    [InlineData("# 2 7 \"Player One\" STEAM_1:1:535393357 00:10 10 0 active", 7)]
    [InlineData("# 6 \"Player One\" [U:1:1070786715] 00:10 10 0 active", 7)]
    public void ResolvesPlayerSlotFromStatus(string statusLine, int expectedSlot)
    {
        int slot = NetConsoleDemoController.ResolvePlayerSlot(
            [statusLine],
            76561199031052443UL);

        Assert.Equal(expectedSlot, slot);
    }

    [Fact]
    public void DoesNotResolveDifferentSteamId()
    {
        Assert.Throws<InvalidOperationException>(() =>
            NetConsoleDemoController.ResolvePlayerSlot(
                ["# 2 7 \"Other\" [U:1:1516903402] 00:10 10 0 active"],
                76561199031052443UL));
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
