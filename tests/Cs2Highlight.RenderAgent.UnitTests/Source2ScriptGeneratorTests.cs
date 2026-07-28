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
}
