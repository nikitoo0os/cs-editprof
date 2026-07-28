using Cs2Highlight.RenderAgent.Application;
using Cs2Highlight.RenderAgent.Infrastructure;

namespace Cs2Highlight.RenderAgent.UnitTests;

public sealed class DemoCompatibilityRepairerTests
{
    [Fact]
    public void UsesBundledToolWhenNoOverrideIsConfigured()
    {
        string path = DemoCompatibilityRepairer.ResolveExecutablePath(new RenderEnvironmentOptions());

        Assert.Equal(
            Path.Combine(AppContext.BaseDirectory, "tools", DemoCompatibilityRepairer.BundledExecutableName),
            path);
    }

    [Fact]
    public void CreatesRepairedCopyBesideWorkspaceDemo()
    {
        string input = Path.Combine("D:", "work", "input", "match.dem");

        string output = DemoCompatibilityRepairer.GetOutputPath(input);

        Assert.Equal(Path.Combine("D:", "work", "input", "match_safe138.dem"), output);
    }
}
