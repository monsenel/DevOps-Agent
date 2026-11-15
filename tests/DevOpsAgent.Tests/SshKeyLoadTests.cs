using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Infrastructure.Persistence;
using DevOpsAgent.Infrastructure.Ssh;
using Microsoft.Extensions.Options;

namespace DevOpsAgent.Tests;

/// <summary>
/// T7.R19: the real SSH.NET transport factory classifies key problems as login failures with fixed wording, before it opens
/// any network connection, and never repeats what the key file or the library said. No SSH server is involved: a key that
/// does load only gets as far as a closed loopback port.
/// </summary>
public sealed class SshKeyLoadTests : IDisposable
{
    private const string PassphraseCanary = "correct-horse-battery-canary";
    private const string GarbageCanary = "THIS-IS-NOT-A-KEY-canary-9f3a";

    private readonly string _directory = Directory.CreateTempSubdirectory("dva-key-tests-").FullName;
    private readonly TestProtector _protector = new();

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* a temp directory: nothing depends on it */ }
    }

    [Fact]
    public async Task AMissingKeyFileIsAKeyFileNotFoundLoginFailureWithoutAnyConnection()
    {
        var failure = await ConnectAsync(KeyServer("no-such-key"));

        Assert.Equal(SshAuthFailure.KeyFileNotFound, failure.Reason);
        Assert.Equal("key file not found", failure.Message);
        Assert.DoesNotContain("no-such-key", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileThatIsNotAKeyIsAnInvalidKeyWithoutEchoingItsContents()
    {
        File.WriteAllText(Path.Combine(_directory, "garbage"), $"{GarbageCanary}\nnot a key at all\n");

        var failure = await ConnectAsync(KeyServer("garbage"));

        Assert.Equal(SshAuthFailure.KeyInvalidOrWrongPassphrase, failure.Reason);
        Assert.DoesNotContain(GarbageCanary, failure.ToString(), StringComparison.Ordinal);
        Assert.Null(failure.InnerException);
    }

    [Fact]
    public async Task ATruncatedKeyIsAnInvalidKeyToo()
    {
        File.WriteAllText(Path.Combine(_directory, "truncated"), "-----BEGIN OPENSSH PRIVATE KEY-----\nAAAA\n-----END OPENSSH PRIVATE KEY-----\n");

        var failure = await ConnectAsync(KeyServer("truncated"));

        Assert.Equal(SshAuthFailure.KeyInvalidOrWrongPassphrase, failure.Reason);
    }

    [Fact]
    public async Task AnEmptyKeyFileIsAnInvalidKey()
    {
        File.WriteAllText(Path.Combine(_directory, "empty"), string.Empty);

        var failure = await ConnectAsync(KeyServer("empty"));

        Assert.Equal(SshAuthFailure.KeyInvalidOrWrongPassphrase, failure.Reason);
    }

    [Fact]
    public async Task AnUnreadableKeyFileIsReportedAsUnreadable()
    {
        if (OperatingSystem.IsWindows()) return; // Unix file modes: the app runs on Linux only
        var path = Path.Combine(_directory, "locked");
        File.WriteAllText(path, "irrelevant");
        File.SetUnixFileMode(path, UnixFileMode.None);
        try
        {
            using var probe = File.OpenRead(path);
            return; // running with enough privilege to read it anyway (root): nothing to assert
        }
        catch (UnauthorizedAccessException)
        {
        }

        var failure = await ConnectAsync(KeyServer("locked"));

        Assert.Equal(SshAuthFailure.KeyFileUnreadable, failure.Reason);
        Assert.Equal("key file cannot be read", failure.Message);
    }

    [Fact]
    public async Task APassphraseProtectedKeyWithoutAStoredPassphraseNeedsOne()
    {
        WriteEncryptedKey("protected");

        var failure = await ConnectAsync(KeyServer("protected"));

        Assert.Equal(SshAuthFailure.PassphraseRequired, failure.Reason);
        Assert.Equal("the key needs a passphrase and none is stored", failure.Message);
    }

    [Fact]
    public async Task AWrongPassphraseIsAnInvalidKeyOrWrongPassphraseAndTheRightOneGetsAsFarAsTheNetwork()
    {
        WriteEncryptedKey("protected");

        var wrong = await ConnectAsync(KeyServer("protected", passphrase: "wrong-" + PassphraseCanary));
        Assert.Equal(SshAuthFailure.KeyInvalidOrWrongPassphrase, wrong.Reason);
        Assert.Equal("wrong passphrase or invalid key file", wrong.Message);
        Assert.DoesNotContain(PassphraseCanary, wrong.ToString(), StringComparison.Ordinal);

        // With the right passphrase the key loads, so the attempt reaches the network and fails there: a connection
        // failure, not a login failure.
        using var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var port = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        var factory = Factory();
        var server = KeyServer("protected", passphrase: PassphraseCanary, port: port);
        var exception = await Assert.ThrowsAnyAsync<Exception>(() => factory.ConnectAsync(server, null, CancellationToken.None));
        Assert.IsNotType<SshAuthFailedException>(exception);
        Assert.Null(SshFailureClassifier.AsAuthFailure(exception));
    }

    [Fact]
    public async Task AnUnreadablePassphraseIsAnInvalidKeyOrWrongPassphrase()
    {
        WriteEncryptedKey("protected");
        var server = KeyServer("protected");
        server.Update(server.Name, server.Host, server.Port, server.User, server.KeyFile, null, "staging", "[]", "not-a-protected-value");

        var failure = await ConnectAsync(server);

        Assert.Equal(SshAuthFailure.KeyInvalidOrWrongPassphrase, failure.Reason);
    }

    [Fact]
    public async Task AServerThatClosesTheConnectionIsAConnectionFailureNotALoginFailure()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        // Accepts and hangs up, without ever sending an SSH banner.
        _ = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
        });

        // A key that cannot load never gets here (covered above); this one is a valid private key in a PEM file, so the
        // failure has to come from the transport.
        WriteUnencryptedKey("plain");
        var exception = await Assert.ThrowsAnyAsync<Exception>(() =>
            Factory().ConnectAsync(KeyServer("plain", port: port), null, CancellationToken.None));

        Assert.IsNotType<SshAuthFailedException>(exception);
        Assert.Null(SshFailureClassifier.AsAuthFailure(exception));
        Assert.False(string.IsNullOrWhiteSpace(SshFailureClassifier.DescribeCause(exception)));
    }

    private async Task<SshAuthFailedException> ConnectAsync(Server server) =>
        await Assert.ThrowsAsync<SshAuthFailedException>(() => Factory().ConnectAsync(server, null, CancellationToken.None));

    private SshNetTransportFactory Factory()
    {
        var options = Options.Create(new SshConnectionOptions
        {
            KnownHostsFile = Path.Combine(_directory, "known_hosts"),
            ManagedKnownHostsFile = Path.Combine(_directory, "managed_known_hosts"),
            ConnectionTimeoutSeconds = 5
        });
        return new(_protector, new ServerKeyStore(Options.Create(new ServerInventoryOptions { KeyDirectory = _directory })), options,
            new ManagedKnownHosts(options, TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<ManagedKnownHosts>.Instance));
    }

    private Server KeyServer(string keyFile, string? passphrase = null, int port = 1)
    {
        var server = new Server("srv", "127.0.0.1", port, "dva", keyFile, "staging");
        if (passphrase is not null)
            server.Update("srv", "127.0.0.1", port, "dva", keyFile, null, "staging", "[]", _protector.Protect(passphrase));
        return server;
    }

    private void WriteUnencryptedKey(string name)
    {
        using var rsa = RSA.Create(2048);
        File.WriteAllText(Path.Combine(_directory, name), rsa.ExportRSAPrivateKeyPem() + "\n");
    }

    // The traditional encrypted PEM format ("Proc-Type: 4,ENCRYPTED", AES-128-CBC, key derived with MD5 as OpenSSL does):
    // a real passphrase-protected private key without needing ssh-keygen on the machine that runs the tests.
    private void WriteEncryptedKey(string name)
    {
        using var rsa = RSA.Create(2048);
        var der = rsa.ExportRSAPrivateKey();
        var iv = RandomNumberGenerator.GetBytes(16);
        var key = MD5.HashData([.. Encoding.UTF8.GetBytes(PassphraseCanary), .. iv.AsSpan(0, 8)]);
        using var aes = Aes.Create();
        aes.Key = key;
        var cipher = aes.EncryptCbc(der, iv, PaddingMode.PKCS7);
        var body = Convert.ToBase64String(cipher, Base64FormattingOptions.InsertLineBreaks);
        File.WriteAllText(Path.Combine(_directory, name),
            $"-----BEGIN RSA PRIVATE KEY-----\nProc-Type: 4,ENCRYPTED\nDEK-Info: AES-128-CBC,{Convert.ToHexString(iv)}\n\n{body}\n-----END RSA PRIVATE KEY-----\n");
    }

    private sealed class TestProtector : ISecretProtector
    {
        public string Protect(string plaintext) => "protected:" + plaintext;

        public string Unprotect(string protectedValue) =>
            protectedValue.StartsWith("protected:", StringComparison.Ordinal)
                ? protectedValue["protected:".Length..]
                : throw new CryptographicException("not protected by this key ring");
    }
}
