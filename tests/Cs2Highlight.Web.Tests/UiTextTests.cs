using Cs2Highlight.Web.Ui;

namespace Cs2Highlight.Web.Tests;

public sealed class UiTextTests
{
    [Fact]
    public void ContiguousTimelineErrorExplainsHowToRecover()
    {
        string message = UiText.Error(
            "CINEMATIC_BROLL_INSUFFICIENT_FOR_CONTIGUOUS_TIMELINE");

        Assert.Contains("без повторов", message, StringComparison.Ordinal);
        Assert.Contains("меньшую длительность", message, StringComparison.Ordinal);
        Assert.DoesNotContain("CINEMATIC_", message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownInternalCodeIsNotExposed()
    {
        string message = UiText.Error("UNRECOGNIZED_INTERNAL_FAILURE");

        Assert.Equal(
            "Не удалось выполнить операцию. Попробуйте ещё раз.",
            message);
    }

    [Fact]
    public void MusicAnalysisFailureWithDetailsUsesSafeMessage()
    {
        string message = UiText.Error(
            "MUSIC_ANALYSIS_FAILED: decoder diagnostics");

        Assert.DoesNotContain("decoder diagnostics", message, StringComparison.Ordinal);
        Assert.Contains("трек", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HighlightCapacityMessageIncludesExactRemovalCount()
    {
        string message = UiText.HighlightRemovalRequired(7, 3);

        Assert.Contains("оставьте не больше 4 из 7", message);
        Assert.Contains("уберите минимум 3", message);
    }
}
