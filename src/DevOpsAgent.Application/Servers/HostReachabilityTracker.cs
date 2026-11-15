using System.Collections.Concurrent;
using DevOpsAgent.Application.Abstractions;

namespace DevOpsAgent.Application.Servers;

/// <summary>
/// In-memory per-host backoff: 1 minute after the first failure, doubling per further failure up to 15 minutes. Connection
/// failures and repeated command timeouts (T7.R17) share the same schedule. Login failures (T7.R19) are a state of their
/// own with a constant 1 minute retry, and after <see cref="IHostReachability.AuthFailedAttemptLimit"/> in a row the host is
/// paused: no automatic attempt at all until the owner retries, tests or edits it (D-039). The state lives in memory only, so
/// an application restart forgets it.
/// </summary>
public sealed class HostReachabilityTracker(TimeProvider timeProvider) : IHostReachability
{
    public static readonly TimeSpan InitialBackoff = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaximumBackoff = TimeSpan.FromMinutes(15);

    /// <summary>
    /// A failed login is retried every minute, never less often (T7.R19): it is a configuration problem the owner is about
    /// to fix, not an outage to be left alone, and each retry is a single cheap handshake.
    /// </summary>
    public static readonly TimeSpan AuthFailedBackoff = TimeSpan.FromMinutes(1);

    /// <summary>
    /// A failed login may be retried this much before its minute is over. Retries are driven by the 30 s alert and 60 s metric
    /// cycles; a cycle that lands a few milliseconds before the deadline (always, when the failure itself happened in a
    /// cycle: the deadline is the attempt time plus a minute plus the attempt's own duration) would otherwise push the retry a
    /// whole cycle later and make a "1 minute" retry a 90 second one (measured live). Only login failures get it; the
    /// backoff of an outage is unchanged.
    /// </summary>
    public static readonly TimeSpan AuthFailedRetrySlack = TimeSpan.FromSeconds(1);

    private readonly ConcurrentDictionary<Guid, HostReachabilityState> _states = new();
    // Consecutive command timeouts per host. They outlive a recycled client: the count only ends with a successful command.
    private readonly ConcurrentDictionary<Guid, int> _timeouts = new();
    private readonly object _gate = new();

    public HostReachabilityState? Get(Guid serverId) => _states.GetValueOrDefault(serverId);

    public bool IsBlocked(Guid serverId, out HostReachabilityState? state)
    {
        state = Get(serverId);
        return state is not null && (state.RetryInProgress || !Elapsed(state, timeProvider.GetUtcNow()));
    }

    // The retry time of a failed login is reached AuthFailedRetrySlack early (see there). A host whose automatic retries are
    // paused never reaches it: only a bypass attempt or Reset gets past.
    private static bool Elapsed(HostReachabilityState state, DateTimeOffset now) =>
        !state.AutomaticRetriesPaused
        && now >= (state.Kind == HostFailureKind.AuthFailed ? state.NextRetryUtc - AuthFailedRetrySlack : state.NextRetryUtc);

    public bool TryBeginAttempt(Guid serverId, bool bypassBackoff, out HostReachabilityState? blockedBy)
    {
        lock (_gate)
        {
            blockedBy = null;
            if (!_states.TryGetValue(serverId, out var state)) return true;
            if (!bypassBackoff && (state.RetryInProgress || !Elapsed(state, timeProvider.GetUtcNow())))
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
            // A connection includes the login, so it ends a connection failure and a login failure alike.
            if (state.Kind is HostFailureKind.Connect or HostFailureKind.AuthFailed)
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

    public HostReachabilityState RecordFailure(Guid serverId, Exception failure, string? cause = null)
    {
        ArgumentNullException.ThrowIfNull(failure);
        lock (_gate)
        {
            var now = timeProvider.GetUtcNow();
            var kind = failure is SshAuthFailedException ? HostFailureKind.AuthFailed : HostFailureKind.Connect;
            _states.TryGetValue(serverId, out var previous);
            // A change between "cannot connect" and "cannot log in" is a new situation: its count and its "since" start over,
            // so a login failure never inherits the long backoff of an outage (and the other way round).
            var continuing = previous is not null && (previous.Kind == HostFailureKind.AuthFailed) == (kind == HostFailureKind.AuthFailed);
            // A failure of the other kind (or none on record) starts the count at 1, so only consecutive login failures count
            // towards the pause. A failed login stays at the limit once paused: a manual attempt that fails again keeps the
            // host paused and never pushes the count further.
            var failures = continuing ? previous!.ConsecutiveFailures + 1 : 1;
            if (kind == HostFailureKind.AuthFailed) failures = Math.Min(failures, IHostReachability.AuthFailedAttemptLimit);
            var paused = kind == HostFailureKind.AuthFailed && failures >= IHostReachability.AuthFailedAttemptLimit;
            // A host that cannot even be connected to is now unreachable, whatever it was before.
            return _states[serverId] = new HostReachabilityState(serverId, continuing ? previous!.UnreachableSinceUtc : now,
                paused ? DateTimeOffset.MaxValue : now + BackoffFor(kind, failures), failures, failure.Message, RetryInProgress: false, failure, kind,
                cause ?? (failure is SshAuthFailedException auth ? SshAuthFailedException.Describe(auth.Reason) : null));
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
                // A connection or login failure already explains the host; a timeout that was already running when the
                // backoff began (or when this retry was claimed by another caller) must not escalate it a second time.
                if (previous.Kind is HostFailureKind.Connect or HostFailureKind.AuthFailed) return null;
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

    /// <summary>The wait after the <paramref name="consecutiveFailures"/>th failure of the given kind.</summary>
    public static TimeSpan BackoffFor(HostFailureKind kind, int consecutiveFailures) =>
        kind == HostFailureKind.AuthFailed ? AuthFailedBackoff : BackoffFor(consecutiveFailures);

    public static TimeSpan BackoffFor(int consecutiveFailures)
    {
        var doublings = Math.Clamp(consecutiveFailures - 1, 0, 10);
        var backoff = TimeSpan.FromTicks(InitialBackoff.Ticks << doublings);
        return backoff < MaximumBackoff ? backoff : MaximumBackoff;
    }
}
