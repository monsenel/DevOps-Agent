using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Domain.Discovery;
using Microsoft.Extensions.Logging;

namespace DevOpsAgent.Application.Discovery;

/// <summary>
/// Runs discovery work inside a host-owned DI scope bound to application shutdown rather than a Blazor circuit (T7.R8).
/// </summary>
public interface IDiscoveryScopeRunner
{
    Task<Snapshot> ExecuteAsync(
        Func<DiscoveryService, IProgress<DiscoveryProgress>, CancellationToken, Task<Snapshot>> work,
        IProgress<DiscoveryProgress> progress);
}

/// <summary>
/// App-wide single-flight gate and host-owned execution coordinator for discovery runs (T7.R8).
/// </summary>
public interface IDiscoveryCoordinator
{
    bool IsRunning { get; }
    DiscoveryProgress? LastProgress { get; }
    event Action? StateChanged;
    event Action<DiscoveryProgress>? ProgressChanged;

    Task<Snapshot> RunAsync(IProgress<DiscoveryProgress>? progress, CancellationToken cancellationToken);
    Task<Snapshot> RunServerAndMergeAsync(Guid serverId, IProgress<DiscoveryProgress>? progress, CancellationToken cancellationToken);
}

public sealed class DiscoveryCoordinator(
    IDiscoveryScopeRunner scopeRunner,
    ILogger<DiscoveryCoordinator> logger) : IDiscoveryCoordinator, IDisposable
{
    private readonly Lock _sync = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Task<Snapshot>? _activeFullRun;
    private int _activeRuns;
    private DiscoveryProgress? _lastProgress;

    public bool IsRunning => Volatile.Read(ref _activeRuns) > 0;
    public DiscoveryProgress? LastProgress => Volatile.Read(ref _lastProgress);

    public event Action? StateChanged;
    public event Action<DiscoveryProgress>? ProgressChanged;

    public Task<Snapshot> RunAsync(IProgress<DiscoveryProgress>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<Snapshot> runTask;
        lock (_sync)
        {
            if (_activeFullRun is { IsCompleted: false } existing)
            {
                runTask = existing;
            }
            else
            {
                runTask = StartFullRun();
                _activeFullRun = runTask;
            }
        }

        return AwaitWithProgressAsync(runTask, progress, cancellationToken);
    }

    public Task<Snapshot> RunServerAndMergeAsync(
        Guid serverId,
        IProgress<DiscoveryProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var runTask = StartScopedRun((discovery, broadcast, hostStopping) =>
            discovery.RunServerAndMergeAsync(serverId, broadcast, hostStopping));
        return AwaitWithProgressAsync(runTask, progress, cancellationToken);
    }

    private Task<Snapshot> StartFullRun()
    {
        var task = StartScopedRun((discovery, broadcast, hostStopping) =>
            discovery.RunAsync(broadcast, hostStopping));
        _ = task.ContinueWith(_ =>
        {
            lock (_sync)
            {
                if (ReferenceEquals(_activeFullRun, task))
                {
                    _activeFullRun = null;
                }
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    private Task<Snapshot> StartScopedRun(
        Func<DiscoveryService, IProgress<DiscoveryProgress>, CancellationToken, Task<Snapshot>> work)
    {
        var task = Task.Run(async () =>
        {
            await _gate.WaitAsync(CancellationToken.None);
            Interlocked.Increment(ref _activeRuns);
            Volatile.Write(ref _lastProgress, null);
            NotifyStateChanged();
            try
            {
                var broadcast = new SynchronousProgress(ReportProgress);
                return await scopeRunner.ExecuteAsync(work, broadcast);
            }
            finally
            {
                Interlocked.Decrement(ref _activeRuns);
                _gate.Release();
                NotifyStateChanged();
            }
        }, CancellationToken.None);

        // If every caller disconnected (e.g. closed browser tab) and the run faults, observe the exception so it never becomes unobserved.
        _ = task.ContinueWith(faulted =>
        {
            logger.LogWarning(faulted.Exception?.GetBaseException(), "Host-owned discovery run did not complete.");
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        return task;
    }

    private async Task<Snapshot> AwaitWithProgressAsync(
        Task<Snapshot> runTask,
        IProgress<DiscoveryProgress>? progress,
        CancellationToken cancellationToken)
    {
        Action<DiscoveryProgress>? handler = null;
        if (progress is not null)
        {
            handler = item => progress.Report(item);
            ProgressChanged += handler;
            if (LastProgress is { } current)
            {
                progress.Report(current);
            }
        }

        try
        {
            return await runTask.WaitAsync(cancellationToken);
        }
        finally
        {
            if (handler is not null)
            {
                ProgressChanged -= handler;
            }
        }
    }

    private void ReportProgress(DiscoveryProgress item)
    {
        Volatile.Write(ref _lastProgress, item);
        ProgressChanged?.Invoke(item);
    }

    private void NotifyStateChanged() => StateChanged?.Invoke();

    public void Dispose() => _gate.Dispose();

    private sealed class SynchronousProgress(Action<DiscoveryProgress> callback) : IProgress<DiscoveryProgress>
    {
        public void Report(DiscoveryProgress value) => callback(value);
    }
}
