using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Domain.Discovery;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DevOpsAgent.Tests;

public sealed class StaleCarryForwardTests
{
    [Fact]
    public async Task UnreachableServerIsCarriedForwardStaleWithItsListenersAndNoDrift()
    {
        await using var database = await CreateDatabaseAsync();
        var app = AddServer(database.Context, "srv-app", "10.88.20.20");
        var db = AddServer(database.Context, "srv-db", "10.88.20.30");
        var runner = new SwitchableRunner();
        var discovery = CreateDiscovery(database.Context, runner);
        var drift = new DriftService(new SnapshotRepository(database.Context), new GraphRepository(database.Context));

        var before = await discovery.RunAsync(null, CancellationToken.None);
        runner.Unreachable.Add(db.Id);
        var during = await discovery.RunAsync(null, CancellationToken.None);

        var graph = (await new GraphRepository(database.Context).GetAsync(during.Id, GraphView.All, CancellationToken.None))!;
        var dbNodes = graph.Nodes.Select(element => element.Data).Where(node => ServerId(node) == db.Id).ToArray();
        Assert.NotEmpty(dbNodes);
        Assert.All(dbNodes, node => Assert.True(StaleCarryForward.IsStale(node.GetProperty("attributes"))));
        Assert.All(dbNodes, node => Assert.StartsWith("stale · last seen ", StaleCarryForward.Badge(node.GetProperty("attributes")), StringComparison.Ordinal));
        Assert.Contains(dbNodes, node => GraphNodeLabel.Describe(node).Contains("(stale · last seen", StringComparison.Ordinal));
        var dbNodeIds = dbNodes.Select(node => node.GetProperty("id").GetString()).ToHashSet();
        Assert.Contains(graph.Listeners, listener => listener.Port == 5432 && dbNodeIds.Contains(listener.NodeId.ToString("N")));
        Assert.Contains(graph.Listeners, listener => listener.Port == 6379 && dbNodeIds.Contains(listener.NodeId.ToString("N")));
        // The reachable server is fresh, not stale.
        Assert.All(graph.Nodes.Select(element => element.Data).Where(node => ServerId(node) == app.Id),
            node => Assert.False(StaleCarryForward.IsStale(node.GetProperty("attributes"))));
        Assert.Equal(SnapshotStatus.Partial, during.Status);

        // Being unreachable is not drift: nothing removed, no new listeners.
        var outage = await drift.CompareAsync(before.Id, during.Id, CancellationToken.None);
        Assert.Empty(outage.Changes);

        // Recovery clears staleness and reports nothing as added.
        runner.Unreachable.Clear();
        var after = await discovery.RunAsync(null, CancellationToken.None);
        var recovered = (await new GraphRepository(database.Context).GetAsync(after.Id, GraphView.All, CancellationToken.None))!;
        Assert.All(recovered.Nodes, node => Assert.False(StaleCarryForward.IsStale(node.Data.GetProperty("attributes"))));
        var recovery = await drift.CompareAsync(during.Id, after.Id, CancellationToken.None);
        Assert.Empty(recovery.Changes);
    }

    [Fact]
    public async Task StaleNodesKeepTheTimeTheyWereReallyLastSeenAcrossSeveralOutageRuns()
    {
        await using var database = await CreateDatabaseAsync();
        var db = AddServer(database.Context, "srv-db", "10.88.20.30");
        var runner = new SwitchableRunner();
        var discovery = CreateDiscovery(database.Context, runner);

        var seen = await discovery.RunAsync(null, CancellationToken.None);
        runner.Unreachable.Add(db.Id);
        await discovery.RunAsync(null, CancellationToken.None);
        var third = await discovery.RunAsync(null, CancellationToken.None);

        var graph = (await new GraphRepository(database.Context).GetAsync(third.Id, GraphView.All, CancellationToken.None))!;
        var host = graph.Nodes.Select(element => element.Data).Single(node => node.GetProperty("kind").GetString() == "Host");
        var lastSeen = DateTimeOffset.Parse(host.GetProperty("attributes").GetProperty(StaleCarryForward.LastSeenAttribute).GetString()!,
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(seen.StartedAtUtc.UtcDateTime, lastSeen.UtcDateTime, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task ServerUnreachableOnItsFirstDiscoveryKeepsABareStaleHost()
    {
        await using var database = await CreateDatabaseAsync();
        var db = AddServer(database.Context, "srv-db", "10.88.20.30");
        var runner = new SwitchableRunner();
        runner.Unreachable.Add(db.Id);

        var snapshot = await CreateDiscovery(database.Context, runner).RunAsync(null, CancellationToken.None);

        var host = (await new GraphRepository(database.Context).GetAsync(snapshot.Id, GraphView.All, CancellationToken.None))!
            .Nodes.Select(element => element.Data).Single();
        Assert.Equal("stale · never discovered", StaleCarryForward.Badge(host.GetProperty("attributes")));
    }

    [Fact]
    public void DriftIgnoresOnlyTheStaleMarkers()
    {
        Assert.True(StaleCarryForward.SameIgnoringStale("{\"Addresses\":[\"10.0.0.1\"]}",
            "{\"Addresses\":[\"10.0.0.1\"],\"Stale\":true,\"LastSeenUtc\":\"2026-11-15T10:00:00.0000000Z\"}"));
        Assert.False(StaleCarryForward.SameIgnoringStale("{\"Addresses\":[\"10.0.0.1\"]}",
            "{\"Addresses\":[\"10.0.0.2\"],\"Stale\":true}"));
    }

    private static Guid? ServerId(JsonElement node) =>
        Guid.TryParse(node.TryGetProperty("serverId", out var value) ? value.GetString() : null, out var id) ? id : null;

    private static DiscoveryService CreateDiscovery(AppDbContext context, ISshRunner runner) => new(
        new ServerRepository(context), new SnapshotRepository(context), new GraphRepository(context), new GraphBuilder(), runner,
        [new HostCollector(), new ListenerCollector()], TimeProvider.System);

    private static Server AddServer(AppDbContext context, string name, string address)
    {
        var server = new Server(name, name, 22, "dva", "key", "staging");
        context.Servers.Add(server);
        context.SaveChanges();
        Addresses[server.Id] = address;
        return server;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, string> Addresses = new();

    /// <summary>"SSH": answers host and listener collection, or reports the server unreachable like the pool does.</summary>
    private sealed class SwitchableRunner : ISshRunner
    {
        public HashSet<Guid> Unreachable { get; } = [];

        public Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options, CancellationToken cancellationToken)
        {
            if (Unreachable.Contains(serverId))
            {
                var now = DateTimeOffset.UtcNow;
                throw new HostUnreachableException(serverId, "server",
                    new HostReachabilityState(serverId, now, now.AddMinutes(1), 1, "timed out", false, null));
            }

            return Task.FromResult(new RemoteCommandResult(0, command, string.Empty, TimeSpan.FromMilliseconds(1)));
        }
    }

    private sealed class HostCollector : ICollector
    {
        public string Name => "host";
        public Task<bool> IsApplicableAsync(ServerContext context, CancellationToken cancellationToken) => Task.FromResult(true);

        public async Task<CollectorOutput> CollectAsync(ServerContext context, CancellationToken cancellationToken)
        {
            await context.SshRunner.RunAsync(context.Server.Id, "ip -j addr", new SshRunOptions(TimeSpan.FromSeconds(5)), cancellationToken);
            var address = Addresses[context.Server.Id];
            var result = new HostDiscoveryResult(context.Server.Name,
                [new HostInterface(2, "eth0", null, "UP", 1500, [new HostAddress("inet", address, 24, "10.88.20.0/24", "global", null)])],
                [], []);
            return new CollectorOutput(JsonSerializer.Serialize(result), string.Empty, 0, TimeSpan.Zero);
        }

        public CollectorResult Parse(CollectorOutput raw) => new(JsonDocument.Parse(raw.StandardOutput).RootElement.Clone());
    }

    private sealed class ListenerCollector : ICollector
    {
        public string Name => "listeners";
        public Task<bool> IsApplicableAsync(ServerContext context, CancellationToken cancellationToken) => Task.FromResult(true);

        public async Task<CollectorOutput> CollectAsync(ServerContext context, CancellationToken cancellationToken)
        {
            await context.SshRunner.RunAsync(context.Server.Id, "ss -tulpnH", new SshRunOptions(TimeSpan.FromSeconds(5)), cancellationToken);
            ListenerInfo[] listeners = context.Server.Name == "srv-db"
                ? [new("tcp", "LISTEN", "0.0.0.0", 5432, null, "postgres", 23), new("tcp", "LISTEN", "0.0.0.0", 6379, null, "redis-server", 45)]
                : [new("tcp", "LISTEN", "0.0.0.0", 5000, null, "docker-proxy", 77)];
            return new CollectorOutput(JsonSerializer.Serialize(new ListenerDiscoveryResult(listeners)), string.Empty, 0, TimeSpan.Zero);
        }

        public CollectorResult Parse(CollectorOutput raw) => new(JsonDocument.Parse(raw.StandardOutput).RootElement.Clone());
    }

    private static async Task<SqliteTestDatabase> CreateDatabaseAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
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
}
