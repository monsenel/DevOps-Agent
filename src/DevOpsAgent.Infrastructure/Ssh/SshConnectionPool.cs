using System.Collections.Concurrent;
using System.Net.Sockets;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Domain.Servers;
using Microsoft.Extensions.DependencyInjection;
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
/// </summary>
public sealed class SshConnectionPool(
    IServiceScopeFactory scopeFactory,
    ISshTransportFactory transportFactory,
    IHostReachability reachability,
    ILogger<SshConnectionPool> logger) : ISshConnectionPool
{
    private readonly ConcurrentDictionary<Guid, ConnectionEntry> _connections = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _connectionLocks = new();
    // Bumped by Evict so a connect that started with old server settings never caches its result.
    private readonly ConcurrentDictionary<Guid, int> _generations = new();
    // Every client that has not been disposed yet: the pooled ones plus retired ones still draining their last leases.
    private readonly ConcurrentDictionary<ConnectionEntry, byte> _live = new();
    private bool _disposed;

    /// <summary>Commands currently running on pooled clients (diagnostics and tests).</summary>
    public int ActiveLeases => _live.Keys.Sum(entry => entry.Leases);

    /// <summary>Clients that are open: pooled ones and retired ones still finishing a command (diagnostics and tests).</summary>
    public int LiveClients => _live.Count;

    public async Task<ISshCommandSession> GetSessionAsync(Guid serverId, bool bypassBackoff, CancellationToken cancellationToken)
    {
        var lease = await GetOrConnectAsync(serverId, [], bypassBackoff, cancellationToken);
        return new SshCommandSession(lease, reason => Invalidate(serverId, lease.Entry, reason));
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

        // Shutdown closes every client at once, leased or not; commands still running fail with the closed connection.
        // Dependents first, so a tunnel is released while its jump client is still open.
        _disposed = true;
        foreach (var connection in _live.Keys.OrderByDescending(entry => entry.Depth))
        {
            connection.Shutdown();
        }

        _live.Clear();
        _connections.Clear();
        foreach (var gate in _connectionLocks.Values)
        {
            gate.Dispose();
        }

        _connectionLocks.Clear();
    }

    // The returned lease belongs to the caller, who must dispose it (the session does; a jump lease is released once the
    // dependent entry has taken its own).
    private async Task<EntryLease> GetOrConnectAsync(
        Guid serverId,
        HashSet<Guid> visited,
        bool bypassBackoff,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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
                var server = await LoadServerAsync(serverId, cancellationToken);
                EntryLease? jumpLease = null;
                try
                {
                    if (server.JumpServerId is Guid jumpId)
                    {
                        try
                        {
                            jumpLease = await GetOrConnectAsync(jumpId, visited, bypassBackoff, cancellationToken);
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
                    var connection = await ConnectHopAsync(server, jumpLease, generation, bypassBackoff, cancellationToken);
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
        CancellationToken cancellationToken)
    {
        var jumpConnection = jumpLease?.Entry;
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
            await gate.WaitAsync(cancellationToken);
            try
            {
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
                    transport = await transportFactory.ConnectAsync(server, jumpConnection?.Transport, cancellationToken);
                }
                catch (Exception exception) when (!(exception is OperationCanceledException && cancellationToken.IsCancellationRequested))
                {
                    // Evicted while connecting: the failure belongs to the old settings and must not back off the new ones.
                    if (Generation(server.Id) != generation) return null;

                    var state = reachability.RecordFailure(server.Id, exception);
                    outcomeRecorded = true;
                    logger.LogInformation("{ServerName} is unreachable; next retry at {NextRetryUtc:u}", server.Name, state.NextRetryUtc);
                    throw new HostUnreachableException(server.Id, server.Name, state);
                }

                reachability.RecordSuccess(server.Id);
                outcomeRecorded = true;
                // The new entry holds its own lease on the jump entry until it is disposed, so the tunnel it runs through
                // outlives every command on it.
                var created = new ConnectionEntry(server.Id, server.Name, transport, jumpConnection, jumpLease?.Entry.RetainAsJump(), logger, Forget);
                _live[created] = 0;
                var lease = created.Retain();
                if (_disposed)
                {
                    // Shut down while connecting: nothing may outlive the pool.
                    created.Shutdown();
                    lease.Dispose();
                    throw new ObjectDisposedException(nameof(SshConnectionPool));
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
            exception.LastFailure?.Message ?? exception.Message, false, exception.LastFailure);

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

    private sealed class SshCommandSession(EntryLease lease, Action<string> retire) : ISshCommandSession
    {
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
            var transport = Entry.Transport;
            try
            {
                var result = await transport.ExecuteAsync(command, timeout, cancellationToken);
                Timeouts.RecordSuccess();
                return result;
            }
            catch (SshOperationTimeoutException) when (Timeouts.RecordTimeout())
            {
                // A silently dead peer (blackholed route) keeps IsConnected true and every command waits its full timeout.
                // One slow command is tolerated; repeated consecutive timeouts mean the session is dead, so retire it.
                // Retiring never closes the client under other commands (for example a running package update): they
                // finish, and the last lease to end disposes it.
                retire("repeated command timeouts");
                throw;
            }
            catch (Exception exception) when (SshTransportFailure.IsTransportFailure(exception) && !transport.IsConnected)
            {
                // INV-5: reconnect on failure. Only a dead transport poisons the shared client; a command timeout merely
                // ends this command's channel and must not tear down other commands or jump tunnels sharing the client.
                retire("connection lost");
                throw;
            }
        }
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
