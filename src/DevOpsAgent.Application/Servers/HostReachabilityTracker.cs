using System.Collections.Concurrent;
using DevOpsAgent.Application.Abstractions;

namespace DevOpsAgent.Application.Servers;

/// <summary>
/// In-memory per-host backoff: 1 minute after the first failure, doubling per further failure up to 15 minutes. Connection
/// failures and repeated command timeouts (T7.R17) share the same schedule.
/// </summary>
public sealed class HostReachabilityTracker(TimeProvider timeProvider) : IHostReachability
{
    public static readonly TimeSpan InitialBackoff = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaximumBackoff = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<Guid, HostReachabilityState> _states = new();
    // Consecutive command timeouts per host. They outlive a recycled client: the count only ends with a successful command.
    private readonly ConcurrentDictionary<Guid, int> _timeouts = new();
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
        lock (_gate)
        {
            if (!_states.TryGetValue(serverId, out var state)) return;
            if (state.Kind == HostFailureKind.Connect)
            {
                _states.TryRemove(serverId, out _);
                _timeouts.TryRemove(serverId, out _);
            }
            else if (state.RetryInProgress)
            {
                // Connected, but the host's commands are what failed: keep the backoff until a command succeeds, and let
                // callers through to prove it.
                _states[serverId] = state with { RetryInProgress = false };
            }
        }
    }

    public HostReachabilityState RecordFailure(Guid serverId, Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        lock (_gate)
        {
            var now = timeProvider.GetUtcNow();
            var failures = _states.TryGetValue(serverId, out var previous) ? previous.ConsecutiveFailures + 1 : 1;
            // A host that cannot even be connected to is now unreachable, whatever it was before.
            return _states[serverId] = new HostReachabilityState(serverId, previous?.UnreachableSinceUtc ?? now,
                now + BackoffFor(failures), failures, failure.Message, RetryInProgress: false, failure, HostFailureKind.Connect);
        }
    }

    public HostReachabilityState? RecordCommandTimeout(Guid serverId, Exception failure, bool force = false)
    {
        ArgumentNullException.ThrowIfNull(failure);
        lock (_gate)
        {
            var now = timeProvider.GetUtcNow();
            if (_states.TryGetValue(serverId, out var previous))
            {
                // A connection failure already explains the host; a timeout that was already running when the backoff
                // began (or when this retry was claimed by another caller) must not escalate it a second time.
                if (previous.Kind == HostFailureKind.Connect) return null;
                if (!force && now < previous.NextRetryUtc) return null;

                var failures = previous.ConsecutiveFailures + 1;
                return _states[serverId] = previous with
                {
                    NextRetryUtc = now + BackoffFor(failures),
                    ConsecutiveFailures = failures,
                    LastError = failure.Message,
                    RetryInProgress = false,
                    LastFailure = failure
                };
            }

            var timeouts = _timeouts.AddOrUpdate(serverId, 1, static (_, count) => count + 1);
            if (timeouts < IHostReachability.CommandTimeoutThreshold) return null;

            // From here the backoff state carries the escalation; the count starts again once the host answers.
            _timeouts.TryRemove(serverId, out _);
            return _states[serverId] = new HostReachabilityState(serverId, now, now + BackoffFor(1), 1, failure.Message,
                RetryInProgress: false, failure, HostFailureKind.CommandTimeout);
        }
    }

    public void RecordCommandSuccess(Guid serverId)
    {
        // The hot path (every command): nothing to do unless timeouts were counted or a command-timeout backoff is open.
        if (_timeouts.IsEmpty && _states.IsEmpty) return;
        lock (_gate)
        {
            _timeouts.TryRemove(serverId, out _);
            if (_states.TryGetValue(serverId, out var state) && state.Kind == HostFailureKind.CommandTimeout)
            {
                _states.TryRemove(serverId, out _);
            }
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
        lock (_gate)
        {
            _states.TryRemove(serverId, out _);
            _timeouts.TryRemove(serverId, out _);
        }
    }

    public static TimeSpan BackoffFor(int consecutiveFailures)
    {
        var doublings = Math.Clamp(consecutiveFailures - 1, 0, 10);
        var backoff = TimeSpan.FromTicks(InitialBackoff.Ticks << doublings);
        return backoff < MaximumBackoff ? backoff : MaximumBackoff;
    }
}
