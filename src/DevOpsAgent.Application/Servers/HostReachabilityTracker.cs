using System.Collections.Concurrent;
using DevOpsAgent.Application.Abstractions;

namespace DevOpsAgent.Application.Servers;

/// <summary>In-memory per-host backoff: 1 minute after the first failure, doubling per further failure up to 15 minutes.</summary>
public sealed class HostReachabilityTracker(TimeProvider timeProvider) : IHostReachability
{
    public static readonly TimeSpan InitialBackoff = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaximumBackoff = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<Guid, HostReachabilityState> _states = new();
    private readonly object _gate = new();

    public HostReachabilityState? Get(Guid serverId) => _states.GetValueOrDefault(serverId);

    public bool IsBlocked(Guid serverId, out HostReachabilityState? state)
    {
        state = Get(serverId);
        return state is not null && (state.RetryInProgress || timeProvider.GetUtcNow() < state.NextRetryUtc);
    }

    public bool TryBeginAttempt(Guid serverId, bool bypassBackoff, out HostReachabilityState? blockedBy)
    {
        lock (_gate)
        {
            blockedBy = null;
            if (!_states.TryGetValue(serverId, out var state)) return true;
            if (!bypassBackoff && (state.RetryInProgress || timeProvider.GetUtcNow() < state.NextRetryUtc))
            {
                blockedBy = state;
                return false;
            }

            // One retry at a time: everyone else is told the host is unreachable instead of queueing behind it.
            _states[serverId] = state with { RetryInProgress = true };
            return true;
        }
    }

    public void RecordSuccess(Guid serverId)
    {
        lock (_gate) _states.TryRemove(serverId, out _);
    }

    public HostReachabilityState RecordFailure(Guid serverId, Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        lock (_gate)
        {
            var now = timeProvider.GetUtcNow();
            var failures = _states.TryGetValue(serverId, out var previous) ? previous.ConsecutiveFailures + 1 : 1;
            return _states[serverId] = new HostReachabilityState(serverId, previous?.UnreachableSinceUtc ?? now,
                now + BackoffFor(failures), failures, failure.Message, RetryInProgress: false, failure);
        }
    }

    public void EndAttempt(Guid serverId)
    {
        lock (_gate)
        {
            if (_states.TryGetValue(serverId, out var state) && state.RetryInProgress)
                _states[serverId] = state with { RetryInProgress = false };
        }
    }

    public void Reset(Guid serverId)
    {
        lock (_gate) _states.TryRemove(serverId, out _);
    }

    public static TimeSpan BackoffFor(int consecutiveFailures)
    {
        var doublings = Math.Clamp(consecutiveFailures - 1, 0, 10);
        var backoff = TimeSpan.FromTicks(InitialBackoff.Ticks << doublings);
        return backoff < MaximumBackoff ? backoff : MaximumBackoff;
    }
}
