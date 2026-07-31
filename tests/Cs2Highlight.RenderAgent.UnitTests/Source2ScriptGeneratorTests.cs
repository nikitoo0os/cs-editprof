using Cs2Highlight.RenderAgent.Application;
using Cs2Highlight.RenderAgent.Infrastructure;

namespace Cs2Highlight.RenderAgent.UnitTests;

public sealed class Source2ScriptGeneratorTests
{
    [Fact]
    public void EscapesQuotesAndNormalizesPathSeparators()
    {
        Assert.Equal("C:/demo/\\\"name.dem", Source2ScriptGenerator.EscapeCfg("C:\\demo\\\"name.dem"));
    }

    [Theory]
    [InlineData("name;quit")]
    [InlineData("name\nquit")]
    public void RejectsCommandInjection(string value)
    {
        Assert.Throws<ArgumentException>(() => Source2ScriptGenerator.EscapeCfg(value));
    }

    [Fact]
    public void BuildsSourceConfirmedCustomLoaderArguments()
    {
        string root = Path.GetFullPath("work");
        RenderWorkspace workspace = new(
            root,
            Path.Combine(root, "input"),
            Path.Combine(root, "config"),
            Path.Combine(root, "raw"),
            Path.Combine(root, "output"),
            Path.Combine(root, "logs"),
            Path.Combine(root, "state"),
            Path.Combine(root, "input", "demo.dem"));
        GeneratedRenderScript script = new(Path.Combine(workspace.Config, "cfg", "render.cfg"), 2560, 1440, []);
        RenderEnvironmentOptions environment = new()
        {
            Cs2ExecutablePath = @"D:\Steam\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe"
        };

        IReadOnlyList<string> arguments = HlaeLauncher.BuildArguments(
            environment,
            workspace,
            script,
            @"D:\Tools\HLAE\x64\AfxHookSource2.dll");

        Assert.Contains("-customLoader", arguments);
        Assert.Contains("-autoStart", arguments);
        Assert.Contains("-noGui", arguments);
        Assert.Contains("-insecure", arguments.Single(value => value.Contains("-insecure", StringComparison.Ordinal)));
        Assert.Contains("-afxFixNetCon", arguments.Single(value => value.Contains("-afxFixNetCon", StringComparison.Ordinal)));
        Assert.Contains("-netconport 32123", arguments.Single(value => value.Contains("-netconport 32123", StringComparison.Ordinal)));
        Assert.Contains("-w 2560", arguments.Single(value => value.Contains("-w 2560", StringComparison.Ordinal)));
        Assert.Contains($"USRLOCALCSGO={workspace.Config}", arguments);
    }

    [Fact]
    public async Task StartupScriptConfiguresCaptureButDefersDemoPlaybackAndControl()
    {
        string root = Path.Combine(Path.GetTempPath(), $"source2-script-{Guid.NewGuid():N}");
        RenderWorkspace workspace = new(
            root,
            Path.Combine(root, "input"),
            Path.Combine(root, "config"),
            Path.Combine(root, "raw"),
            Path.Combine(root, "output"),
            Path.Combine(root, "logs"),
            Path.Combine(root, "state"),
            Path.Combine(root, "input", "demo_safe138.dem"));
        RenderJob job = new(
            "test",
            workspace.PreparedDemoPath,
            new PlayerSelector("76561198000000001", "Player"),
            new RenderSegment(100, 200),
            new VideoSettings(1920, 1080, 60, 90),
            workspace.Output);
        try
        {
            RenderEnvironmentOptions environment = new()
            {
                FfmpegExecutablePath = @"D:\Tools\FFmpeg\bin\ffmpeg.exe"
            };
            GeneratedRenderScript script = await new Source2ScriptGenerator(environment)
                .GenerateAsync(job, workspace, CancellationToken.None);
            string cfg = await File.ReadAllTextAsync(script.Path);

            Assert.DoesNotContain("playdemo", cfg);
            Assert.Contains("settings add ffmpegEx cs2HighlightFfmpeg", cfg);
            Assert.Contains("{QUOTE}D:/Tools/FFmpeg/bin/ffmpeg.exe{QUOTE}", cfg);
            Assert.Contains("{QUOTE}{AFX_STREAM_PATH}/video.mp4{QUOTE}", cfg);
            Assert.DoesNotContain(@"\\", cfg);
            Assert.DoesNotContain("demo_gototick", cfg);
            Assert.DoesNotContain("addAtTick", cfg);
            Assert.DoesNotContain("spec_player", cfg);
            Assert.Contains("capture-presentation-reset.v4 (PovCombat)", cfg);
            Assert.Contains("cl_showdemooverlay 0", cfg);
            Assert.Contains("r_show_build_info 0", cfg);
            Assert.Contains("cl_trueview_show_status 0", cfg);
            Assert.Contains("cl_drawhud 1", cfg);
            Assert.Contains("r_drawviewmodel 1", cfg);
            Assert.Contains("cl_draw_only_deathnotices 0", cfg);
            Assert.Contains("gameui_hide", cfg);
            Assert.Contains("hideconsole", cfg);
            Assert.DoesNotContain(job.Player.Name!, cfg);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }
}
