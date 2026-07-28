using Cs2Highlight.RenderAgent.Application;

namespace Cs2Highlight.RenderAgent.UnitTests;

public sealed class ExitCodesTests
{
    [Fact]
    public void MapsOutputFailure()
    {
        RenderError error = new("OUTPUT_INVALID", "missing", RenderState.VerifyingOutput, true);
        Assert.Equal(ExitCodes.OutputVerificationFailed, ExitCodes.FromError(error));
    }
}
