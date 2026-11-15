using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Domain.Servers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
/// </summary>
public sealed class SshConnectionPool(
    IServiceScopeFactory scopeFactory,
    ISshTransportFactory transportFactory,
    IHostReachability reachability,
    ILogger<SshConnectionPool> logger,
    IHostApplicationLifetime? lifetime = null) : ISshConnectionPool
{
    private readonly ConcurrentDictionary<Guid, ConnectionEntry> _connections = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _connectionLocks = new();
    // Bumped by Evict so a connect that started with old server settings never caches its result.
    private readonly ConcurrentDictionary<Guid, int> _generations = new();
    // Every client that has not been disposed yet: the pooled ones plus retired ones still draining their last leases.
    private readonly ConcurrentDictionary<ConnectionEntry, byte> _live = new();
    // ApplicationStopping, re-delivered on a thread-pool thread: everything the pool cancels (and every continuation of an
    // abandoned wait) then runs there, never inline on the thread that handles SIGTERM. Left undisposed on purpose: calls in
    // flight when the pool closes may still hold a token from it, and it lives exactly as long as the host.
    private readonly DetachedCancellation? _stopSignal = lifetime is null ? null : new DetachedCancellation(lifetime.ApplicationStopping);
    private bool _disposed;

    /// <summary>Commands currently running on pooled clients (diagnostics and tests).</summary>
    public int ActiveLeases => _live.Keys.Sum(entry => entry.Leases);

    /// <summary>Clients that are open: pooled ones and retired ones still finishing a command (diagnostics and tests).</summary>
    public int LiveClients => _live.Count;

    // Cancelled when the host starts stopping (SIGTERM): the point after which nothing may start or keep waiting. Checked
    // through the host's own token (immediate), while waits are linked to StoppingSignal (delivered on a pool thread).
    private CancellationToken Stopping => lifetime?.ApplicationStopping ?? CancellationToken.None;

    private CancellationToken StoppingSignal => _stopSignal?.Token ?? CancellationToken.None;

    private bool IsStopping => _disposed || Stopping.IsCancellationRequested;

    private IHostReachability Reachability => reachability;

    private ILogger Log => logger;

    public Task<ISshCommandSession> GetSessionAsync(Guid serverId, bool bypassBackoff, CancellationToken cancellationToken) =>
        GetSessionAsync(serverId, bypassBackoff, Timeout.InfiniteTimeSpan, cancellationToken);

    public async Task<ISshCommandSession> GetSessionAsync(Guid serverId, bool bypassBackoff, TimeSpan deadline, CancellationToken cancellationToken)
    {
        // Once the host is stopping, a new request is refused before anything is loaded or connected.
        if (IsStopping) throw new SshShutdownException();

        var startedAt = Stopwatch.GetTimestamp();
        using var scope = new CallScope(cancellationToken, Stopping, StoppingSignal, deadline);
        try
        {
            var lease = await GetOrConnectAsync(serverId, [], bypassBackoff, scope);
            var budget = deadline == Timeout.InfiniteTimeSpan ? (TimeSpan?)null : deadline;
            return new SshCommandSession(this, lease, serverId, bypassBackoff, startedAt, budget,
                reason => Invalidate(serverId, lease.Entry, reason));
        }
        catch (OperationCanceledException) when (scope.ShuttingDown)
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
        if (_disposed) return;
        _generations.AddOrUpdate(serverId, 1, static (_, current) => current + 1);
        // New settings deserve an immediate attempt rather than the old settings' backoff.
        reachability.Reset(serverId);
        RemoveConnection(serverId, "server settings changed");
    }

    private int Generation(Guid serverId) => _generations.GetValueOrDefault(serverId);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // Shutdown closes every client at once, leased or not. Dependents first, so a tunnel is released while its jump
        // client is still open. Nothing here waits for a connect in flight: such an attempt notices the pool is closed (or
        // the stopping token) and ends itself, and a connection it finishes late is closed by its caller. The connect
        // gates are deliberately not disposed: a caller still holding one must be able to release it (disposing made that
        // throw ObjectDisposedException into the host).
        _disposed = true;
        foreach (var connection in _live.Keys.OrderByDescending(entry => entry.Depth))
        {
            connection.Shutdown();
        }

        _live.Clear();
        _connections.Clear();
    }

    // The returned lease belongs to the caller, who must dispose it (the session does; a jump lease is released once the
    // dependent entry has taken its own).
    private async Task<EntryLease> GetOrConnectAsync(
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
                EntryLease? jumpLease = null;
                try
                {
                    if (server.JumpServerId is Guid jumpId)
                    {
                        try
                        {
                            jumpLease = await GetOrConnectAsync(jumpId, visited, bypassBackoff, scope);
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
                    if (connection is not null) return connection;
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

        if (TryReuse(server.Id, jumpConnection) is { } pooled) return pooled;

        // A host in backoff, or one another caller is already retrying, fails immediately.
        var claimedRetry = reachability.Get(server.Id) is not null;
        if (!reachability.TryBeginAttempt(server.Id, bypassBackoff, out var blockedBy))
        {
            throw new HostUnreachableException(server.Id, server.Name, blockedBy!);
        }

        var outcomeRecorded = false;
        try
        {
            var gate = _connectionLocks.GetOrAdd(server.Id, static _ => new SemaphoreSlim(1, 1));
            // ForceYielding: when this wait is cancelled, what follows must not run on the thread that cancelled it.
            await gate.WaitAsync(scope.Token).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            try
            {
                // Another caller's abandoned attempt released the gate because the host is stopping: nothing starts now.
                if (IsStopping) throw new SshShutdownException();

                if (TryReuse(server.Id, jumpConnection) is { } current) return current;

                // Waited behind an attempt that just failed: do not try again until the backoff expires.
                if (!claimedRetry && !bypassBackoff && reachability.IsBlocked(server.Id, out var failedMeanwhile))
                {
                    throw new HostUnreachableException(server.Id, server.Name, failedMeanwhile!);
                }

                RemoveConnection(server.Id, "replaced by a new connection");
                logger.LogDebug("SSH connect attempt to {ServerName}", server.Name);
                ISshTransport transport;
                try
                {
                    transport = await ConnectAbandonableAsync(server, jumpConnection?.Transport, scope.Token);
                }
                catch (Exception exception) when (scope.ShuttingDown || IsStopping)
                {
                    // Shutdown aborted the attempt (or tore the client down under it): not a failure of the server, so it
                    // never backs the host off or opens an alert.
                    logger.LogDebug("SSH connect to {ServerName} abandoned: the application is stopping", server.Name);
                    throw exception is SshShutdownException ? exception : new SshShutdownException();
                }
                catch (Exception exception) when (!(exception is OperationCanceledException && scope.CallerCancelled))
                {
                    // Evicted while connecting: the failure belongs to the old settings and must not back off the new ones.
                    if (Generation(server.Id) != generation) return null;

                    // Cut off by the call's own deadline: the host did not answer in time, which is what a connect timeout is.
                    var failure = exception is OperationCanceledException && scope.DeadlineExpired
                        ? new SshOperationTimeoutException(
                            $"Connecting to {server.Host}:{server.Port} did not complete within the call deadline.")
                        : exception;
                    var state = reachability.RecordFailure(server.Id, failure);
                    outcomeRecorded = true;
                    logger.LogInformation("{ServerName} is unreachable (could not connect); next retry at {NextRetryUtc:u}", server.Name, state.NextRetryUtc);
                    throw new HostUnreachableException(server.Id, server.Name, state);
                }

                if (IsStopping)
                {
                    // Connected just as the host started stopping: nothing may outlive the shutdown.
                    transport.Dispose();
                    throw new SshShutdownException();
                }

                reachability.RecordSuccess(server.Id);
                outcomeRecorded = true;
                // The new entry holds its own lease on the jump entry until it is disposed, so the tunnel it runs through
                // outlives every command on it.
                var created = new ConnectionEntry(server.Id, server.Name, transport, jumpConnection, jumpLease?.Entry.RetainAsJump(), logger, Forget);
                _live[created] = 0;
                var lease = created.Retain();
                if (IsStopping)
                {
                    // Shut down while connecting: nothing may outlive the pool.
                    created.Shutdown();
                    lease.Dispose();
                    throw new SshShutdownException();
                }

                _connections[server.Id] = created;
                logger.LogDebug("SSH client for {ServerName} connected{ViaJump}", server.Name,
                    jumpConnection is null ? string.Empty : $" through {jumpConnection.ServerName}");
                if (Generation(server.Id) == generation) return lease;

                // Evicted while connecting: do not keep a connection built from stale settings.
                lease.Dispose();
                Invalidate(server.Id, created, "server settings changed while connecting");
                return null;
            }
            finally
            {
                gate.Release();
            }
        }
        finally
        {
            if (!outcomeRecorded) reachability.EndAttempt(server.Id);
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
        new(exception.ServerId, exception.UnreachableSinceUtc, exception.NextRetryUtc, 1,
            exception.LastFailure?.Message ?? exception.Message, false, exception.LastFailure, exception.Kind);

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
                if (task.IsCompletedSuccessfully) task.Result.Dispose();
                else _ = task.Exception; // observed, never rethrown
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
        if (_disposed) return;
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

    /// <summary>
    /// The three reasons a call stops waiting, kept apart: its caller cancelled, the host is stopping, or the call's
    /// deadline (connect time included) passed.
    /// </summary>
    private sealed class CallScope : IDisposable
    {
        private readonly CancellationToken _caller;
        private readonly CancellationToken _stopping;
        private readonly CancellationTokenSource _deadline = new();
        private readonly CancellationTokenSource _linked;

        // stopping answers "is the host stopping" at once; stoppingSignal is what the call's waits are linked to.
        public CallScope(CancellationToken caller, CancellationToken stopping, CancellationToken stoppingSignal, TimeSpan deadline)
        {
            _caller = caller;
            _stopping = stopping;
            if (deadline != Timeout.InfiniteTimeSpan) _deadline.CancelAfter(deadline);
            _linked = CancellationTokenSource.CreateLinkedTokenSource(caller, stoppingSignal, _deadline.Token);
        }

        public CancellationToken Token => _linked.Token;
        public bool CallerCancelled => _caller.IsCancellationRequested;
        public bool ShuttingDown => _stopping.IsCancellationRequested && !_caller.IsCancellationRequested;
        public bool DeadlineExpired => _deadline.IsCancellationRequested && !_caller.IsCancellationRequested && !_stopping.IsCancellationRequested;

        public void Dispose()
        {
            _linked.Dispose();
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

        /// <summary>A new command lease for the caller that created the entry.</summary>
        public EntryLease Retain()
        {
            lock (_sync)
            {
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

        public async Task<RemoteCommandResult> ExecuteAsync(
            string command,
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
            var stopping = pool.Stopping;

            timeout = WithinCallDeadline(timeout);
            var transport = Entry.Transport;
            // The call is bounded here as well as inside SSH.NET: its own timeout and cancellation do not always end a
            // command on a dead peer, and Dispose does not wake a command already running (T7.R15 live run). The wait is
            // abandoned when the deadline passes, the caller cancels or the host stops; a late result is discarded.
            using var deadline = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, pool.StoppingSignal, deadline.Token);
            var running = transport.ExecuteAsync(command, timeout, linked.Token);
            try
            {
                var result = await running.WaitAsync(linked.Token).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
                Timeouts.RecordSuccess();
                pool.Reachability.RecordCommandSuccess(serverId);
                return result;
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                Abandon(running);
                if (cancellationToken.IsCancellationRequested) throw;
                if (stopping.IsCancellationRequested) throw new SshShutdownException();
                throw RegisterTimeout(new SshOperationTimeoutException(
                    $"The command did not finish within {timeout.TotalSeconds:0.#} s."));
            }
            catch (Exception) when (stopping.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // The host is stopping and tore the client down under the command.
                throw new SshShutdownException();
            }
            catch (SshOperationTimeoutException exception)
            {
                throw RegisterTimeout(exception);
            }
            catch (Exception exception) when (SshTransportFailure.IsTransportFailure(exception) && !transport.IsConnected)
            {
                // INV-5: reconnect on failure. Only a dead transport poisons the shared client; a command timeout merely
                // ends this command's channel and must not tear down other commands or jump tunnels sharing the client.
                retire("connection lost");
                throw;
            }
        }

        // The first command of a session has only what is left of the call's deadline after connecting.
        private TimeSpan WithinCallDeadline(TimeSpan timeout)
        {
            if (budget is not { } total || Interlocked.Exchange(ref _budgetUsed, 1) != 0) return timeout;
            var remaining = total - Stopwatch.GetElapsedTime(startedAt);
            if (remaining <= TimeSpan.Zero)
            {
                throw new SshOperationTimeoutException(
                    $"Connecting used up the {total.TotalSeconds:0.#} s deadline of the call; the command was not started.");
            }

            return remaining < timeout ? remaining : timeout;
        }

        // A silently dead peer (blackholed route, frozen process) keeps IsConnected true and every command waits its full
        // timeout. One slow command is tolerated. Three in a row on this client recycle it: it is retired, never closed
        // under other commands (for example a running package update), and the last lease to end disposes it. The host
        // itself goes into backoff when the timeouts continue on the fresh client (IHostReachability.CommandTimeoutThreshold),
        // or at once when it was already in backoff and this was its retry.
        private SshOperationTimeoutException RegisterTimeout(SshOperationTimeoutException exception)
        {
            var recycle = Timeouts.RecordTimeout();
            var backoff = pool.Reachability.RecordCommandTimeout(serverId, exception, force: bypassBackoff);
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
