namespace DevOpsAgent.Application.Abstractions;

/// <summary>
/// A token that is cancelled on a thread-pool thread when <c>source</c> is cancelled, instead of on the thread that cancelled
/// <c>source</c>. Cancelling a token runs every registered callback, and the continuations of waits that were cancelled,
/// inline on the cancelling thread. For the host's ApplicationStopping that thread is the one handling SIGTERM; anything
/// that blocks there (disposing an SSH client in the middle of a connect, for example) holds up the whole shutdown
/// (T7.R17: a 15 s exit with the connect timeout running inline).
/// </summary>
public sealed class DetachedCancellation : IDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly CancellationTokenRegistration _registration;

    public DetachedCancellation(CancellationToken source)
    {
        _registration = source.UnsafeRegister(static state =>
        {
            var self = (DetachedCancellation)state!;
            ThreadPool.UnsafeQueueUserWorkItem(static cancellation =>
            {
                // Disposed meanwhile (the owner finished first): nothing is waiting any more.
                try { cancellation.Cancel(); }
                catch (ObjectDisposedException) { }
            }, self._cancellation, preferLocal: false);
        }, this);
    }

    public CancellationToken Token => _cancellation.Token;

    public void Dispose()
    {
        _registration.Dispose();
        _cancellation.Dispose();
    }
}
