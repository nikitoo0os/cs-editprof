using Cs2Highlight.RenderAgent.Application;
using Cs2Highlight.RenderAgent.Infrastructure;

namespace Cs2Highlight.RenderAgent.UnitTests;

public sealed class Source2ScriptGeneratorTests
{
    [Fact]
    public void EscapesQuotesAndBackslashes()
    {
        Assert.Equal("C:\\\\demo\\\\\\\"name.dem", Source2ScriptGenerator.EscapeCfg("C:\\demo\\\"name.dem"));
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
        Assert.Contains("-w 2560", arguments.Single(value => value.Contains("-w 2560", StringComparison.Ordinal)));
        Assert.Contains($"USRLOCALCSGO={workspace.Config}", arguments);
    }
}
