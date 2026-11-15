using System.Collections.Concurrent;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Servers;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Infrastructure.Persistence;
using DevOpsAgent.Infrastructure.Ssh;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Renci.SshNet.Common;

namespace DevOpsAgent.Tests;

/// <summary>T7.R15: a pooled client is leased per command and is never closed under a running command.</summary>
public sealed class SshConnectionLeaseTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task EvictDuringAnInFlightCommandLetsItFinishAndDisposesTheClientAfterwards()
    {
        await using var lab = await Lab.CreateAsync();
        var app = await lab.AddAsync("app", "10.0.0.2");

        var running = lab.Run(app, "gate:long");
        await lab.Started("long");
        var old = lab.Transports.Single();

        lab.Pool.Evict(app.Id);

        Assert.False(old.Disposed);
        Assert.False(running.IsCompleted);
        Assert.Equal(1, lab.Pool.ActiveLeases);

        // A command started while the old client drains gets a new client.
        var fresh = await lab.Run(app, "quick").WaitAsync(Patience);
        Assert.Equal(2, lab.Transports.Count);
        Assert.Equal(lab.Transports[1].Name, fresh.StandardOutput);
        Assert.False(old.Disposed);

        lab.Open("long");
        var result = await running.WaitAsync(Patience);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("long finished", result.StandardOutput);
        Assert.True(old.Disposed);
        Assert.False(lab.Transports[1].Disposed);
        Assert.Equal(0, lab.Pool.ActiveLeases);
        Assert.Equal(1, lab.Pool.LiveClients);
    }

    [Fact]
    public async Task AHostEditThroughTheInventoryServiceKeepsTheRunningCommandAlive()
    {
        await using var lab = await Lab.CreateAsync();
        var app = await lab.AddAsync("app", "10.0.0.2");

        var running = lab.Run(app, "gate:update");
        await lab.Started("update");
        var old = lab.Transports.Single();

        // The real edit path: ServerService.UpdateAsync evicts the pooled connection when how the server is reached changes.
        await lab.Service.UpdateAsync(app.Id, lab.Input("app", "10.0.0.99"), CancellationToken.None);

        Assert.False(old.Disposed);
        var fresh = await lab.Run(app, "quick").WaitAsync(Patience);
        Assert.Equal("10.0.0.99", lab.Transports[1].Host);
        Assert.Equal(lab.Transports[1].Name, fresh.StandardOutput);
        Assert.False(old.Disposed);

        lab.Open("update");
        Assert.Equal(0, (await running.WaitAsync(Patience)).ExitCode);
        Assert.True(old.Disposed);
        Assert.Equal(0, lab.Pool.ActiveLeases);
    }

    [Fact]
    public async Task ARenameDoesNotRetireTheClientAtAll()
    {
        await using var lab = await Lab.CreateAsync();
        var app = await lab.AddAsync("app", "10.0.0.2");
        var running = lab.Run(app, "gate:long");
        await lab.Started("long");

        await lab.Service.UpdateAsync(app.Id, lab.Input("app-renamed", "10.0.0.2"), CancellationToken.None);
        var next = await lab.Run(app, "quick").WaitAsync(Patience);

        Assert.Single(lab.Transports);
        Assert.Equal(lab.Transports[0].Name, next.StandardOutput);
        lab.Open("long");
        await running.WaitAsync(Patience);
        Assert.False(lab.Transports[0].Disposed);
    }

    [Fact]
    public async Task TimeoutThresholdWhileAnotherCommandRunsRetiresTheClientWithoutKillingIt()
    {
        await using var lab = await Lab.CreateAsync();
        var app = await lab.AddAsync("app", "10.0.0.2");

        var upgrade = lab.Run(app, "gate:upgrade");
        await lab.Started("upgrade");
        var old = lab.Transports.Single();

        for (var timeout = 1; timeout <= SshTimeoutTracker.DefaultThreshold; timeout++)
        {
            await Assert.ThrowsAsync<SshOperationTimeoutException>(() => lab.Run(app, "timeout"));
        }

        // Retired, not disposed: the long command is still running on it.
        Assert.False(old.Disposed);
        Assert.False(upgrade.IsCompleted);
        Assert.Equal(1, lab.Pool.ActiveLeases);
        var fresh = await lab.Run(app, "quick").WaitAsync(Patience);
        Assert.Equal(lab.Transports[1].Name, fresh.StandardOutput);

        lab.Open("upgrade");
        Assert.Equal("upgrade finished", (await upgrade.WaitAsync(Patience)).StandardOutput);
        Assert.True(old.Disposed);
        Assert.False(lab.Transports[1].Disposed);
        Assert.Equal(0, lab.Pool.ActiveLeases);
    }

    [Fact]
    public async Task TimeoutThresholdWithNothingElseInFlightDisposesTheClientImmediately()
    {
        await using var lab = await Lab.CreateAsync();
        var app = await lab.AddAsync("app", "10.0.0.2");

        for (var timeout = 1; timeout < SshTimeoutTracker.DefaultThreshold; timeout++)
        {
            await Assert.ThrowsAsync<SshOperationTimeoutException>(() => lab.Run(app, "timeout"));
            Assert.False(lab.Transports.Single().Disposed);
        }

        await Assert.ThrowsAsync<SshOperationTimeoutException>(() => lab.Run(app, "timeout"));

        Assert.True(lab.Transports.Single().Disposed);
        Assert.Equal(0, lab.Pool.ActiveLeases);
        Assert.Equal(0, lab.Pool.LiveClients);
        // The next command reconnects.
        await lab.Run(app, "quick");
        Assert.Equal(2, lab.Transports.Count);
    }

    [Fact]
    public async Task AJumpTunnelOutlivesTheCommandsRunningThroughIt()
    {
        await using var lab = await Lab.CreateAsync();
        var edge = await lab.AddAsync("edge", "10.0.0.1");
        var app = await lab.AddAsync("app", "10.0.0.2", edge);

        var running = lab.Run(app, "gate:deploy");
        await lab.Started("deploy");
        var edgeClient = lab.Transports.Single(transport => transport.Host == "10.0.0.1");
        var appClient = lab.Transports.Single(transport => transport.Host == "10.0.0.2");
        Assert.Same(edgeClient, appClient.Jump);

        // Editing the jump server evicts it and every server behind it (the real edit path).
        await lab.Service.UpdateAsync(edge.Id, lab.Input("edge", "10.0.0.11"), CancellationToken.None);

        Assert.False(edgeClient.Disposed);
        Assert.False(appClient.Disposed);
        Assert.False(running.IsCompleted);

        // New work reconnects through a new jump client while the old tunnel stays up for the running command.
        var fresh = await lab.Run(app, "quick").WaitAsync(Patience);
        var newApp = lab.Transports.Last(transport => transport.Host == "10.0.0.2");
        Assert.NotSame(appClient, newApp);
        Assert.Equal("10.0.0.11", newApp.Jump!.Host);
        Assert.Equal(newApp.Name, fresh.StandardOutput);
        Assert.False(edgeClient.Disposed);

        lab.Open("deploy");
        Assert.Equal("deploy finished", (await running.WaitAsync(Patience)).StandardOutput);

        Assert.True(appClient.Disposed);
        Assert.True(edgeClient.Disposed);
        // The dependent closes first, then the tunnel's jump client.
        var log = lab.DisposeLog.ToArray();
        Assert.True(Array.IndexOf(log, appClient.Name) < Array.IndexOf(log, edgeClient.Name), string.Join(", ", log));
        Assert.False(newApp.Disposed);
        Assert.False(newApp.Jump.Disposed);
        Assert.Equal(0, lab.Pool.ActiveLeases);
    }

    [Fact]
    public async Task ARetiredJumpClientStaysOpenWhileTheDependentEntryUsesItAndClosesWhenItIsReplaced()
    {
        await using var lab = await Lab.CreateAsync();
        var edge = await lab.AddAsync("edge", "10.0.0.1");
        var app = await lab.AddAsync("app", "10.0.0.2", edge);
        await lab.Run(app, "quick");
        var edgeClient = lab.Transports.Single(transport => transport.Host == "10.0.0.1");
        var appClient = lab.Transports.Single(transport => transport.Host == "10.0.0.2");

        // Only the jump entry is retired; the dependent entry keeps its tunnel alive even though no command runs.
        lab.Pool.Evict(edge.Id);
        Assert.False(edgeClient.Disposed);
        Assert.False(appClient.Disposed);

        // The next command notices the replaced jump entry, builds a new dependent entry and lets the old chain go.
        await lab.Run(app, "quick");

        Assert.True(appClient.Disposed);
        Assert.True(edgeClient.Disposed);
        Assert.Equal(0, lab.Pool.ActiveLeases);
        Assert.Equal(2, lab.Pool.LiveClients);
    }

    [Fact]
    public async Task ACancelledCommandReleasesItsLeaseAndARetiredClientIsDisposed()
    {
        await using var lab = await Lab.CreateAsync();
        var app = await lab.AddAsync("app", "10.0.0.2");
        using var cancellation = new CancellationTokenSource();

        var running = lab.Run(app, "gate:cancel", cancellation.Token);
        await lab.Started("cancel");
        lab.Pool.Evict(app.Id);
        Assert.False(lab.Transports.Single().Disposed);

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(Patience));

        Assert.Equal(0, lab.Pool.ActiveLeases);
        Assert.True(lab.Transports.Single().Disposed);
        Assert.Equal(0, lab.Pool.LiveClients);
    }

    [Fact]
    public async Task AFailingCommandReleasesItsLeaseAndARetiredClientIsDisposed()
    {
        await using var lab = await Lab.CreateAsync();
        var app = await lab.AddAsync("app", "10.0.0.2");

        // A command that throws on a retired client: the lease is released and the client disposed.
        var failing = lab.Run(app, "gatefail:boom");
        await lab.Started("boom");
        lab.Pool.Evict(app.Id);
        Assert.False(lab.Transports[0].Disposed);
        lab.Open("boom");
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.WaitAsync(Patience));
        Assert.Equal(0, lab.Pool.ActiveLeases);
        Assert.True(lab.Transports[0].Disposed);

        // A command that throws on a healthy client releases its lease and does not retire the client.
        await Assert.ThrowsAsync<InvalidOperationException>(() => lab.Run(app, "throw"));
        Assert.Equal(0, lab.Pool.ActiveLeases);
        Assert.Equal(2, lab.Transports.Count);
        Assert.False(lab.Transports[1].Disposed);
    }

    [Fact]
    public async Task ATimedOutCommandReleasesItsLease()
    {
        await using var lab = await Lab.CreateAsync();
        var app = await lab.AddAsync("app", "10.0.0.2");

        await Assert.ThrowsAsync<SshOperationTimeoutException>(() => lab.Run(app, "timeout"));

        Assert.Equal(0, lab.Pool.ActiveLeases);
        Assert.False(lab.Transports.Single().Disposed);
    }

    [Fact]
    public async Task ReleasingALeaseTwiceNeverFreesAnotherCommandsClient()
    {
        await using var lab = await Lab.CreateAsync();
        var app = await lab.AddAsync("app", "10.0.0.2");

        var first = await lab.Pool.GetSessionAsync(app.Id, false, CancellationToken.None);
        var second = await lab.Pool.GetSessionAsync(app.Id, false, CancellationToken.None);
        Assert.Equal(2, lab.Pool.ActiveLeases);
        lab.Pool.Evict(app.Id);

        first.Dispose();
        first.Dispose();
        Assert.Equal(1, lab.Pool.ActiveLeases);
        Assert.False(lab.Transports.Single().Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => first.ExecuteAsync("quick", TimeSpan.FromSeconds(5), CancellationToken.None));

        second.Dispose();
        Assert.True(lab.Transports.Single().Disposed);
        Assert.Equal(0, lab.Pool.ActiveLeases);
    }

    [Fact]
    public async Task PoolDisposeClosesClientsEvenWithCommandsInFlightAndDoesNotThrow()
    {
        await using var lab = await Lab.CreateAsync();
        var edge = await lab.AddAsync("edge", "10.0.0.1");
        var app = await lab.AddAsync("app", "10.0.0.2", edge);
        var other = await lab.AddAsync("other", "10.0.0.3");

        var running = lab.Run(app, "gate:shutdown");
        var draining = lab.Run(other, "gate:draining");
        await lab.Started("shutdown");
        await lab.Started("draining");
        // One client is retired and still draining when the app shuts down.
        lab.Pool.Evict(other.Id);

        var exception = Record.Exception(lab.Pool.Dispose);

        Assert.Null(exception);
        Assert.All(lab.Transports, transport => Assert.True(transport.Disposed, transport.Name));
        Assert.Equal(0, lab.Pool.LiveClients);
        // Commands in flight fail with the shutdown exception; they are not waited for.
        await Assert.ThrowsAsync<SshConnectionException>(() => running.WaitAsync(Patience));
        await Assert.ThrowsAsync<SshConnectionException>(() => draining.WaitAsync(Patience));
        // The tunnel is released before its jump client closes.
        var log = lab.DisposeLog.ToArray();
        var appName = lab.Transports.Single(transport => transport.Host == "10.0.0.2").Name;
        var edgeName = lab.Transports.Single(transport => transport.Host == "10.0.0.1").Name;
        Assert.True(Array.IndexOf(log, appName) < Array.IndexOf(log, edgeName), string.Join(", ", log));
        Assert.Equal(0, lab.Pool.ActiveLeases);
        Record.Exception(lab.Pool.Dispose);
    }

    private sealed class Lab : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly AppDbContext _context;
        private readonly FakeFactory _factory = new();

        private Lab(SqliteConnection connection, AppDbContext context)
        {
            _connection = connection;
            _context = context;
            var repository = new ServerRepository(context);
            var services = new ServiceCollection().AddSingleton<IServerRepository>(repository).BuildServiceProvider();
            Pool = new SshConnectionPool(services.GetRequiredService<IServiceScopeFactory>(), _factory,
                new HostReachabilityTracker(TimeProvider.System), NullLogger<SshConnectionPool>.Instance);
            Service = new ServerService(repository, new PlainSecrets(), new Keys(), Pool);
            Runner = new SshRunner(Pool);
        }

        public SshConnectionPool Pool { get; }
        public ServerService Service { get; }
        public SshRunner Runner { get; }
        public IReadOnlyList<FakeTransport> Transports => _factory.Created.ToArray();
        public IReadOnlyCollection<string> DisposeLog => _factory.DisposeLog;

        public static async Task<Lab> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync();
            return new Lab(connection, context);
        }

        public ServerInput Input(string name, string host, Guid? jump = null) =>
            new(name, host, 22, "dva", "id_ed25519", null, jump, "staging", []);

        public async Task<ServerSummary> AddAsync(string name, string host, ServerSummary? jump = null) =>
            await Service.CreateAsync(Input(name, host, jump?.Id), CancellationToken.None);

        public Task<RemoteCommandResult> Run(ServerSummary server, string command, CancellationToken cancellationToken = default) =>
            Runner.RunAsync(server.Id, command, new SshRunOptions(TimeSpan.FromSeconds(30)), cancellationToken);

        public Task Started(string gate) => _factory.Gate(gate).Started.Task.WaitAsync(Patience);
        public void Open(string gate) => _factory.Gate(gate).Release.TrySetResult();

        public async ValueTask DisposeAsync()
        {
            Pool.Dispose();
            await _context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class Gate
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class FakeFactory : ISshTransportFactory
    {
        private readonly ConcurrentDictionary<string, Gate> _gates = new(StringComparer.Ordinal);
        private int _counter;

        public ConcurrentQueue<FakeTransport> Created { get; } = new();
        public ConcurrentQueue<string> DisposeLog { get; } = new();

        public Gate Gate(string name) => _gates.GetOrAdd(name, static _ => new Gate());

        public Task<ISshTransport> ConnectAsync(Server server, ISshTransport? jump, CancellationToken cancellationToken)
        {
            var transport = new FakeTransport(this, $"{server.Host}#{Interlocked.Increment(ref _counter)}", server.Host, jump as FakeTransport);
            Created.Enqueue(transport);
            return Task.FromResult<ISshTransport>(transport);
        }
    }

    private sealed class FakeTransport(FakeFactory factory, string name, string host, FakeTransport? jump) : ISshTransport
    {
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed;

        public string Name { get; } = name;
        public string Host { get; } = host;
        public FakeTransport? Jump { get; } = jump;
        public bool Disposed => Volatile.Read(ref _disposed) != 0;
        public bool IsConnected => !Disposed && (Jump is null || Jump.IsConnected);

        public async Task<RemoteCommandResult> ExecuteAsync(string command, TimeSpan timeout, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            if (command.StartsWith("gate", StringComparison.Ordinal))
            {
                var failAfter = command.StartsWith("gatefail:", StringComparison.Ordinal);
                var name = command[(command.IndexOf(':', StringComparison.Ordinal) + 1)..];
                var gate = factory.Gate(name);
                gate.Started.TrySetResult();
                var finished = await Task.WhenAny(gate.Release.Task, _closed.Task).WaitAsync(cancellationToken);
                // A client closed under a running command surfaces as a broken connection, as in SSH.NET.
                if (finished == _closed.Task) throw new SshConnectionException("The connection was closed under the command.");
                return failAfter ? throw new InvalidOperationException("The command failed.") : Ok($"{name} finished");
            }

            return command switch
            {
                "timeout" => throw new SshOperationTimeoutException("The command timed out."),
                "throw" => throw new InvalidOperationException("The command failed."),
                _ => Ok(Name)
            };
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            factory.DisposeLog.Enqueue(Name);
            _closed.TrySetResult();
        }

        private static RemoteCommandResult Ok(string output) => new(0, output, string.Empty, TimeSpan.FromMilliseconds(1));
    }

    private sealed class PlainSecrets : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string protectedValue) => protectedValue;
    }

    private sealed class Keys : IServerKeyStore
    {
        public IReadOnlyList<string> ListFileNames() => ["id_ed25519"];
        public bool Contains(string fileName) => fileName == "id_ed25519";
        public string GetFullPath(string fileName) => Path.Combine(Path.GetTempPath(), fileName);
    }
}
