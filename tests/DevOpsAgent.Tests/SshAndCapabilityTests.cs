using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Servers;
using DevOpsAgent.Infrastructure.Persistence;
using DevOpsAgent.Infrastructure.Ssh;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DevOpsAgent.Tests;

public sealed class SshAndCapabilityTests
{
    [Fact]
    public async Task CapabilityTest_ParsesProbeOutputAndPersistsServerCapabilities()
    {
        await using var database = await CreateDatabaseAsync();
        var repository = new ServerRepository(database.Context);
        var inventory = new ServerService(repository, new TestSecretProtector(), new TestKeyStore("id_ed25519"), new RecordingSshConnectionPool());
        var server = await inventory.CreateAsync(new ServerInput(
            "edge", "edge.example.test", 22, "deploy", "id_ed25519", null, null, "prod", []), CancellationToken.None);
        var runner = new FakeSshRunner();
        var service = new ServerCapabilityService(runner, repository, TimeProvider.System);

        var result = await service.TestAsync(server.Id, CancellationToken.None);

        Assert.True(result.Reachable);
        Assert.Equal("Ubuntu 24.04", result.OsInfo);
        Assert.Equal("up 4 hours", result.Uptime);
        Assert.True(result.SudoCapable);
        Assert.Equal(new[] { "docker", "ip", "systemctl" }, result.AvailableCommands);
        Assert.Equal(4, runner.Commands.Count);
        var persisted = await repository.GetAsync(server.Id, CancellationToken.None);
        Assert.True(persisted!.SudoCapable);
        Assert.Equal("Ubuntu 24.04", persisted.OsInfo);
        Assert.NotNull(persisted.LastSeenAtUtc);
    }

    [Fact]
    public void ProbeParsers_IgnoreUnknownFieldsAndCommands()
    {
        Assert.Equal("Debian", ServerCapabilityService.ParseOsInfo("ID=debian\nNAME='Debian'\nPRETTY_NAME=Debian GNU/Linux"));
        Assert.Equal(new[] { "ip", "nginx" }, ServerCapabilityService.ParseAvailableCommands("nginx\nunknown\nip\nnginx\n"));
        Assert.Equal(new[] { "iptables", "nft" }, ServerCapabilityService.ParseAvailableCommands("nft\niptables\n"));
        Assert.Contains("PATH=\"$PATH:/usr/sbin:/sbin\"", ServerCapabilityService.AvailableCommandsCommand, StringComparison.Ordinal);
        Assert.Null(ServerCapabilityService.ParseOsInfo("VERSION_ID=24.04\n"));
    }

    [Fact]
    public async Task CapabilityTest_ReportsUnpinnedHostFingerprint()
    {
        await using var database = await CreateDatabaseAsync();
        var repository = new ServerRepository(database.Context);
        var inventory = new ServerService(repository, new TestSecretProtector(), new TestKeyStore("id_ed25519"), new RecordingSshConnectionPool());
        var server = await inventory.CreateAsync(new ServerInput(
            "edge", "edge.example.test", 22, "deploy", "id_ed25519", null, null, "prod", []), CancellationToken.None);
        var service = new ServerCapabilityService(new RejectingHostKeyRunner(), repository, TimeProvider.System);

        var result = await service.TestAsync(server.Id, CancellationToken.None);

        Assert.False(result.Reachable);
        Assert.Contains("SHA256:abc123", result.Error);
        Assert.Contains("/keys/known_hosts", result.Error);
    }

    [Fact]
    public void KnownHostsVerifier_RequiresExactHostPortAndKey()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dva-known-hosts-{Guid.NewGuid():N}");
        var key = Convert.FromBase64String("AAAAC3NzaC1lZDI1NTE5AAAAIA==");
        File.WriteAllText(path, "edge.example.test,edge-alias ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIA==\n[edge.example.test]:2222 ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIA==\n");
        try
        {
            var verifier = new KnownHostsVerifier(path);
            Assert.True(verifier.Matches("edge.example.test", 22, key));
            Assert.True(verifier.Matches("edge.example.test", 2222, key));
            Assert.False(verifier.Matches("other.example.test", 22, key));
            Assert.False(verifier.Matches("edge.example.test", 22, [.. key, 1]));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SshRunner_UsesNonInteractiveSudoAndPassesTimeout()
    {
        var session = new FakeSshCommandSession();
        var runner = new SshRunner(new FakeSshConnectionPool(session));
        var timeout = TimeSpan.FromSeconds(12);

        var result = await runner.RunAsync(Guid.NewGuid(), "printf '%s' 'a b'", new SshRunOptions(timeout, UseSudo: true), CancellationToken.None);

        Assert.Equal("sudo -n -- sh -c 'printf '\\''%s'\\'' '\\''a b'\\'''", session.Command);
        Assert.Equal(timeout, session.Timeout);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("stdout", result.StandardOutput);
        Assert.Equal("stderr", result.StandardError);
    }

    private static async Task<SqliteTestDatabase> CreateDatabaseAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var context = new AppDbContext(options);
        await context.Database.EnsureCreatedAsync();
        return new SqliteTestDatabase(connection, context);
    }

    private sealed class SqliteTestDatabase(SqliteConnection connection, AppDbContext context) : IAsyncDisposable
    {
        public AppDbContext Context { get; } = context;

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class TestSecretProtector : ISecretProtector
    {
        public string Protect(string plaintext) => $"protected:{plaintext}";
        public string Unprotect(string protectedValue) => protectedValue["protected:".Length..];
    }

    private sealed class TestKeyStore(params string[] files) : IServerKeyStore
    {
        public IReadOnlyList<string> ListFileNames() => files;
        public bool Contains(string fileName) => files.Contains(fileName, StringComparer.Ordinal);
        public string GetFullPath(string fileName) => Path.Combine(Path.GetTempPath(), fileName);
    }

    private sealed class FakeSshRunner : ISshRunner
    {
        public List<string> Commands { get; } = [];

        public Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options, CancellationToken cancellationToken)
        {
            Commands.Add(command);
            var output = command switch
            {
                "cat /etc/os-release" => "NAME=\"Ubuntu\"\nVERSION_ID=\"24.04\"\n",
                "uptime -p" => "up 4 hours\n",
                "sudo -n true" => string.Empty,
                _ => "docker\nsystemctl\nip\n"
            };
            var exitCode = command == "sudo -n true" ? 0 : 0;
            return Task.FromResult(new RemoteCommandResult(exitCode, output, string.Empty, TimeSpan.FromMilliseconds(1)));
        }
    }

    private sealed class RejectingHostKeyRunner : ISshRunner
    {
        public Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options, CancellationToken cancellationToken) =>
            throw new SshHostKeyMismatchException("edge.example.test", 22, "abc123");
    }

    private sealed class FakeSshConnectionPool(FakeSshCommandSession session) : ISshConnectionPool
    {
        public Task<ISshCommandSession> GetSessionAsync(Guid serverId, CancellationToken cancellationToken) => Task.FromResult<ISshCommandSession>(session);
        public void Evict(Guid serverId) { }
        public void Dispose() { }
    }

    private sealed class FakeSshCommandSession : ISshCommandSession
    {
        public string? Command { get; private set; }
        public TimeSpan Timeout { get; private set; }
        public bool IsConnected => true;

        public Task<RemoteCommandResult> ExecuteAsync(string command, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Command = command;
            Timeout = timeout;
            return Task.FromResult(new RemoteCommandResult(0, "stdout", "stderr", TimeSpan.FromMilliseconds(4)));
        }
    }
}
