using Cs2Highlight.Web.Domain;
using Cs2Highlight.Web.Services;

namespace Cs2Highlight.Web.Tests;

public sealed class GenerationStageMappingTests
{
    [Fact]
    public void RenderingHasExactlyOneAuthoritativeCurrentStage()
    {
        IReadOnlyList<GenerationStageView> stages =
            GenerationStageMapping.For(GenerationStatus.RenderingClips);

        Assert.Single(stages, value => value.State == GenerationStageState.Current);
        Assert.Equal("rendering", stages.Single(value => value.State == GenerationStageState.Current).Key);
        Assert.All(stages.Where(value => value.Key is "upload" or "analysis" or "music" or "planning"),
            value => Assert.Equal(GenerationStageState.Complete, value.State));
    }

    [Fact]
    public void CompletedGenerationMarksReadyAsCurrent()
    {
        IReadOnlyList<GenerationStageView> stages =
            GenerationStageMapping.For(GenerationStatus.Completed);

        Assert.Equal("ready", stages.Single(value => value.State == GenerationStageState.Current).Key);
        Assert.DoesNotContain(stages, value => value.State == GenerationStageState.Pending);
    }
}
