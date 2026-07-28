using Cs2Highlight.RenderAgent.Application;
using Cs2Highlight.RenderAgent.Infrastructure;

namespace Cs2Highlight.RenderAgent.IntegrationTests;

public sealed class RenderLockTests
{
    [Fact]
    public async Task CanReleaseAfterAsyncContinuationAndAcquireAgain()
    {
        RenderLockFactory factory = new();
        IRenderLock first = factory.TryAcquire();
        Assert.True(first.Acquired);

        await Task.Run(async () => await first.DisposeAsync());

        await using IRenderLock second = factory.TryAcquire();
        Assert.True(second.Acquired);
    }
}
