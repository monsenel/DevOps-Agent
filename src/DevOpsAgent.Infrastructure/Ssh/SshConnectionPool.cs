using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Domain.Servers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Renci.SshNet.Common;

namespace DevOpsAgent.Infrastructure.Ssh;

/// <summary>
/// Pooled SSH connections, one per server, routed through jump hosts. A connection-level failure puts the host in backoff
/// (<see cref="IHostReachability"/>): until its next retry every request for it, and for every server behind it, fails
/// immediately with <see cref="HostUnreachableException"/> instead of waiting on a connect timeout (T7.R1).
/// <para>
/// Every command runs on a leased client (T7.R15, D-036). Eviction, host edits, connection failures and repeated timeouts
/// only retire an entry: it takes no new leases and is disposed when its last lease ends. A dependent entry holds a lease on
/// its jump entry, so a jump tunnel outlives the commands running through it. Only <see cref="Dispose"/> (shutdown) closes
/// clients regardless of leases.
/// </para>
/// <para>
/// T7.R17: a call has one deadline that covers connecting as well as the command; repeated command timeouts put the host in
/// the same backoff as a failed connect; and once the host application is stopping (<paramref name="lifetime"/>) no new
/// connection or command starts and the ones in flight are abandoned at once, whatever token their caller passed.
/// </para>
/// <para>
/// T7.R19 (D-039): a connect attempt is one <c>ConnectFlight</c> per server, shared by the callers that may share it; a failed
/// login (rejected key, missing, unreadable or invalid key, passphrase) is recorded as <see cref="HostFailureKind.AuthFailed"/>
/// and retried every minute, not as an outage; and closing the pool never throws into the host.
/// </para>
/// </summary>
public sealed class SshConnectionPool(
    IServiceScopeFactory scopeFactory,
    ISshTransportFactory transportFactory,
    IHostReachability reachability,
    ILogger<SshConnectionPool> logger,
    IHostApplicationLifetime? lifetime = null,
    IOptions<SshConnectionOptions>? options = null,
    TimeProvider? timeProvider = null) : ISshConnectionPool
{
    /// <summary>
    /// Commands with a timeout at or above this threshold (for example a 10-minute <c>apt upgrade</c> action) are treated as
    /// long-running work: short probe timeouts on the same host do not recycle or back it off while one is leased (T7.R21 c).
    /// </summary>
    public static readonly TimeSpan LongCommandThreshold = TimeSpan.FromMinutes(2);

    // Overall budget for disposing pooled clients on shutdown so many frozen peers never outlast compose's stop_grace_period (T7.R22).
    private static readonly TimeSpan ShutdownDisposeBudget = TimeSpan.FromMilliseconds(750);

    private readonly ConcurrentDictionary<Guid, ConnectionEntry> _connections = new();
    // The connect attempt in flight per server (T7.R19): at most one, shared by every caller that may share it. Guarded by
    // _flightSync; nothing waits while holding it.
    private readonly Dictionary<Guid, ConnectFlight> _flights = new();
    private readonly object _flightSync = new();
    // Bumped by Evict so a connect that started with old server settings never caches its result.
    private readonly ConcurrentDictionary<Guid, int> _generations = new();
    // Active long-running commands per server (T7.R21 c).
    private readonly ConcurrentDictionary<Guid, int> _longCommands = new();
    // Every client that has not been disposed yet: the pooled ones plus retired ones still draining their last leases.
    private readonly ConcurrentDictionary<ConnectionEntry, byte> _live = new();
    // ApplicationStopping, re-delivered on a thread-pool thread: everything the pool cancels (and every continuation of an
    // abandoned wait) then runs there, never inline on the thread that handles SIGTERM. Left undisposed on purpose: calls in
    // flight when the pool closes may still hold a token from it, and it lives exactly as long as the host.
    private readonly DetachedCancellation? _stopSignal = lifetime is null ? null : new DetachedCancellation(lifetime.ApplicationStopping);
    // Fired on a pool thread by Dispose() so commands racing a plain Dispose wake immediately as SshShutdownException (T7.R22).
    private readonly CancellationTokenSource _closedSignal = new();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private int _closed;

    /// <summary>Optional connect-timeout threshold override (for sub-second tests, T7.R21 b); null uses <see cref="SshConnectionOptions.ConnectionTimeoutSeconds"/>.</summary>
    public TimeSpan? ConnectTimeoutOverride { get; set; }

    private TimeSpan ConnectTimeout => ConnectTimeoutOverride
        ?? TimeSpan.FromSeconds(Math.Clamp(options?.Value.ConnectionTimeoutSeconds ?? 15, 1, 300));

    private bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>Commands currently running on pooled clients (diagnostics and tests).</summary>
    public int ActiveLeases => _live.Keys.Sum(entry => entry.Leases);

    /// <summary>Clients that are open: pooled ones and retired ones still finishing a command (diagnostics and tests).</summary>
    public int LiveClients => _live.Count;

    /// <summary>Callers currently waiting for the connect attempt in flight to a server, 0 when none (diagnostics and tests).</summary>
    public int ConnectWaiters(Guid serverId)
    {
        lock (_flightSync) return _flights.TryGetValue(serverId, out var flight) ? flight.Waiters : 0;
    }

    // Cancelled when the host starts stopping (SIGTERM): the point after which nothing may start or keep waiting. Checked
    // through the host's own token (immediate), while waits are linked to StoppingSignal (delivered on a pool thread).
    private CancellationToken Stopping => lifetime?.ApplicationStopping ?? CancellationToken.None;

    private CancellationToken StoppingSignal => _stopSignal?.Token ?? CancellationToken.None;

    private CancellationToken ClosedSignal => _closedSignal.Token;

    private bool IsStopping => IsClosed || Stopping.IsCancellationRequested;

    private IHostReachability Reachability => reachability;

    private ILogger Log => logger;

    private TimeProvider Time => _time;

    private void BeginLongCommand(Guid serverId) => _longCommands.AddOrUpdate(serverId, 1, static (_, count) => count + 1);

    private void EndLongCommand(Guid serverId)
    {
        var remaining = _longCommands.AddOrUpdate(serverId, 0, static (_, count) => count > 0 ? count - 1 : 0);
        if (remaining <= 0) _longCommands.TryRemove(new KeyValuePair<Guid, int>(serverId, remaining));
    }

    private bool HasActiveLongCommand(Guid serverId) => _longCommands.GetValueOrDefault(serverId) > 0;

    public Task<ISshCommandSession> GetSessionAsync(Guid serverId, bool bypassBackoff, CancellationToken cancellationToken) =>
        GetSessionAsync(serverId, bypassBackoff, Timeout.InfiniteTimeSpan, cancellationToken);

    public async Task<ISshCommandSession> GetSessionAsync(Guid serverId, bool bypassBackoff, TimeSpan deadline, CancellationToken cancellationToken)
    {
        // Once the host is stopping, a new request is refused before anything is loaded or connected.
        if (IsStopping) throw new SshShutdownException();

        var startedAt = Stopwatch.GetTimestamp();
        using var scope = new CallScope(cancellationToken, Stopping, StoppingSignal, ClosedSignal, deadline);
        try
        {
            var (lease, generation) = await GetOrConnectAsync(serverId, [], bypassBackoff, scope);
            var budget = deadline == Timeout.InfiniteTimeSpan ? (TimeSpan?)null : deadline;
            return new SshCommandSession(this, lease, serverId, generation, bypassBackoff, startedAt, budget,
                reason => Invalidate(serverId, lease.Entry, reason));
        }
        catch (OperationCanceledException) when (scope.ShuttingDown || (IsStopping && !cancellationToken.IsCancellationRequested))
        {
            // The host is stopping: a clean cancellation, not a failure of the server.
            throw new SshShutdownException();
        }
        catch (OperationCanceledException) when (scope.DeadlineExpired)
        {
            // Out of time while waiting (for the server record, or behind another caller's connect). The caller that was
            // connecting has already recorded the failure.
            throw new SshOperationTimeoutException(
                $"Connecting did not complete within the {deadline.TotalSeconds:0.#} s deadline of the call.");
        }
    }

    public void Evict(Guid serverId)
    {
        // Sessions that depend on this entry as a jump host notice the replaced entry on their next request and reconnect.
        if (IsClosed) return;
        // T7.R23: bump generation, reset reachability, and abandon any running flight atomically under _flightSync so
        // no stale failure can slip in between the generation check and RecordFailure.
        lock (_flightSync)
        {
            _generations.AddOrUpdate(serverId, 1, static (_, current) => current + 1);
            // New settings deserve an immediate attempt rather than the old settings' backoff.
            reachability.Reset(serverId);
            // A connect still running was started with the old settings: end it now so the new settings are tried at once
            // instead of after it (its callers are told to start over with the new settings).
            if (_flights.TryGetValue(serverId, out var flight)) flight.Abandon(FlightAbandon.Stale);
        }

        RemoveConnection(serverId, "server settings changed");
    }

    private int Generation(Guid serverId) => _generations.GetValueOrDefault(serverId);

    public void Dispose()
    {
        // Idempotent and safe against a second, concurrent call: only the first one does the work.
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        // Wake any command racing a plain Dispose on a pool thread so it exits as SshShutdownException, never ObjectDisposedException (T7.R22).
        ThreadPool.UnsafeQueueUserWorkItem(static cts =>
        {
            try { cts.Cancel(); }
            catch (Exception) { }
        }, _closedSignal, preferLocal: false);

        // Shutdown closes every client at once, leased or not. Dependents first, so a tunnel is released while its jump
        // client is still open. Nothing here waits for a connect in flight: such an attempt is told to stop (on a pool
        // thread, so no callback of the connect runs here), notices the pool is closed and closes whatever it opened late.
        // Nothing the pool owns is disposed while a caller can still reach it (connect attempts own their cancellation
        // source and complete it themselves), so no caller can be thrown an ObjectDisposedException by this method.
        lock (_flightSync)
        {
            foreach (var flight in _flights.Values) flight.Abandon(FlightAbandon.Closed);
        }

        // T7.R22: dispose pooled clients in parallel per depth level on thread-pool threads with a tight overall budget,
        // so multiple frozen peers do not serialize shutdown past the container stop grace period.
        var groups = _live.Keys
            .GroupBy(entry => entry.Depth)
            .OrderByDescending(group => group.Key)
            .ToArray();
        var deadline = Stopwatch.StartNew();
        foreach (var group in groups)
        {
            var tasks = group.Select(static entry => Task.Run(entry.Shutdown)).ToArray();
            var remaining = ShutdownDisposeBudget - deadline.Elapsed;
            if (remaining > TimeSpan.Zero)
            {
                try { Task.WaitAll(tasks, remaining); }
                catch (AggregateException) { /* Shutdown() already catches transport errors */ }
            }
        }

        _live.Clear();
        _connections.Clear();
    }

    // The returned lease belongs to the caller, who must dispose it (the session does; a jump lease is released once the
    // dependent entry has taken its own).
    private async Task<(EntryLease Lease, int Generation)> GetOrConnectAsync(
        Guid serverId,
        HashSet<Guid> visited,
        bool bypassBackoff,
        CallScope scope)
    {
        if (IsStopping) throw new SshShutdownException();
        if (!visited.Add(serverId))
        {
            throw new InvalidOperationException("The configured jump-server route contains a cycle.");
        }

        try
        {
            // A connect that raced with Evict (settings edited while it was connecting) is discarded and retried once.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var generation = Generation(serverId);
                var server = await LoadServerAsync(serverId, scope.Token);
                // T7.R19 v2 jump order: check the target server's own backoff before connecting its jump host, so a server
                // already in backoff does not cost a jump-host connect.
                if (!bypassBackoff && reachability.IsBlocked(server.Id, out var ownBlocked))
                {
                    throw new HostUnreachableException(server.Id, server.Name, ownBlocked!);
                }

                EntryLease? jumpLease = null;
                try
                {
                    if (server.JumpServerId is Guid jumpId)
                    {
                        try
                        {
                            (jumpLease, _) = await GetOrConnectAsync(jumpId, visited, bypassBackoff, scope);
                        }
                        catch (HostUnreachableException jumpUnreachable)
                        {
                            // No connection is attempted to a server whose jump host is unreachable.
                            var state = reachability.Get(jumpUnreachable.ServerId) ?? StateFrom(jumpUnreachable);
                            throw new HostUnreachableException(server.Id, server.Name, state,
                                jumpUnreachable.ViaJumpServerName ?? jumpUnreachable.ServerName);
                        }
                    }

                    // The jump lease keeps the tunnel's client open while this hop connects through it.
                    var connection = await ConnectHopAsync(server, jumpLease, generation, bypassBackoff, scope);
                    if (connection is not null) return (connection, generation);
                }
                finally
                {
                    jumpLease?.Dispose();
                }
            }

            throw new InvalidOperationException("The server settings changed while connecting. Try again.");
        }
        finally
        {
            visited.Remove(serverId);
        }
    }

    // Bounded: every round either returns, throws or finds the situation changed by another caller (a flight that ended, a
    // connection that was retired in between). Eight changes in a row mean something is wrong, not that waiting helps.
    private const int MaxConnectRounds = 8;

    private async Task<EntryLease?> ConnectHopAsync(
        Server server,
        EntryLease? jumpLease,
        int generation,
        bool bypassBackoff,
        CallScope scope)
    {
        var jumpConnection = jumpLease?.Entry;

        // A host whose commands keep timing out is skipped even though its client may still be connected (T7.R17): the
        // backoff must hold until the host answers a command, not only until a connect succeeds.
        if (!bypassBackoff && reachability.Get(server.Id) is { Kind: HostFailureKind.CommandTimeout }
            && reachability.IsBlocked(server.Id, out var commandBackoff))
        {
            throw new HostUnreachableException(server.Id, server.Name, commandBackoff!);
        }

        for (var round = 0; round < MaxConnectRounds; round++)
        {
            if (IsStopping) throw new SshShutdownException();
            if (TryReuse(server.Id, jumpConnection) is { } pooled) return pooled;

            // Decide, under one lock, whether to reuse a just-published client, share the attempt that is running, wait for
            // it to end, fail fast because the host is in backoff, or start the attempt. Nothing is awaited inside.
            EntryLease? racedReuse = null;
            ConnectFlight? flight = null;
            ConnectFlight? waitFor = null;
            HostReachabilityState? blockedBy = null;
            var stale = false;
            var started = false;
            EntryLease? flightJumpLease = null;
            lock (_flightSync)
            {
                // T7.R23: re-check generation under the lock so a flight never starts with settings edited a moment before.
                if (Generation(server.Id) != generation)
                {
                    stale = true;
                }
                // T7.R22: re-check TryReuse under the lock so a caller that missed a just-published client reuses it
                // instead of starting a second flight and retiring the fresh client.
                else if (TryReuse(server.Id, jumpConnection) is { } justPublished)
                {
                    racedReuse = justPublished;
                }
                else if (_flights.TryGetValue(server.Id, out var running))
                {
                    if (running.CanJoin(generation, jumpConnection, bypassBackoff) && running.TryJoin()) flight = running;
                    // A retry of a host in backoff: everyone but "Retry now" is told at once, as before (D-032).
                    else if (!bypassBackoff && !running.Shared && reachability.Get(server.Id) is { } retrying) blockedBy = retrying;
                    else waitFor = running;
                }
                else
                {
                    // The first connect of a host (no failure on record) is shared by whoever arrives while it runs; a retry
                    // of a host in backoff is the retry of whoever claimed it.
                    var shared = reachability.Get(server.Id) is null;
                    if (!reachability.TryBeginAttempt(server.Id, bypassBackoff, out blockedBy, out var claimToken))
                    {
                        // Told at once: in backoff, or another retry is in flight.
                    }
                    else
                    {
                        // The attempt holds its own reference on the jump client (the caller's lease may end before it does).
                        flightJumpLease = jumpLease?.Entry.RetainAsJump();
                        flight = new ConnectFlight(server.Id, generation, claimToken, jumpConnection, bypassBackoff, shared, StoppingSignal);
                        _flights[server.Id] = flight;
                        started = true;
                    }
                }
            }

            if (stale) return null;
            if (racedReuse is not null) return racedReuse;
            if (blockedBy is not null) throw new HostUnreachableException(server.Id, server.Name, blockedBy);

            if (waitFor is not null)
            {
                // Another caller's attempt this one may not share (a retry that "Retry now" must follow with its own, an
                // attempt with other settings or another jump client): wait for it to end, then look again.
                await WaitForEndAsync(waitFor, scope);
                continue;
            }

            if (started) _ = RunFlightAsync(flight!, server, jumpConnection, flightJumpLease);

            var outcome = await AwaitFlightAsync(flight!, scope);
            switch (outcome.Kind)
            {
                case FlightResult.Connected:
                    // Every caller of the attempt takes its own lease on the client it opened.
                    if (TryReuse(server.Id, jumpConnection) is { } leased) return leased;
                    continue;
                case FlightResult.Failed:
                    // The same failure, at the same moment, for everyone who waited on this attempt.
                    throw new HostUnreachableException(server.Id, server.Name, outcome.State!);
                default:
                    if (IsStopping) throw new SshShutdownException();
                    // Evicted while connecting: the caller starts over with the new settings.
                    if (Generation(server.Id) != generation) return null;
                    continue;
            }
        }

        throw new InvalidOperationException("The SSH connection to the server kept changing while connecting. Try again.");
    }

    // The caller waits for the attempt with its own deadline and cancellation. Leaving early never cancels an attempt other
    // callers still wait for; the last caller to leave ends it. A deadline only counts as a failed connect when this hop's
    // budget was at least the connect timeout (T7.R21 b); a shorter caller deadline abandons without backing the host off.
    private async Task<FlightOutcome> AwaitFlightAsync(ConnectFlight flight, CallScope scope)
    {
        var hopBudget = scope.Remaining;
        var left = false;
        try
        {
            return await flight.Result.WaitAsync(scope.Token).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        }
        catch (OperationCanceledException) when (scope.Token.IsCancellationRequested)
        {
            left = true;
            var countsAsConnectFailure = scope.DeadlineExpired
                && (hopBudget == Timeout.InfiniteTimeSpan || hopBudget >= ConnectTimeout);
            var reason = !scope.DeadlineExpired ? FlightAbandon.CallerCancelled
                : countsAsConnectFailure ? FlightAbandon.Deadline
                : FlightAbandon.ShortDeadline;
            var endedIt = flight.Leave(reason);
            if (endedIt && countsAsConnectFailure)
            {
                try
                {
                    var outcome = await flight.Result.WaitAsync(AbandonedAttemptGrace, scope.AbortToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
                    if (outcome.Kind == FlightResult.Failed) return outcome;
                }
                catch (Exception)
                {
                    // The attempt did not end promptly or was aborted: the caller reports its own deadline.
                }
            }

            throw;
        }
        finally
        {
            if (!left) flight.Leave(FlightAbandon.None);
        }
    }

    // T7.R21 (h): tight bound on waiting for a just-cancelled attempt to record its failure (it wakes within milliseconds),
    // so a wedged attempt never extends a call 5 s past its deadline.
    private static readonly TimeSpan AbandonedAttemptGrace = TimeSpan.FromMilliseconds(250);

    private static async Task WaitForEndAsync(ConnectFlight flight, CallScope scope)
    {
        try
        {
            await flight.Result.WaitAsync(scope.Token).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        }
        catch (OperationCanceledException) when (scope.Token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // The other caller's attempt failed in some unexpected way; that is its callers' business, not this one's.
        }
    }

    /// <summary>
    /// The connect attempt itself, run once for every caller that shares it. It owns its cancellation (the host stopping, an
    /// eviction, or every caller having left), never a caller's token, records its outcome with the reachability tracker, and
    /// registers the client it opened. It always completes <paramref name="flight"/>.
    /// </summary>
    private async Task RunFlightAsync(ConnectFlight flight, Server server, ConnectionEntry? jumpConnection, EntryLease? jumpReference)
    {
        var outcomeRecorded = false;
        var outcome = FlightOutcome.Aborted;
        Exception? unexpected = null;
        try
        {
            // Another caller's abandoned attempt, or a pool that closed, can leave nothing to do.
            if (IsStopping) return;

            RemoveConnection(server.Id, "replaced by a new connection");
            logger.LogDebug("SSH connect attempt to {ServerName}", server.Name);
            ISshTransport transport;
            try
            {
                transport = await ConnectAbandonableAsync(server, jumpConnection?.Transport, flight.Token);
            }
            catch (Exception) when (IsStopping)
            {
                // Shutdown aborted the attempt (or tore the client down under it): not a failure of the server, so it
                // never backs the host off or opens an alert.
                logger.LogDebug("SSH connect to {ServerName} abandoned: the application is stopping", server.Name);
                return;
            }
            catch (Exception exception)
            {
                var abandon = flight.AbandonReason;
                // Evicted while connecting: the failure belongs to the old settings and must not back off the new ones.
                if (abandon == FlightAbandon.Stale || Generation(server.Id) != flight.Generation) return;
                // Every caller left (cancelled, or cut by a caller deadline shorter than the connect timeout, T7.R21 b):
                // nobody is waiting for the outcome and the host did not fail.
                if (abandon is FlightAbandon.CallerCancelled or FlightAbandon.ShortDeadline && exception is OperationCanceledException) return;

                // Cut off because every caller's deadline passed (at least the connect timeout): the host did not answer
                // in time, which is what a connect timeout is.
                var failure = exception is OperationCanceledException && abandon == FlightAbandon.Deadline
                    ? new SshOperationTimeoutException(
                        $"Connecting to {server.Host}:{server.Port} did not complete within the call deadline.")
                    : exception;
                // A login failure is its own state (T7.R19); only the fixed wording of its reason is kept.
                var loginFailure = SshFailureClassifier.AsAuthFailure(failure);
                // The sentence shown to the owner is fixed; the exception's type name (never its message) is what tells
                // "cannot be read" from "wrong passphrase" or a blocked socket when it does not make sense.
                logger.LogDebug("{ServerName}: the connection attempt failed with {ErrorType}", server.Name, exception.GetType().Name);
                // T7.R23: check generation and record failure atomically under _flightSync so an Evict cannot slip between them.
                HostReachabilityState state;
                lock (_flightSync)
                {
                    if (IsStopping || flight.AbandonReason == FlightAbandon.Stale || Generation(server.Id) != flight.Generation) return;
                    state = reachability.RecordFailure(server.Id, loginFailure ?? failure, SshFailureClassifier.DescribeCause(failure));
                    outcomeRecorded = true;
                }

                if (loginFailure is null)
                    logger.LogInformation("{ServerName} is unreachable (could not connect); next retry at {NextRetryUtc:u}", server.Name, state.NextRetryUtc);
                else if (state.AutomaticRetriesPaused)
                    logger.LogWarning("{ServerName}: authentication failed ({Reason}); automatic retries are paused until Retry now, Test connection or an edit of the server", server.Name, state.Cause);
                else
                    logger.LogInformation("{ServerName}: authentication failed ({Reason}); next retry at {NextRetryUtc:u}", server.Name, state.Cause, state.NextRetryUtc);
                outcome = FlightOutcome.Failed(state);
                return;
            }

            // The new entry holds the attempt's reference on the jump entry until it is disposed, so the tunnel it runs
            // through outlives every command on it.
            ConnectionEntry? created = null;
            var discard = false;
            lock (_flightSync)
            {
                if (IsStopping || flight.AbandonReason == FlightAbandon.Stale || Generation(server.Id) != flight.Generation)
                {
                    discard = true;
                }
                else
                {
                    reachability.RecordSuccess(server.Id);
                    outcomeRecorded = true;
                    created = new ConnectionEntry(server.Id, server.Name, transport, jumpConnection, jumpReference, logger, Forget);
                    jumpReference = null;
                    _live[created] = 0;
                    _connections[server.Id] = created;
                }
            }

            if (discard)
            {
                // Connected just as the host started stopping, or after the settings changed: nothing may keep it.
                transport.Dispose();
                return;
            }

            logger.LogDebug("SSH client for {ServerName} connected{ViaJump}", server.Name,
                jumpConnection is null ? string.Empty : $" through {jumpConnection.ServerName}");
            if (IsStopping)
            {
                // Shut down while publishing: nothing may outlive the pool.
                created!.Shutdown();
                return;
            }

            outcome = FlightOutcome.Connected;
        }
        catch (Exception exception)
        {
            // A bug, not an outcome: every caller is told, and nothing stays claimed.
            unexpected = exception;
        }
        finally
        {
            try
            {
                jumpReference?.Dispose();
                if (!outcomeRecorded) reachability.EndAttempt(server.Id, flight.ClaimToken);
            }
            catch (Exception exception)
            {
                unexpected ??= exception;
            }

            lock (_flightSync)
            {
                if (_flights.TryGetValue(server.Id, out var registered) && ReferenceEquals(registered, flight)) _flights.Remove(server.Id);
            }

            flight.Complete(outcome, unexpected);
        }
    }

    // A retired entry is never leased again (TryLease refuses it), so a reuse can race with Evict but never revives a client
    // that is about to be, or already was, disposed.
    private EntryLease? TryReuse(Guid serverId, ConnectionEntry? jumpConnection) =>
        _connections.TryGetValue(serverId, out var current)
        && current.Transport.IsConnected
        && (jumpConnection is null || jumpConnection.Transport.IsConnected)
        && ReferenceEquals(current.JumpConnection, jumpConnection)
            ? current.TryLease()
            : null;

    private static HostReachabilityState StateFrom(HostUnreachableException exception) =>
        new(exception.ServerId, exception.UnreachableSinceUtc, exception.NextRetryUtc,
            exception.AutomaticRetriesPaused ? IHostReachability.AuthFailedAttemptLimit : 1,
            exception.LastFailure?.Message ?? exception.Message, false, exception.LastFailure, exception.Kind, exception.Cause);

    // The transport is asked to stop when the token fires, but SSH.NET does not always notice promptly (a handshake with a
    // blackholed peer, a channel to a dead jump target). The caller stops waiting at once; what the attempt opens later is
    // closed here, so an abandoned connect never leaks a client or keeps the host alive.
    private async Task<ISshTransport> ConnectAbandonableAsync(Server server, ISshTransport? jump, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsStopping) throw new SshShutdownException();

        var connecting = transportFactory.ConnectAsync(server, jump, cancellationToken);
        try
        {
            return await connecting.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = connecting.ContinueWith(static task =>
            {
                // A connection that finishes after the caller stopped waiting is closed, never leaked; closing must not throw
                // into whoever runs this continuation (the finalizer thread would report it as an unobserved exception).
                try
                {
                    if (task.IsCompletedSuccessfully) task.Result.Dispose();
                    else _ = task.Exception; // observed, never rethrown
                }
                catch (Exception)
                {
                    // Best effort: the connection is gone either way.
                }
            }, TaskScheduler.Default);
            throw;
        }
    }

    private async Task<Server> LoadServerAsync(Guid serverId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IServerRepository>();
        return await repository.GetAsync(serverId, cancellationToken)
            ?? throw new KeyNotFoundException("Server was not found in inventory.");
    }

    private void Forget(ConnectionEntry entry) => _live.TryRemove(entry, out _);

    // A connection-level failure leaves a poisoned client in the pool; retire exactly that entry (not a newer replacement)
    // so the next request reconnects. Commands still running on it finish first; the last lease to end disposes it.
    private void Invalidate(Guid serverId, ConnectionEntry entry, string reason)
    {
        if (IsClosed) return;
        // TryRemove(KeyValuePair) is identity-checked, so a newer replacement entry is never removed.
        _connections.TryRemove(new KeyValuePair<Guid, ConnectionEntry>(serverId, entry));
        // Idempotent: the entry may already be draining (evicted earlier, or retired by another timed-out command).
        entry.Retire(reason);
    }

    private void RemoveConnection(Guid serverId, string reason)
    {
        if (_connections.TryRemove(serverId, out var previous))
        {
            previous.Retire(reason);
        }
    }

    private enum FlightAbandon
    {
        None,
        /// <summary>Every caller that waited for the attempt cancelled: nobody wants the outcome and the host did not fail.</summary>
        CallerCancelled,
        /// <summary>Every caller's deadline passed (shorter than the connect timeout): abandon without backing the host off (T7.R21 b).</summary>
        ShortDeadline,
        /// <summary>Every caller's deadline passed (at least the connect timeout): the host did not answer in time (a failed connect).</summary>
        Deadline,
        /// <summary>The server's settings changed: the attempt belongs to the old ones.</summary>
        Stale,
        /// <summary>The pool is being disposed.</summary>
        Closed
    }

    private enum FlightResult
    {
        /// <summary>A client was opened and registered; each caller takes its own lease on it.</summary>
        Connected,
        /// <summary>The attempt failed and the host is in backoff; every caller gets the same state.</summary>
        Failed,
        /// <summary>No outcome: stopped, evicted, abandoned or nothing to do.</summary>
        Aborted
    }

    private sealed record FlightOutcome(FlightResult Kind, HostReachabilityState? State = null)
    {
        public static readonly FlightOutcome Aborted = new(FlightResult.Aborted);
        public static readonly FlightOutcome Connected = new(FlightResult.Connected);
        public static FlightOutcome Failed(HostReachabilityState state) => new(FlightResult.Failed, state);
    }

    /// <summary>
    /// One connect attempt to one server, shared by the callers that arrive while it runs (T7.R19): one attempt, one outcome,
    /// reached by all of them at the same moment. The attempt has its own cancellation, never a caller's: a caller that
    /// leaves (its deadline passes, it is cancelled) does not end an attempt that others still wait for, and the last one to
    /// leave does. The cancellation source is created and disposed here only, under the flight's lock, so cancelling and
    /// finishing can race without anyone seeing an <see cref="ObjectDisposedException"/>.
    /// </summary>
    private sealed class ConnectFlight
    {
        private readonly object _sync = new();
        private readonly CancellationTokenSource _cancellation;
        private readonly TaskCompletionSource<FlightOutcome> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _waiters = 1; // the caller that started it
        private bool _finished;
        private FlightAbandon _abandon;

        public ConnectFlight(Guid serverId, int generation, long claimToken, ConnectionEntry? jump, bool bypass, bool shared, CancellationToken stoppingSignal)
        {
            ServerId = serverId;
            Generation = generation;
            ClaimToken = claimToken;
            Jump = jump;
            Bypass = bypass;
            Shared = shared;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingSignal);
            Token = _cancellation.Token;
        }

        public Guid ServerId { get; }

        /// <summary>The server's settings generation the attempt was started with (<see cref="SshConnectionPool.Evict"/> bumps it).</summary>
        public int Generation { get; }

        /// <summary>The reachability claim token for this attempt (T7.R19 v2).</summary>
        public long ClaimToken { get; }

        /// <summary>The jump client the attempt tunnels through.</summary>
        public ConnectionEntry? Jump { get; }

        /// <summary>Started by "Test connection" / "Retry now".</summary>
        public bool Bypass { get; }

        /// <summary>
        /// The first connect of a host with no failure on record: every caller may share it. A retry of a host in backoff is
        /// its claimant's alone, as before (D-032); only a "Retry now" caller may share one that a "Retry now" started.
        /// </summary>
        public bool Shared { get; }

        public CancellationToken Token { get; }

        public Task<FlightOutcome> Result => _result.Task;

        public int Waiters
        {
            get { lock (_sync) return _finished ? 0 : _waiters; }
        }

        public FlightAbandon AbandonReason
        {
            get { lock (_sync) return _abandon; }
        }

        public bool CanJoin(int generation, ConnectionEntry? jump, bool bypass)
        {
            lock (_sync)
            {
                return !_finished && _abandon == FlightAbandon.None && Generation == generation && ReferenceEquals(Jump, jump)
                    && (Shared || (bypass && Bypass));
            }
        }

        public bool TryJoin()
        {
            lock (_sync)
            {
                if (_finished || _abandon != FlightAbandon.None) return false;
                _waiters++;
                return true;
            }
        }

        /// <summary>
        /// A caller stops waiting. The last one ends an attempt that is still running, for the given reason.
        /// </summary>
        /// <returns>True when this caller ended the attempt.</returns>
        public bool Leave(FlightAbandon reasonIfLast)
        {
            lock (_sync)
            {
                _waiters--;
                if (_waiters > 0 || _finished || _abandon != FlightAbandon.None || reasonIfLast == FlightAbandon.None) return false;
                _abandon = reasonIfLast;
            }

            QueueCancel();
            return true;
        }

        /// <summary>Ends the attempt for a reason of the pool's own (the settings changed, the pool closed).</summary>
        public void Abandon(FlightAbandon reason)
        {
            lock (_sync)
            {
                if (_finished) return;
                _abandon = reason;
            }

            QueueCancel();
        }

        /// <summary>The attempt is over: the cancellation source is released and every waiting caller continues.</summary>
        public void Complete(FlightOutcome outcome, Exception? unexpected)
        {
            lock (_sync)
            {
                _finished = true;
            }

            // Never under the lock: disposing a linked source unregisters from its parent, which can wait for a callback
            // that is running on another thread (see CancelNow).
            try { _cancellation.Dispose(); }
            catch (Exception) { /* already released */ }

            if (unexpected is not null)
            {
                _result.TrySetException(unexpected);
                // Observed here: when every caller has already left, nobody else would read it.
                _ = _result.Task.Exception;
            }
            else _result.TrySetResult(outcome);
        }

        // Cancelling runs the callbacks registered on the token (the connect's own, SSH.NET's) on the cancelling thread;
        // that thread may be a UI circuit, a caller's continuation or the one disposing the pool. A pool thread does it.
        private void QueueCancel() =>
            ThreadPool.UnsafeQueueUserWorkItem(static flight => flight.CancelNow(), this, preferLocal: false);

        private void CancelNow()
        {
            lock (_sync)
            {
                if (_finished) return;
            }

            // Not under the lock: cancelling runs the callbacks of the connect (and, through them, continuations of the
            // attempt itself) inline on this thread. Holding the lock then, while the pool's own lock is taken by those
            // continuations and the pool takes this lock under its own, is a lock-order inversion (found by the shutdown
            // race tests). The attempt may finish and release the source meanwhile; that is not an error.
            try { _cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
            catch (Exception) { /* a callback of the connect threw: the attempt is ending anyway */ }
        }
    }

    /// <summary>
    /// The three reasons a call stops waiting, kept apart: its caller cancelled, the host is stopping, or the call's
    /// deadline (connect time included) passed.
    /// </summary>
    private sealed class CallScope : IDisposable
    {
        private readonly CancellationToken _caller;
        private readonly CancellationToken _stopping;
        private readonly CancellationToken _closedSignal;
        private readonly TimeSpan _deadlineSpan;
        private readonly long _startedAt = Stopwatch.GetTimestamp();
        private readonly CancellationTokenSource _deadline = new();
        private readonly CancellationTokenSource _abort;
        private readonly CancellationTokenSource _linked;

        // stopping answers "is the host stopping" at once; stoppingSignal and closedSignal are what waits are linked to.
        public CallScope(CancellationToken caller, CancellationToken stopping, CancellationToken stoppingSignal, CancellationToken closedSignal, TimeSpan deadline)
        {
            _caller = caller;
            _stopping = stopping;
            _closedSignal = closedSignal;
            _deadlineSpan = deadline;
            if (deadline != Timeout.InfiniteTimeSpan) _deadline.CancelAfter(deadline);
            _abort = CancellationTokenSource.CreateLinkedTokenSource(caller, stoppingSignal, closedSignal);
            _linked = CancellationTokenSource.CreateLinkedTokenSource(_abort.Token, _deadline.Token);
        }

        public CancellationToken Token => _linked.Token;
        public CancellationToken AbortToken => _abort.Token;
        public TimeSpan Remaining => _deadlineSpan == Timeout.InfiniteTimeSpan
            ? Timeout.InfiniteTimeSpan
            : _deadlineSpan - Stopwatch.GetElapsedTime(_startedAt);
        public bool CallerCancelled => _caller.IsCancellationRequested;
        public bool ShuttingDown => (_stopping.IsCancellationRequested || _closedSignal.IsCancellationRequested) && !_caller.IsCancellationRequested;
        public bool DeadlineExpired => _deadline.IsCancellationRequested && !_caller.IsCancellationRequested
            && !_stopping.IsCancellationRequested && !_closedSignal.IsCancellationRequested;

        public void Dispose()
        {
            _linked.Dispose();
            _abort.Dispose();
            _deadline.Dispose();
        }
    }

    /// <summary>
    /// One leased claim on a pooled client. Disposing it is idempotent, so a lease can never be released twice.
    /// </summary>
    private sealed class EntryLease : IDisposable
    {
        private readonly ConnectionEntry _entry;
        private readonly bool _isReference;
        private int _released;

        public EntryLease(ConnectionEntry entry, bool isReference)
        {
            _entry = entry;
            _isReference = isReference;
        }

        public ConnectionEntry Entry => _entry;
        public bool IsReleased => Volatile.Read(ref _released) != 0;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) _entry.Release(_isReference);
        }
    }

    /// <summary>
    /// A pooled client with a lease count. Retiring it (eviction, host edit, failure, repeated timeouts) stops new leases;
    /// it is disposed when the last lease ends, immediately when none is held. It holds a lease on its jump entry for its
    /// whole life, released after its own client is closed.
    /// </summary>
    private sealed class ConnectionEntry
    {
        private readonly object _sync = new();
        private readonly ILogger _logger;
        private readonly Action<ConnectionEntry> _onDisposed;
        private readonly EntryLease? _jumpLease;
        private int _leases;      // every lease: commands and dependents' jump references
        private int _references; // the part of _leases held by dependent entries (jump tunnels)
        private bool _retired;
        private bool _disposed;

        public ConnectionEntry(
            Guid serverId,
            string serverName,
            ISshTransport transport,
            ConnectionEntry? jumpConnection,
            EntryLease? jumpLease,
            ILogger logger,
            Action<ConnectionEntry> onDisposed)
        {
            ServerId = serverId;
            ServerName = serverName;
            Transport = transport;
            JumpConnection = jumpConnection;
            _jumpLease = jumpLease;
            _logger = logger;
            _onDisposed = onDisposed;
        }

        public Guid ServerId { get; }
        public string ServerName { get; }
        public ISshTransport Transport { get; }
        public ConnectionEntry? JumpConnection { get; }
        public SshTimeoutTracker Timeouts { get; } = new();

        /// <summary>Commands currently running on this client (jump references held by dependent entries are not counted).</summary>
        public int Leases
        {
            get { lock (_sync) return _leases - _references; }
        }

        /// <summary>Length of the jump chain behind this entry (0 for a direct connection).</summary>
        public int Depth => JumpConnection is null ? 0 : JumpConnection.Depth + 1;

        /// <summary>A new lease, or null when the entry is retired (callers then use a fresh entry).</summary>
        public EntryLease? TryLease()
        {
            lock (_sync)
            {
                if (_retired) return null;
                _leases++;
                return new EntryLease(this, isReference: false);
            }
        }

        /// <summary>
        /// A reference held by a dependent entry whose tunnel runs through this one. Only for a caller that already holds a
        /// lease, so it is valid even if the entry has been retired meanwhile.
        /// </summary>
        public EntryLease RetainAsJump()
        {
            lock (_sync)
            {
                _leases++;
                _references++;
                return new EntryLease(this, isReference: true);
            }
        }

        public void Retire(string reason)
        {
            bool disposeNow;
            int leases;
            lock (_sync)
            {
                if (_retired) return;
                _retired = true;
                leases = _leases;
                disposeNow = leases == 0 && !_disposed;
                if (disposeNow) _disposed = true;
            }

            if (disposeNow)
            {
                _logger.LogDebug("SSH client for {ServerName} retired ({Reason}); no command is running, disposing it now", ServerName, reason);
                DisposeClient();
            }
            else
            {
                _logger.LogDebug("SSH client for {ServerName} retired ({Reason}); {Leases} command(s) still running, it is disposed when the last one ends",
                    ServerName, reason, leases);
            }
        }

        // Called only by EntryLease.Dispose, exactly once per lease.
        public void Release(bool isReference)
        {
            bool disposeNow;
            lock (_sync)
            {
                _leases--;
                if (isReference) _references--;
                disposeNow = _retired && _leases == 0 && !_disposed;
                if (disposeNow) _disposed = true;
            }

            if (disposeNow)
            {
                _logger.LogDebug("SSH client for {ServerName}: last command ended, disposing the retired client", ServerName);
                DisposeClient();
            }
        }

        /// <summary>Shutdown: closes the client now, whatever leases are held.</summary>
        public void Shutdown()
        {
            lock (_sync)
            {
                _retired = true;
                if (_disposed) return;
                _disposed = true;
            }

            DisposeClient();
        }

        private void DisposeClient()
        {
            try
            {
                Transport.Dispose();
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Disposing the SSH client for {ServerName} failed", ServerName);
            }

            _logger.LogDebug("SSH client for {ServerName} disposed", ServerName);
            _onDisposed(this);
            // The tunnel is gone; the jump client may now close once its other commands and dependents are done.
            _jumpLease?.Dispose();
        }
    }

    private sealed class SshCommandSession(
        SshConnectionPool pool,
        EntryLease lease,
        Guid serverId,
        int sessionGeneration,
        bool bypassBackoff,
        long startedAt,
        TimeSpan? budget,
        Action<string> retire) : ISshCommandSession
    {
        private int _budgetUsed;

        private ConnectionEntry Entry => lease.Entry;
        private SshTimeoutTracker Timeouts => Entry.Timeouts;

        public bool IsConnected => !lease.IsReleased && Entry.Transport.IsConnected;

        public void Dispose() => lease.Dispose();

        public Task<RemoteCommandResult> ExecuteAsync(string command, TimeSpan timeout, CancellationToken cancellationToken) =>
            ExecuteAsync(command, null, timeout, cancellationToken);

        public Task<RemoteCommandResult> ExecuteAsync(
            string command,
            string? standardInput,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            ExecuteAsync(command, standardInput, null, timeout, cancellationToken);

        public async Task<RemoteCommandResult> ExecuteAsync(
            string command,
            string? standardInput,
            OutputMarker? marker,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                throw new ArgumentException("Command is required.", nameof(command));
            }

            if (timeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout), "Command timeout must be positive.");
            }

            ObjectDisposedException.ThrowIf(lease.IsReleased, this);
            // After shutdown began nothing new starts, on a client leased earlier as well.
            if (pool.IsStopping) throw new SshShutdownException();

            var requestedTimeout = timeout;
            var commandStartedAtUtc = pool.Time.GetUtcNow();
            var commandGeneration = sessionGeneration;
            var isLongCommand = requestedTimeout >= LongCommandThreshold;
            timeout = WithinCallDeadline(requestedTimeout, out var ranFullTimeout);
            var transport = Entry.Transport;
            // The call is bounded here as well as inside SSH.NET: its own timeout and cancellation do not always end a
            // command on a dead peer, and Dispose does not wake a command already running (T7.R15 live run). The wait is
            // abandoned when the deadline passes, the caller cancels or the host stops; a late result is discarded.
            using var deadline = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, pool.StoppingSignal, pool.ClosedSignal, deadline.Token);
            if (isLongCommand) pool.BeginLongCommand(serverId);
            // SSH.NET opens the command's channel synchronously, so on a session whose peer is frozen the "async" call blocks
            // its caller for up to the connect timeout. The cycles start all their hosts from one thread: started here, one
            // frozen host would hold back every host after it and a cycle would run past its deadline.
            var running = Task.Run(() => transport.ExecuteAsync(command, standardInput, marker, timeout, linked.Token), CancellationToken.None);
            try
            {
                var result = await running.WaitAsync(linked.Token).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
                if (pool.Generation(serverId) == commandGeneration)
                {
                    Timeouts.RecordSuccess();
                    pool.Reachability.RecordCommandSuccess(serverId);
                }

                return result;
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                // T7.R27 (1): explicitly abandon stdin password writing before abandoning the background task.
                marker?.AbandonInput();
                Abandon(running);
                if (cancellationToken.IsCancellationRequested) throw;
                if (pool.IsStopping) throw new SshShutdownException();
                var timeoutException = new SshOperationTimeoutException(
                    $"The command did not finish within {timeout.TotalSeconds:0.#} s.");
                throw ShouldCountTimeout(ranFullTimeout, isLongCommand, commandGeneration)
                    ? RegisterTimeout(timeoutException, commandStartedAtUtc)
                    : timeoutException;
            }
            catch (Exception) when (pool.IsStopping && !cancellationToken.IsCancellationRequested)
            {
                // The host is stopping or the pool was disposed and tore the client down under the command (T7.R22).
                marker?.AbandonInput();
                throw new SshShutdownException();
            }
            catch (SshOperationTimeoutException exception)
            {
                marker?.AbandonInput();
                throw ShouldCountTimeout(ranFullTimeout, isLongCommand, commandGeneration)
                    ? RegisterTimeout(exception, commandStartedAtUtc)
                    : exception;
            }
            catch (Exception exception) when (SshTransportFailure.IsTransportFailure(exception) && !transport.IsConnected)
            {
                marker?.AbandonInput();
                // INV-5: reconnect on failure. Only a dead transport poisons the shared client; a command timeout merely
                // ends this command's channel and must not tear down other commands or jump tunnels sharing the client.
                retire("connection lost");
                throw;
            }
            finally
            {
                if (isLongCommand) pool.EndLongCommand(serverId);
            }
        }

        // T7.R21 (a, c, d): only count a timeout toward client recycling and hung-host backoff when the command ran its
        // full timeout (not cut short by connect time), belongs to the current settings generation, and is not a short
        // probe racing a leased long command (like an apt upgrade).
        private bool ShouldCountTimeout(bool ranFullTimeout, bool isLongCommand, int commandGeneration) =>
            ranFullTimeout
            && pool.Generation(serverId) == commandGeneration
            && (isLongCommand || !pool.HasActiveLongCommand(serverId));

        // The first command of a session has only what is left of the call's deadline after connecting.
        private TimeSpan WithinCallDeadline(TimeSpan requestedTimeout, out bool ranFullTimeout)
        {
            if (budget is not { } total || Interlocked.Exchange(ref _budgetUsed, 1) != 0)
            {
                ranFullTimeout = true;
                return requestedTimeout;
            }

            var remaining = total - Stopwatch.GetElapsedTime(startedAt);
            if (remaining <= TimeSpan.Zero)
            {
                throw new SshOperationTimeoutException(
                    $"Connecting used up the {total.TotalSeconds:0.#} s deadline of the call; the command was not started.");
            }

            var effective = remaining < requestedTimeout ? remaining : requestedTimeout;
            // T7.R21 (a): allow up to 10% scheduling/fast-connect overhead so a normal reused/fast-connect call counts,
            // while a command meaningfully truncated by a slow connect does not count toward recycle/backoff.
            ranFullTimeout = effective.Ticks >= (long)(requestedTimeout.Ticks * 0.9);
            return effective;
        }

        // A silently dead peer (blackholed route, frozen process) keeps IsConnected true and every command waits its full
        // timeout. One slow command is tolerated. Three in a row on this client recycle it: it is retired, never closed
        // under other commands (for example a running package update), and the last lease to end disposes it. The host
        // itself goes into backoff when the timeouts continue on the fresh client (IHostReachability.CommandTimeoutThreshold),
        // or at once when it was already in backoff and this was its retry.
        private SshOperationTimeoutException RegisterTimeout(SshOperationTimeoutException exception, DateTimeOffset commandStartedAtUtc)
        {
            var recycle = Timeouts.RecordTimeout();
            var backoff = pool.Reachability.RecordCommandTimeout(serverId, exception, force: bypassBackoff, commandStartedAtUtc);
            if (backoff is not null)
            {
                pool.Log.LogInformation("{ServerName} is not answering commands; next retry at {NextRetryUtc:u}",
                    Entry.ServerName, backoff.NextRetryUtc);
            }

            if (recycle || backoff is not null)
            {
                retire(recycle ? "repeated command timeouts" : "host is not answering commands");
            }

            return exception;
        }

        // The abandoned command may still finish or fail later; nobody waits for it, so its outcome is only observed.
        private static void Abandon(Task running) =>
            _ = running.ContinueWith(static task => _ = task.Exception, TaskScheduler.Default);
    }
}

/// <summary>Decides which command failures mean the pooled SSH transport itself is broken.</summary>
public static class SshTransportFailure
{
    // SshOperationTimeoutException is deliberately absent: a slow command is not a dead connection.
    public static bool IsTransportFailure(Exception exception) =>
        exception is SshConnectionException or SocketException or ObjectDisposedException;
}

/// <summary>
/// Counts consecutive command timeouts on one pooled session. A single slow command must not recycle a healthy session,
/// but <see cref="DefaultThreshold"/> in a row indicates a silently dead connection.
/// </summary>
public sealed class SshTimeoutTracker(int threshold = SshTimeoutTracker.DefaultThreshold)
{
    public const int DefaultThreshold = 3;

    private int _consecutive;

    /// <summary>Records a timeout; returns true exactly when the threshold is reached (the session should be recycled).</summary>
    public bool RecordTimeout() => Interlocked.Increment(ref _consecutive) >= threshold;

    public void RecordSuccess() => Interlocked.Exchange(ref _consecutive, 0);
}
