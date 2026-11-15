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
    private bool _disposed;

    public async Task<ISshCommandSession> GetSessionAsync(Guid serverId, bool bypassBackoff, CancellationToken cancellationToken)
    {
        var connection = await GetOrConnectAsync(serverId, [], bypassBackoff, cancellationToken);
        return new SshCommandSession(connection.Transport, connection.Timeouts, () =>
        {
            Invalidate(serverId, connection);
            return Task.CompletedTask;
        });
    }

    public void Evict(Guid serverId)
    {
        // Sessions that depend on this entry as a jump host notice the replaced entry on their next request and reconnect.
        if (_disposed) return;
        _generations.AddOrUpdate(serverId, 1, static (_, current) => current + 1);
        // New settings deserve an immediate attempt rather than the old settings' backoff.
        reachability.Reset(serverId);
        RemoveConnection(serverId);
    }

    private int Generation(Guid serverId) => _generations.GetValueOrDefault(serverId);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var connection in _connections.Values)
        {
            connection.Dispose();
        }

        _connections.Clear();
        foreach (var gate in _connectionLocks.Values)
        {
            gate.Dispose();
        }

        _connectionLocks.Clear();
    }

    private async Task<ConnectionEntry> GetOrConnectAsync(
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
                ConnectionEntry? jumpConnection = null;
                if (server.JumpServerId is Guid jumpId)
                {
                    try
                    {
                        jumpConnection = await GetOrConnectAsync(jumpId, visited, bypassBackoff, cancellationToken);
                    }
                    catch (HostUnreachableException jumpUnreachable)
                    {
                        // No connection is attempted to a server whose jump host is unreachable.
                        var state = reachability.Get(jumpUnreachable.ServerId) ?? StateFrom(jumpUnreachable);
                        throw new HostUnreachableException(server.Id, server.Name, state,
                            jumpUnreachable.ViaJumpServerName ?? jumpUnreachable.ServerName);
                    }
                }

                var connection = await ConnectHopAsync(server, jumpConnection, generation, bypassBackoff, cancellationToken);
                if (connection is not null) return connection;
            }

            throw new InvalidOperationException("The server settings changed while connecting. Try again.");
        }
        finally
        {
            visited.Remove(serverId);
        }
    }

    private async Task<ConnectionEntry?> ConnectHopAsync(
        Server server,
        ConnectionEntry? jumpConnection,
        int generation,
        bool bypassBackoff,
        CancellationToken cancellationToken)
    {
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

                RemoveConnection(server.Id);
                logger.LogDebug("SSH connect attempt to {ServerName}", server.Name);
                ISshTransport transport;
                try
                {
                    transport = await transportFactory.ConnectAsync(server, jumpConnection?.Transport, cancellationToken);
                }
                catch (Exception exception) when (!(exception is OperationCanceledException && cancellationToken.IsCancellationRequested))
                {
                    reachability.RecordFailure(server.Id, exception);
                    outcomeRecorded = true;
                    logger.LogInformation("{ServerName} is unreachable; next retry at {NextRetryUtc:u}", server.Name,
                        reachability.Get(server.Id)!.NextRetryUtc);
                    throw new HostUnreachableException(server.Id, server.Name, reachability.Get(server.Id)!);
                }

                reachability.RecordSuccess(server.Id);
                outcomeRecorded = true;
                var created = new ConnectionEntry(transport, jumpConnection);
                _connections[server.Id] = created;
                if (Generation(server.Id) == generation) return created;

                // Evicted while connecting: do not keep a connection built from stale settings.
                Invalidate(server.Id, created);
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

    private ConnectionEntry? TryReuse(Guid serverId, ConnectionEntry? jumpConnection) =>
        _connections.TryGetValue(serverId, out var current)
        && current.Transport.IsConnected
        && (jumpConnection is null || jumpConnection.Transport.IsConnected)
        && ReferenceEquals(current.JumpConnection, jumpConnection)
            ? current
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

    // A connection-level failure leaves a poisoned client in the pool; drop exactly that entry (not a newer replacement)
    // so the next request reconnects.
    private void Invalidate(Guid serverId, ConnectionEntry entry)
    {
        // TryRemove(KeyValuePair) is identity-checked, so a newer replacement entry is never removed.
        if (!_disposed && _connections.TryRemove(new KeyValuePair<Guid, ConnectionEntry>(serverId, entry)))
        {
            entry.Dispose();
        }
    }

    private void RemoveConnection(Guid serverId)
    {
        if (_connections.TryRemove(serverId, out var previous))
        {
            previous.Dispose();
        }
    }

    private sealed class ConnectionEntry(ISshTransport transport, ConnectionEntry? jumpConnection) : IDisposable
    {
        public ISshTransport Transport { get; } = transport;
        public ConnectionEntry? JumpConnection { get; } = jumpConnection;
        public SshTimeoutTracker Timeouts { get; } = new();

        public void Dispose() => Transport.Dispose();
    }

    private sealed class SshCommandSession(ISshTransport transport, SshTimeoutTracker timeouts, Func<Task> onConnectionFailure) : ISshCommandSession
    {
        public bool IsConnected => transport.IsConnected;

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

            try
            {
                var result = await transport.ExecuteAsync(command, timeout, cancellationToken);
                timeouts.RecordSuccess();
                return result;
            }
            catch (SshOperationTimeoutException) when (timeouts.RecordTimeout())
            {
                // A silently dead peer (blackholed route) keeps IsConnected true and every command waits its full timeout.
                // One slow command is tolerated; repeated consecutive timeouts mean the session is dead, so recycle it.
                await onConnectionFailure();
                throw;
            }
            catch (Exception exception) when (SshTransportFailure.IsTransportFailure(exception) && !transport.IsConnected)
            {
                // INV-5: reconnect on failure. Only a dead transport poisons the shared client; a command timeout merely
                // ends this command's channel and must not tear down other commands or jump tunnels sharing the client.
                await onConnectionFailure();
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
