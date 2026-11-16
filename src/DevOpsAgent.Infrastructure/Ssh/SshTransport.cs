using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Domain.Servers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace DevOpsAgent.Infrastructure.Ssh;

/// <summary>One live SSH connection. The seam between the pool's routing/backoff logic and SSH.NET, so it can be faked in tests.</summary>
public interface ISshTransport : IDisposable
{
    bool IsConnected { get; }
    Task<RemoteCommandResult> ExecuteAsync(string command, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>
    /// Runs <paramref name="command"/> and writes <paramref name="standardInput"/> to its stdin, then closes stdin (the sudo
    /// password, T7.R24). Transports that cannot write input only accept null.
    /// </summary>
    Task<RemoteCommandResult> ExecuteAsync(string command, string? standardInput, TimeSpan timeout, CancellationToken cancellationToken) =>
        standardInput is null
            ? ExecuteAsync(command, timeout, cancellationToken)
            : throw new NotSupportedException("This SSH transport cannot write to a command's input.");

    /// <summary>
    /// Like the overload above, reading stdout as it arrives and reporting <paramref name="marker"/> as soon as its line is
    /// there (T7.R25), also when the command later times out; the marker line is removed from the returned output.
    /// </summary>
    Task<RemoteCommandResult> ExecuteAsync(string command, string? standardInput, OutputMarker? marker, TimeSpan timeout, CancellationToken cancellationToken) =>
        marker is null
            ? ExecuteAsync(command, standardInput, timeout, cancellationToken)
            : throw new NotSupportedException("This SSH transport cannot watch a command's output.");
}

public interface ISshTransportFactory
{
    /// <summary>Connects to <paramref name="server"/>, through <paramref name="jump"/> when it has a jump host.</summary>
    Task<ISshTransport> ConnectAsync(Server server, ISshTransport? jump, CancellationToken cancellationToken);
}

public sealed class SshNetTransportFactory(
    ISecretProtector secretProtector,
    IServerKeyStore keyStore,
    IOptions<SshConnectionOptions> options,
    ManagedKnownHosts hostKeys,
    ILogger<SshNetTransportFactory>? logger = null) : ISshTransportFactory
{
    private readonly SshConnectionOptions _options = options.Value;

    public async Task<ISshTransport> ConnectAsync(Server server, ISshTransport? jump, CancellationToken cancellationToken)
    {
        var jumpTransport = jump as SshNetTransport;
        if (jump is not null && jumpTransport is null) throw new ArgumentException("The jump connection is not an SSH.NET transport.", nameof(jump));

        ForwardedPortLocal? tunnel = null;
        PrivateKeyFile? privateKey = null;
        SshClient? client = null;
        KeyAuthentication? authentication = null;
        string? rejectedFingerprint = null;
        try
        {
            // The key first: a missing or unusable key is a login failure that needs no tunnel and no network at all.
            privateKey = LoadPrivateKey(server);
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

            authentication = new KeyAuthentication(server.User, privateKey);
            var connectionInfo = new ConnectionInfo(host, port, server.User, authentication)
            {
                Timeout = TimeSpan.FromSeconds(Math.Clamp(_options.ConnectionTimeoutSeconds, 1, 300))
            };

            client = new SshClient(connectionInfo)
            {
                KeepAliveInterval = TimeSpan.FromSeconds(Math.Clamp(_options.KeepAliveSeconds, 0, 3600))
            };
            // T8.4 (D-041): the owner's known_hosts decides for the hosts it lists; any other host is trusted on first use and
            // its key recorded once the connection (login included) has succeeded; a changed key is refused.
            (string Type, byte[] Key)? firstUse = null;
            client.HostKeyReceived += (_, eventArgs) =>
            {
                var decision = hostKeys.Check(server.Host, server.Port, eventArgs.HostKeyName, eventArgs.HostKey);
                eventArgs.CanTrust = decision != HostKeyDecision.Mismatch;
                if (decision == HostKeyDecision.FirstUse) firstUse = (eventArgs.HostKeyName, eventArgs.HostKey.ToArray());
                if (!eventArgs.CanTrust)
                {
                    rejectedFingerprint = eventArgs.FingerPrintSHA256;
                }
            };

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.ConnectionTimeoutSeconds, 1, 300)));
            // SSH.NET notices the token only at certain points of the handshake (and a channel opened through a blackholed
            // jump target not at all), so the wait itself is cancellable; the cleanup below disposes the client, which ends
            // the abandoned attempt in the background.
            var connecting = client.ConnectAsync(timeout.Token);
            try
            {
                await connecting.WaitAsync(timeout.Token).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _ = connecting.ContinueWith(static task => _ = task.Exception, TaskScheduler.Default);
                throw new SshOperationTimeoutException($"Connecting to {server.Host}:{server.Port} timed out.");
            }
            catch (OperationCanceledException)
            {
                _ = connecting.ContinueWith(static task => _ = task.Exception, TaskScheduler.Default);
                throw;
            }

            if (firstUse is { } recorded)
            {
                await hostKeys.RecordFirstUseAsync(server.Host, server.Port, recorded.Type, recorded.Key, CancellationToken.None);
            }

            return new SshNetTransport(client, privateKey, tunnel, jumpTransport);
        }
        catch (Exception) when (rejectedFingerprint is not null)
        {
            CleanUp();
            throw new SshHostKeyMismatchException(server.Host, server.Port, rejectedFingerprint);
        }
        catch (Exception exception) when (exception is SshAuthenticationException or SshAuthFailedException)
        {
            // The server answered and refused the key (or the key could not be used): a login failure, not an outage. The
            // library's message is dropped on purpose; only the fixed wording of the reason travels on.
            CleanUp();
            throw SshFailureClassifier.AsAuthFailure(exception)!;
        }
        catch (Exception exception) when (exception is SshConnectionException && authentication is { Attempted: true })
        {
            // The server hung up while the key was being offered (sshd closes the socket at MaxAuthTries or under a login
            // penalty instead of answering "denied"). That is a failed login, not an outage: left as a connection failure it
            // would restart the count of consecutive failed logins every time and the retry cap (D-039) would never apply.
            CleanUp();
            throw new SshAuthFailedException(SshAuthFailure.ServerClosedDuringLogin);
        }
        catch (Exception exception)
        {
            // Disposing an SshClient that is still connecting blocks until its socket gives up (the connect timeout, for a
            // blackholed route), and this catch can run inline on the thread that cancelled the attempt - on shutdown, the
            // SIGTERM thread. An abandoned attempt is therefore cleaned up in the background.
            if (exception is OperationCanceledException or SshOperationTimeoutException) _ = Task.Run(CleanUp);
            else CleanUp();
            throw;
        }

        void CleanUp()
        {
            try
            {
                client?.Dispose();
                privateKey?.Dispose();
                SshNetTransport.ReleaseTunnel(tunnel, jumpTransport);
            }
            catch (Exception)
            {
                // Best effort: the attempt is over either way and nothing is waiting for the cleanup.
            }
        }
    }

    /// <summary>
    /// Loads the server's identity file. Every way this can fail is a login failure with fixed wording (missing file,
    /// unreadable file, passphrase needed, wrong passphrase or not a key); the exceptions themselves, which can name the
    /// file, are not passed on.
    /// </summary>
    private PrivateKeyFile LoadPrivateKey(Server server)
    {
        try
        {
            var keyPath = keyStore.GetFullPath(server.KeyFile);
            var passphrase = string.IsNullOrEmpty(server.ProtectedKeyPassphrase)
                ? null
                : secretProtector.Unprotect(server.ProtectedKeyPassphrase);
            return new PrivateKeyFile(keyPath, passphrase);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The owner sees a fixed sentence; what actually failed (a type name, never a message, a path or a passphrase) is
            // logged so "cannot be read" can be told from "wrong passphrase" when the key will not load.
            logger?.LogWarning("{ServerName}: the identity key could not be loaded ({ErrorType})", server.Name, exception.GetType().Name);
            throw SshFailureClassifier.AsAuthFailure(exception)
                ?? new SshAuthFailedException(exception is IOException
                    ? SshAuthFailure.KeyFileUnreadable
                    : SshAuthFailure.KeyInvalidOrWrongPassphrase);
        }
    }
}

/// <summary>
/// The key login of a connect, remembering that the key was offered: a server that drops the connection after that point
/// refused the login (a login limit hangs up instead of replying "denied"), it did not fail to be reached.
/// </summary>
internal sealed class KeyAuthentication(string username, PrivateKeyFile key) : PrivateKeyAuthenticationMethod(username, key)
{
    private int _attempted;

    public bool Attempted => Volatile.Read(ref _attempted) != 0;

    public override AuthenticationResult Authenticate(Session session)
    {
        Volatile.Write(ref _attempted, 1);
        return base.Authenticate(session);
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

    public Task<RemoteCommandResult> ExecuteAsync(string command, TimeSpan timeout, CancellationToken cancellationToken) =>
        ExecuteAsync(command, null, timeout, cancellationToken);

    public Task<RemoteCommandResult> ExecuteAsync(string command, string? standardInput, TimeSpan timeout, CancellationToken cancellationToken) =>
        ExecuteAsync(command, standardInput, null, timeout, cancellationToken);

    public async Task<RemoteCommandResult> ExecuteAsync(
        string command, string? standardInput, OutputMarker? marker, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var sshCommand = Client.CreateCommand(command);
        sshCommand.CommandTimeout = timeout;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var executing = sshCommand.ExecuteAsync(cancellationToken);
        // With a marker stdout is read here as it arrives (SSH.NET's OutputStream can be read while the command runs; Result
        // then holds only what was not read), so the marker is known the moment sudo let the command start.
        var reading = marker is null ? null : ReadOutputAsync(sshCommand.OutputStream, marker);
        if (standardInput is not null)
        {
            // The input goes over the command's own channel and is closed at once, so the command sees EOF after it. Without
            // input stdin is never opened, exactly as before.
            var bytes = System.Text.Encoding.UTF8.GetBytes(standardInput);
            try
            {
                await using var input = sshCommand.CreateInputStream();
                await input.WriteAsync(bytes, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The command may already have ended (a NOPASSWD rule does not read the password), or the channel failed;
                // either way the command's own outcome below is what counts.
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
            }
        }

        try
        {
            await executing;
        }
        catch when (reading is not null)
        {
            // The command failed or timed out; disposing it ends the read. Whether the marker came back is already recorded.
            _ = reading.ContinueWith(static task => _ = task.Exception, TaskScheduler.Default);
            throw;
        }

        // The output stream ends just after the command does.
        var output = reading is null ? sshCommand.Result : await reading.WaitAsync(timeout, cancellationToken);
        stopwatch.Stop();
        return new RemoteCommandResult(sshCommand.ExitStatus, output, sshCommand.Error, stopwatch.Elapsed);
    }

    // Reads stdout to its end, reporting the marker once the first line is complete; the marker line is not part of the result.
    private static async Task<string> ReadOutputAsync(Stream output, OutputMarker marker)
    {
        using var collected = new MemoryStream();
        var buffer = new byte[8192];
        var expected = System.Text.Encoding.UTF8.GetBytes(marker.Value + "\n");
        var decided = false;
        int read;
        while ((read = await output.ReadAsync(buffer)) > 0)
        {
            collected.Write(buffer, 0, read);
            if (decided) continue;
            var sofar = collected.GetBuffer().AsSpan(0, (int)collected.Length);
            if (sofar.Length >= expected.Length || sofar.Contains((byte)'\n'))
            {
                decided = true;
                if (sofar.StartsWith(expected)) marker.MarkSeen();
            }
        }

        var text = System.Text.Encoding.UTF8.GetString(collected.GetBuffer(), 0, (int)collected.Length);
        return marker.Strip(text);
    }

    private int _disposed;

    /// <summary>
    /// Closes the client, the tunnel it runs through and the key. Safe to call twice and from several threads, and it never
    /// throws: it runs during shutdown, where nothing can be done about a failure anyway (T7.R19).
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { Client.Dispose(); } catch (Exception) { /* closing a dead connection */ }
        try { ReleaseTunnel(tunnel, jump); } catch (Exception) { /* the tunnel is gone either way */ }
        try { privateKey.Dispose(); } catch (Exception) { /* nothing left to release */ }
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
