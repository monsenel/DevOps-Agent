using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Domain.Servers;
using Microsoft.Extensions.Options;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace DevOpsAgent.Infrastructure.Ssh;

/// <summary>One live SSH connection. The seam between the pool's routing/backoff logic and SSH.NET, so it can be faked in tests.</summary>
public interface ISshTransport : IDisposable
{
    bool IsConnected { get; }
    Task<RemoteCommandResult> ExecuteAsync(string command, TimeSpan timeout, CancellationToken cancellationToken);
}

public interface ISshTransportFactory
{
    /// <summary>Connects to <paramref name="server"/>, through <paramref name="jump"/> when it has a jump host.</summary>
    Task<ISshTransport> ConnectAsync(Server server, ISshTransport? jump, CancellationToken cancellationToken);
}

public sealed class SshNetTransportFactory(
    ISecretProtector secretProtector,
    IServerKeyStore keyStore,
    IOptions<SshConnectionOptions> options) : ISshTransportFactory
{
    private readonly SshConnectionOptions _options = options.Value;

    public async Task<ISshTransport> ConnectAsync(Server server, ISshTransport? jump, CancellationToken cancellationToken)
    {
        var jumpTransport = jump as SshNetTransport;
        if (jump is not null && jumpTransport is null) throw new ArgumentException("The jump connection is not an SSH.NET transport.", nameof(jump));

        ForwardedPortLocal? tunnel = null;
        PrivateKeyFile? privateKey = null;
        SshClient? client = null;
        string? rejectedFingerprint = null;
        try
        {
            var host = server.Host;
            var port = server.Port;
            if (jumpTransport is not null)
            {
                tunnel = new ForwardedPortLocal("127.0.0.1", 0, server.Host, checked((uint)server.Port));
                jumpTransport.Client.AddForwardedPort(tunnel);
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
            try
            {
                await client.ConnectAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new SshOperationTimeoutException($"Connecting to {server.Host}:{server.Port} timed out.");
            }

            return new SshNetTransport(client, privateKey, tunnel, jumpTransport);
        }
        catch (Exception) when (rejectedFingerprint is not null)
        {
            client?.Dispose();
            privateKey?.Dispose();
            SshNetTransport.ReleaseTunnel(tunnel, jumpTransport);
            throw new SshHostKeyMismatchException(server.Host, server.Port, rejectedFingerprint);
        }
        catch
        {
            client?.Dispose();
            privateKey?.Dispose();
            SshNetTransport.ReleaseTunnel(tunnel, jumpTransport);
            throw;
        }
    }
}

public sealed class SshNetTransport(
    SshClient client,
    PrivateKeyFile privateKey,
    ForwardedPortLocal? tunnel,
    SshNetTransport? jump) : ISshTransport
{
    internal SshClient Client { get; } = client;

    public bool IsConnected
    {
        get
        {
            try { return Client.IsConnected; }
            catch (ObjectDisposedException) { return false; }
        }
    }

    public async Task<RemoteCommandResult> ExecuteAsync(string command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var sshCommand = Client.CreateCommand(command);
        sshCommand.CommandTimeout = timeout;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await sshCommand.ExecuteAsync(cancellationToken);
        stopwatch.Stop();
        return new RemoteCommandResult(sshCommand.ExitStatus, sshCommand.Result, sshCommand.Error, stopwatch.Elapsed);
    }

    public void Dispose()
    {
        Client.Dispose();
        ReleaseTunnel(tunnel, jump);
        privateKey.Dispose();
    }

    // Stops the tunnel and detaches it from the jump client, which would otherwise keep every dead tunnel registered.
    internal static void ReleaseTunnel(ForwardedPortLocal? tunnel, SshNetTransport? jump)
    {
        if (tunnel is null) return;
        // The jump client may already be disposed or disconnected (for example when it was replaced first).
        try { tunnel.Stop(); } catch (Exception exception) when (exception is ObjectDisposedException or SshException) { }
        try { jump?.Client.RemoveForwardedPort(tunnel); } catch (Exception exception) when (exception is ObjectDisposedException or SshException) { }
        tunnel.Dispose();
    }
}
