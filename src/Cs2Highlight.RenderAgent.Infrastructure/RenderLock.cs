using Cs2Highlight.RenderAgent.Application;

namespace Cs2Highlight.RenderAgent.Infrastructure;

public sealed class RenderLockFactory : IRenderLockFactory
{
    public IRenderLock TryAcquire() => new NamedMutexRenderLock();

    private sealed class NamedMutexRenderLock : IRenderLock
    {
        private readonly Mutex mutex = new(false, @"Local\Cs2Highlight.RenderAgent");
        public bool Acquired { get; }

        public NamedMutexRenderLock()
        {
            try
            {
                Acquired = mutex.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                Acquired = true;
            }
        }

        public ValueTask DisposeAsync()
        {
            if (Acquired)
            {
                mutex.ReleaseMutex();
            }
            mutex.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
