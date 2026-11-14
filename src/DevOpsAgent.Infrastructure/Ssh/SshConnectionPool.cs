using System.Collections.Concurrent;
using System.Net.Sockets;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Domain.Servers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace DevOpsAgent.Infrastructure.Ssh;

public sealed class SshConnectionPool(
    IServiceScopeFactory scopeFactory,
    ISecretProtector secretProtector,
    IServerKeyStore keyStore,
    IOptions<SshConnectionOptions> options) : ISshConnectionPool
{
    private readonly ConcurrentDictionary<Guid, ConnectionEntry> _connections = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _connectionLocks = new();
    // Bumped by Evict so a connect that started with old server settings never caches its result.
    private readonly ConcurrentDictionary<Guid, int> _generations = new();
    private readonly SshConnectionOptions _options = options.Value;
    private bool _disposed;

    public async Task<ISshCommandSession> GetSessionAsync(Guid serverId, CancellationToken cancellationToken)
    {
        var connection = await GetOrConnectAsync(serverId, [], cancellationToken);
        return new SshCommandSession(connection.Client, () =>
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
                var jumpConnection = server.JumpServerId is Guid jumpId
                    ? await GetOrConnectAsync(jumpId, visited, cancellationToken)
                    : null;

                var gate = _connectionLocks.GetOrAdd(serverId, static _ => new SemaphoreSlim(1, 1));
                await gate.WaitAsync(cancellationToken);
                try
                {
                    if (_connections.TryGetValue(serverId, out var current)
                        && current.Client.IsConnected
                        && (jumpConnection is null || jumpConnection.Client.IsConnected)
                        && ReferenceEquals(current.JumpConnection, jumpConnection))
                    {
                        return current;
                    }

                    RemoveConnection(serverId);
                    var created = await CreateConnectionAsync(server, jumpConnection, cancellationToken);
                    _connections[serverId] = created;
                    if (Generation(serverId) == generation) return created;

                    // Evicted while connecting: do not keep a connection built from stale settings.
                    Invalidate(serverId, created);
                }
                finally
                {
                    gate.Release();
                }
            }

            throw new InvalidOperationException("The server settings changed while connecting. Try again.");
        }
        finally
        {
            visited.Remove(serverId);
        }
    }

    private async Task<Server> LoadServerAsync(Guid serverId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IServerRepository>();
        return await repository.GetAsync(serverId, cancellationToken)
            ?? throw new KeyNotFoundException("Server was not found in inventory.");
    }

    private async Task<ConnectionEntry> CreateConnectionAsync(
        Server server,
        ConnectionEntry? jumpConnection,
        CancellationToken cancellationToken)
    {
        ForwardedPortLocal? tunnel = null;
        PrivateKeyFile? privateKey = null;
        SshClient? client = null;
        string? rejectedFingerprint = null;
        try
        {
            var host = server.Host;
            var port = server.Port;
            if (jumpConnection is not null)
            {
                tunnel = new ForwardedPortLocal("127.0.0.1", 0, server.Host, checked((uint)server.Port));
                jumpConnection.Client.AddForwardedPort(tunnel);
                tunnel.Start();
                if (!tunnel.IsStarted || tunnel.BoundPort == 0)
                {
                    throw new InvalidOperationException("The jump-host tunnel did not start.");
                }

                host = "127.0.0.1";
                port = checked((int)tunnel.BoundPort);
            }

            var keyPath = keyStore.GetFullPath(server.KeyFile);
            var passphrase = string.IsNullOrEmpty(server.ProtectedKeyPassphrase)
                ? null
                : secretProtector.Unprotect(server.ProtectedKeyPassphrase);
            privateKey = new PrivateKeyFile(keyPath, passphrase);
            var authentication = new PrivateKeyAuthenticationMethod(server.User, privateKey);
            var connectionInfo = new ConnectionInfo(host, port, server.User, authentication)
            {
                Timeout = TimeSpan.FromSeconds(Math.Clamp(_options.ConnectionTimeoutSeconds, 1, 300))
            };

            client = new SshClient(connectionInfo)
            {
                KeepAliveInterval = TimeSpan.FromSeconds(Math.Clamp(_options.KeepAliveSeconds, 0, 3600))
            };
            var knownHosts = new KnownHostsVerifier(_options.KnownHostsFile);
            client.HostKeyReceived += (_, eventArgs) =>
            {
                eventArgs.CanTrust = knownHosts.Matches(server.Host, server.Port, eventArgs.HostKey);
                if (!eventArgs.CanTrust)
                {
                    rejectedFingerprint = eventArgs.FingerPrintSHA256;
                }
            };

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.ConnectionTimeoutSeconds, 1, 300)));
            await client.ConnectAsync(timeout.Token);
            return new ConnectionEntry(client, privateKey, tunnel, jumpConnection);
        }
        catch (Exception) when (rejectedFingerprint is not null)
        {
            client?.Dispose();
            privateKey?.Dispose();
            ReleaseTunnel(tunnel, jumpConnection);
            throw new SshHostKeyMismatchException(server.Host, server.Port, rejectedFingerprint);
        }
        catch
        {
            client?.Dispose();
            privateKey?.Dispose();
            ReleaseTunnel(tunnel, jumpConnection);
            throw;
        }
    }

    // Stops the tunnel and detaches it from the jump client, which would otherwise keep every dead tunnel registered.
    private static void ReleaseTunnel(ForwardedPortLocal? tunnel, ConnectionEntry? jumpConnection)
    {
        if (tunnel is null) return;
        // The jump client may already be disposed or disconnected (for example when it was replaced first).
        try { tunnel.Stop(); } catch (Exception exception) when (exception is ObjectDisposedException or SshException) { }
        try { jumpConnection?.Client.RemoveForwardedPort(tunnel); } catch (Exception exception) when (exception is ObjectDisposedException or SshException) { }
        tunnel.Dispose();
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

    private sealed class ConnectionEntry(
        SshClient client,
        PrivateKeyFile privateKey,
        ForwardedPortLocal? tunnel,
        ConnectionEntry? jumpConnection) : IDisposable
    {
        public SshClient Client { get; } = client;
        public ConnectionEntry? JumpConnection { get; } = jumpConnection;

        public void Dispose()
        {
            Client.Dispose();
            ReleaseTunnel(tunnel, JumpConnection);
            privateKey.Dispose();
        }
    }

    private sealed class SshCommandSession(SshClient client, Func<Task> onConnectionFailure) : ISshCommandSession
    {
        private static bool IsClientConnected(SshClient value)
        {
            try { return value.IsConnected; }
            catch (ObjectDisposedException) { return false; }
        }

        public bool IsConnected => client.IsConnected;

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
                using var sshCommand = client.CreateCommand(command);
                sshCommand.CommandTimeout = timeout;
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                await sshCommand.ExecuteAsync(cancellationToken);
                stopwatch.Stop();
                return new RemoteCommandResult(
                    sshCommand.ExitStatus,
                    sshCommand.Result,
                    sshCommand.Error,
                    stopwatch.Elapsed);
            }
            catch (Exception exception) when (SshTransportFailure.IsTransportFailure(exception) && !IsClientConnected(client))
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
