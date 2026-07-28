using Cs2Highlight.RenderAgent.Application;

namespace Cs2Highlight.RenderAgent.Infrastructure;

public sealed class RenderLockFactory : IRenderLockFactory
{
    public IRenderLock TryAcquire() => new NamedSemaphoreRenderLock();

    private sealed class NamedSemaphoreRenderLock : IRenderLock
    {
        private readonly Semaphore semaphore = new(1, 1, @"Local\Cs2Highlight.RenderAgent");
        public bool Acquired { get; }

        public NamedSemaphoreRenderLock() => Acquired = semaphore.WaitOne(TimeSpan.Zero);

        public ValueTask DisposeAsync()
        {
            if (Acquired)
            {
                semaphore.Release();
            }
            semaphore.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
