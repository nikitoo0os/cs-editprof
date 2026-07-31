using Cs2Highlight.Analysis;

namespace Cs2Highlight.RenderAgent.UnitTests;

public sealed class AnalysisValidatorTests
{
    [Theory]
    [InlineData("1.0")]
    [InlineData("1.1")]
    [InlineData("1.2")]
    [InlineData("1.3")]
    public void SupportedParserSchemasAreAccepted(string schemaVersion)
    {
        DemoAnalysis analysis = ValidAnalysis(schemaVersion);

        DemoAnalysis result = AnalysisValidator.Validate(analysis);

        Assert.Same(analysis, result);
    }

    [Fact]
    public void UnknownParserSchemaIsRejected()
    {
        AnalysisException exception = Assert.Throws<AnalysisException>(
            () => AnalysisValidator.Validate(ValidAnalysis("2.0")));

        Assert.Equal("UNSUPPORTED_ANALYSIS_SCHEMA", exception.Error.Code);
    }

    private static DemoAnalysis ValidAnalysis(string schemaVersion) =>
        new(
            schemaVersion,
            new ParserInfo("test", "1"),
            new DemoMetadata("match.dem", "de_test", 64, 128, null),
            [],
            [new DemoRound(1, 0, 1, 127, "T", null)],
            [],
            []);
}
