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
    public async Task AServerWhoseLoginFailedIsCarriedForwardStaleLikeAnUnreachableOneAndTheRunNamesTheCause()
    {
        await using var database = await CreateDatabaseAsync();
        var app = AddServer(database.Context, "srv-app", "10.88.20.20");
        var db = AddServer(database.Context, "srv-db", "10.88.20.30");
        var runner = new SwitchableRunner();
        var discovery = CreateDiscovery(database.Context, runner);
        var drift = new DriftService(new SnapshotRepository(database.Context), new GraphRepository(database.Context));

        var before = await discovery.RunAsync(null, CancellationToken.None);
        runner.AuthFailed.Add(db.Id);
        var during = await discovery.RunAsync(null, CancellationToken.None);

        var graph = (await new GraphRepository(database.Context).GetAsync(during.Id, GraphView.All, CancellationToken.None))!;
        var dbNodes = graph.Nodes.Select(element => element.Data).Where(node => ServerId(node) == db.Id).ToArray();
        Assert.NotEmpty(dbNodes);
        Assert.All(dbNodes, node => Assert.True(StaleCarryForward.IsStale(node.GetProperty("attributes"))));
        Assert.Contains(graph.Listeners, listener => listener.Port == 5432);
        Assert.All(graph.Nodes.Select(element => element.Data).Where(node => ServerId(node) == app.Id),
            node => Assert.False(StaleCarryForward.IsStale(node.GetProperty("attributes"))));
        Assert.Equal(SnapshotStatus.Partial, during.Status);
        // Not drift, as for an unreachable server.
        Assert.Empty((await drift.CompareAsync(before.Id, during.Id, CancellationToken.None)).Changes);

        // The run records the login failure, in words, for every collector it skipped.
        var runs = await database.Context.CollectorRuns.Where(run => run.SnapshotId == during.Id && run.ServerId == db.Id).ToArrayAsync();
        Assert.Equal(2, runs.Length);
        Assert.All(runs, run =>
        {
            Assert.Equal(CollectorRunStatus.Failed, run.Status);
            Assert.StartsWith("server: authentication failed (key file not found).", run.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("unreachable", run.Error, StringComparison.OrdinalIgnoreCase);
        });

        // Fixing the login recovers: no stale marker, nothing added.
        runner.AuthFailed.Clear();
        var after = await discovery.RunAsync(null, CancellationToken.None);
        var recovered = (await new GraphRepository(database.Context).GetAsync(after.Id, GraphView.All, CancellationToken.None))!;
        Assert.All(recovered.Nodes, node => Assert.False(StaleCarryForward.IsStale(node.Data.GetProperty("attributes"))));
        Assert.Empty((await drift.CompareAsync(during.Id, after.Id, CancellationToken.None)).Changes);
    }

    [Fact]
    public async Task StaleNodesKeepTheTimeTheyWereReallyLastSeenAcrossSeveralOutageRuns()
    {
        await using var database = await CreateDatabaseAsync();
        var db = AddServer(database.Context, "srv-db", "10.88.20.30");
        var runner = new SwitchableRunner();
        var discovery = CreateDiscovery(database.Context, runner);

        var seen = await discovery.RunAsync(null, CancellationToken.None);
        var hostRun = await database.Context.CollectorRuns
            .SingleAsync(run => run.SnapshotId == seen.Id && run.ServerId == db.Id && run.CollectorName == "host");
        runner.Unreachable.Add(db.Id);
        await discovery.RunAsync(null, CancellationToken.None);
        var third = await discovery.RunAsync(null, CancellationToken.None);

        var graph = (await new GraphRepository(database.Context).GetAsync(third.Id, GraphView.All, CancellationToken.None))!;
        var host = graph.Nodes.Select(element => element.Data).Single(node => node.GetProperty("kind").GetString() == "Host");
        var lastSeen = DateTimeOffset.Parse(host.GetProperty("attributes").GetProperty(StaleCarryForward.LastSeenAttribute).GetString()!,
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(hostRun.CreatedAtUtc.UtcDateTime, lastSeen.UtcDateTime, TimeSpan.FromMilliseconds(1));
        Assert.Equal(StaleCarryForward.Badge(host.GetProperty("attributes")), host.GetProperty("staleBadge").GetString());
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
    public async Task TwoDockerDnsListenersOnOneServerAreOneDriftIdentityAndDoNotThrow()
    {
        await using var database = await CreateDatabaseAsync();
        AddServer(database.Context, "srv-db", "10.88.20.30");
        var discovery = CreateDiscovery(database.Context, new SwitchableRunner(), twoDnsListeners: true);
        var drift = new DriftService(new SnapshotRepository(database.Context), new GraphRepository(database.Context));

        var first = await discovery.RunAsync(null, CancellationToken.None);
        var second = await discovery.RunAsync(null, CancellationToken.None);

        Assert.Empty((await drift.CompareAsync(first.Id, second.Id, CancellationToken.None)).Changes);
    }

    [Fact]
    public void DriftIgnoresOnlyTheStaleMarkers()
    {
        Assert.True(StaleCarryForward.SameIgnoringStale("{\"Addresses\":[\"10.0.0.1\"]}",
            "{\"Addresses\":[\"10.0.0.1\"],\"Stale\":true,\"LastSeenUtc\":\"2026-11-15T10:00:00.0000000Z\"}"));
        Assert.False(StaleCarryForward.SameIgnoringStale("{\"Addresses\":[\"10.0.0.1\"]}",
            "{\"Addresses\":[\"10.0.0.2\"],\"Stale\":true}"));
    }

    [Fact]
    public async Task MultiAddressStaleHostSeedsIpIndexAndRemovedFreshProxyIsNotHidden()
    {
        await using var database = await CreateDatabaseAsync();
        var edge = AddServer(database.Context, "srv-edge", "10.88.20.10");
        var db = AddServer(database.Context, "srv-db", "10.88.20.30");
        ExtraAddresses[db.Id] = ["10.88.20.31"];
        var runner = new SwitchableRunner();
        var nginxCollector = new ConfigurableNginxCollector(edge.Id, "http://10.88.20.31:5432");
        var discovery = new DiscoveryService(
            new ServerRepository(database.Context),
            new SnapshotRepository(database.Context),
            new GraphRepository(database.Context),
            new GraphBuilder(),
            runner,
            [new HostCollector(), new ListenerCollector(), nginxCollector],
            TimeProvider.System);
        var drift = new DriftService(new SnapshotRepository(database.Context), new GraphRepository(database.Context));

        var before = await discovery.RunAsync(null, CancellationToken.None);
        runner.Unreachable.Add(db.Id);
        var during = await discovery.RunAsync(null, CancellationToken.None);

        var duringGraph = (await new GraphRepository(database.Context).GetAsync(during.Id, GraphView.All, CancellationToken.None))!;
        // T7.R16a: second IP 10.88.20.31 resolves to stale srv-db, NEVER to a new Unknown node!
        Assert.DoesNotContain(duringGraph.Nodes, node => node.Data.GetProperty("kind").GetString() == "Unknown");
        Assert.Empty((await drift.CompareAsync(before.Id, during.Id, CancellationToken.None)).Changes);

        // T7.R16a: removing proxy_pass on reachable srv-edge while srv-db is stale MUST NOT be hidden by carry-forward.
        nginxCollector.ProxyTarget = null;
        var afterRemoval = await discovery.RunAsync(null, CancellationToken.None);
        var removalDrift = await drift.CompareAsync(during.Id, afterRemoval.Id, CancellationToken.None);
        Assert.Contains(removalDrift.Changes, change =>
            change.ChangeType == DriftChangeType.Removed && change.EntityType == DriftEntityType.Edge);

        // Restoring proxy_pass and recovering srv-db produces no Unknown nodes and no new-listener alerts.
        nginxCollector.ProxyTarget = "http://10.88.20.31:5432";
        runner.Unreachable.Clear();
        var recovered = await discovery.RunAsync(null, CancellationToken.None);
        var recoveredGraph = (await new GraphRepository(database.Context).GetAsync(recovered.Id, GraphView.All, CancellationToken.None))!;
        Assert.DoesNotContain(recoveredGraph.Nodes, node => node.Data.GetProperty("kind").GetString() == "Unknown");
        Assert.Empty((await drift.CompareAsync(during.Id, recovered.Id, CancellationToken.None)).NewListeners);
    }

    [Fact]
    public async Task StaleToStaleEdgesAreCarriedAndMarkedStaleInGraphDto()
    {
        await using var database = await CreateDatabaseAsync();
        var edge = AddServer(database.Context, "srv-edge", "10.88.20.10");
        var db = AddServer(database.Context, "srv-db", "10.88.20.30");
        var runner = new SwitchableRunner();
        var nginxCollector = new ConfigurableNginxCollector(edge.Id, "http://10.88.20.30:5432");
        var discovery = new DiscoveryService(
            new ServerRepository(database.Context),
            new SnapshotRepository(database.Context),
            new GraphRepository(database.Context),
            new GraphBuilder(),
            runner,
            [new HostCollector(), new ListenerCollector(), nginxCollector],
            TimeProvider.System);

        await discovery.RunAsync(null, CancellationToken.None);
        runner.Unreachable.Add(edge.Id);
        runner.Unreachable.Add(db.Id);
        var bothStale = await discovery.RunAsync(null, CancellationToken.None);

        var graph = (await new GraphRepository(database.Context).GetAsync(bothStale.Id, GraphView.All, CancellationToken.None))!;
        var carriedDeclared = graph.Edges.Select(e => e.Data)
            .FirstOrDefault(e => e.GetProperty("kind").GetString() == "Declared"
                && e.TryGetProperty("port", out var p) && p.ValueKind == JsonValueKind.Number && p.GetInt32() == 5432);
        Assert.Equal(JsonValueKind.Object, carriedDeclared.ValueKind);
        Assert.True(carriedDeclared.GetProperty("stale").GetBoolean());
        Assert.Equal(StaleCarryForward.StaleEdgeBadge, carriedDeclared.GetProperty("staleText").GetString());
        var edgeId = Guid.ParseExact(carriedDeclared.GetProperty("id").GetString()!, "N");
        Assert.Contains(graph.Evidence, item => item.EdgeId == edgeId && item.Collector == StaleCarryForward.StaleEvidenceCollector);
    }

    [Fact]
    public async Task MergeBaseLastSeenUsesServersOwnHostCollectorTimeNotMergeSnapshotStart()
    {
        await using var database = await CreateDatabaseAsync();
        var app = AddServer(database.Context, "srv-app", "10.88.20.20");
        var db = AddServer(database.Context, "srv-db", "10.88.20.30");
        var runner = new SwitchableRunner();
        var clock = new SteppingTimeProvider(new DateTimeOffset(2026, 11, 16, 10, 0, 0, TimeSpan.Zero));
        var discovery = new DiscoveryService(
            new ServerRepository(database.Context),
            new SnapshotRepository(database.Context),
            new GraphRepository(database.Context),
            new GraphBuilder(),
            runner,
            [new HostCollector(), new ListenerCollector()],
            clock);

        var first = await discovery.RunAsync(null, CancellationToken.None);
        var dbHostRun = await database.Context.CollectorRuns
            .SingleAsync(run => run.SnapshotId == first.Id && run.ServerId == db.Id && run.CollectorName == "host");

        // Advance 30 minutes and merge ONLY srv-app: srv-db is untouched since `first`.
        clock.Advance(TimeSpan.FromMinutes(30));
        await discovery.RunServerAndMergeAsync(app.Id, null, CancellationToken.None);

        // Advance another 30 minutes and take srv-db offline.
        clock.Advance(TimeSpan.FromMinutes(30));
        runner.Unreachable.Add(db.Id);
        var outage = await discovery.RunAsync(null, CancellationToken.None);

        var graph = (await new GraphRepository(database.Context).GetAsync(outage.Id, GraphView.All, CancellationToken.None))!;
        var dbHost = graph.Nodes.Select(e => e.Data)
            .Single(node => node.GetProperty("kind").GetString() == "Host" && ServerId(node) == db.Id);
        var lastSeen = DateTimeOffset.Parse(dbHost.GetProperty("attributes").GetProperty(StaleCarryForward.LastSeenAttribute).GetString()!,
            System.Globalization.CultureInfo.InvariantCulture);

        // T7.R16b: must equal db's own host collector run time from `first`, NOT the merge snapshot start time 30 min later.
        Assert.Equal(dbHostRun.CreatedAtUtc.UtcDateTime, lastSeen.UtcDateTime, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task NewListenerDriftExcludesNeverDiscoveredRecoveryAndFailedListenerCollectorRuns()
    {
        await using var database = await CreateDatabaseAsync();
        AddServer(database.Context, "srv-ok", "10.88.20.10");
        var app = AddServer(database.Context, "srv-app", "10.88.20.20");
        var db = AddServer(database.Context, "srv-db", "10.88.20.30");
        var runner = new SwitchableRunner();
        var failListenersFor = new HashSet<Guid> { app.Id };
        runner.Unreachable.Add(db.Id);
        var discovery = new DiscoveryService(
            new ServerRepository(database.Context),
            new SnapshotRepository(database.Context),
            new GraphRepository(database.Context),
            new GraphBuilder(),
            runner,
            [new HostCollector(), new FailableListenerCollector(failListenersFor)],
            TimeProvider.System);
        var drift = new DriftService(new SnapshotRepository(database.Context), new GraphRepository(database.Context));

        // Snapshot 1: db is unreachable ("never discovered"), app's listener collector fails.
        var first = await discovery.RunAsync(null, CancellationToken.None);

        // Snapshot 2: db recovers for the first time, app's listener collector now succeeds.
        runner.Unreachable.Clear();
        failListenersFor.Clear();
        var second = await discovery.RunAsync(null, CancellationToken.None);

        // T7.R16c: neither never-discovered recovery nor failed-listener recovery may raise new-listener alerts.
        var recoveryReport = await drift.CompareAsync(first.Id, second.Id, CancellationToken.None);
        Assert.Empty(recoveryReport.NewListeners);

        // Snapshot 3: both servers succeeded in Snapshot 2 and Snapshot 3; a genuinely new listener on app IS reported.
        ExtraListeners[app.Id] = [new ListenerInfo("tcp", "LISTEN", "0.0.0.0", 9090, null, "metrics", 88)];
        var third = await discovery.RunAsync(null, CancellationToken.None);
        var realChange = await drift.CompareAsync(second.Id, third.Id, CancellationToken.None);
        Assert.Contains(realChange.NewListeners, change => change.Description.Contains(":9090", StringComparison.Ordinal));
    }

    private static Guid? ServerId(JsonElement node) =>
        Guid.TryParse(node.TryGetProperty("serverId", out var value) ? value.GetString() : null, out var id) ? id : null;

    private static DiscoveryService CreateDiscovery(AppDbContext context, ISshRunner runner, bool twoDnsListeners = false) => new(
        new ServerRepository(context), new SnapshotRepository(context), new GraphRepository(context), new GraphBuilder(), runner,
        [new HostCollector(), new ListenerCollector(twoDnsListeners)], TimeProvider.System);

    private static Server AddServer(AppDbContext context, string name, string address)
    {
        var server = new Server(name, name, 22, "dva", "key", "staging");
        context.Servers.Add(server);
        context.SaveChanges();
        Addresses[server.Id] = address;
        return server;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, string> Addresses = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, string[]> ExtraAddresses = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, ListenerInfo[]> ExtraListeners = new();

    /// <summary>"SSH": answers host and listener collection, or reports the server unreachable like the pool does.</summary>
    private sealed class SwitchableRunner : ISshRunner
    {
        public HashSet<Guid> Unreachable { get; } = [];
        /// <summary>Servers whose login fails (T7.R19): skipped by the pool in the same way, with a distinct state.</summary>
        public HashSet<Guid> AuthFailed { get; } = [];

        public Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options, CancellationToken cancellationToken)
        {
            if (AuthFailed.Contains(serverId))
            {
                var now = DateTimeOffset.UtcNow;
                throw new HostUnreachableException(serverId, "server",
                    new HostReachabilityState(serverId, now, now.AddMinutes(1), 1, "key file not found", false,
                        new SshAuthFailedException(SshAuthFailure.KeyFileNotFound), HostFailureKind.AuthFailed, "key file not found"));
            }

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
            var allAddresses = new[] { address }.Concat(ExtraAddresses.GetValueOrDefault(context.Server.Id) ?? [])
                .Select(ip => new HostAddress("inet", ip, 24, "10.88.20.0/24", "global", null)).ToArray();
            var result = new HostDiscoveryResult(context.Server.Name,
                [new HostInterface(2, "eth0", null, "UP", 1500, allAddresses)],
                [], []);
            return new CollectorOutput(JsonSerializer.Serialize(result), string.Empty, 0, TimeSpan.Zero);
        }

        public CollectorResult Parse(CollectorOutput raw) => new(JsonDocument.Parse(raw.StandardOutput).RootElement.Clone());
    }

    private static int _dnsPort = 40000;

    private sealed class ListenerCollector(bool twoDnsListeners = false) : ICollector
    {
        public string Name => "listeners";
        public Task<bool> IsApplicableAsync(ServerContext context, CancellationToken cancellationToken) => Task.FromResult(true);

        public async Task<CollectorOutput> CollectAsync(ServerContext context, CancellationToken cancellationToken)
        {
            await context.SshRunner.RunAsync(context.Server.Id, "ss -tulpnH", new SshRunOptions(TimeSpan.FromSeconds(5)), cancellationToken);
            // Docker's embedded DNS picks a new random port each time the (containerised) server starts.
            var dnsPort = Interlocked.Increment(ref _dnsPort);
            ListenerInfo[] listeners = context.Server.Name == "srv-db"
                ? [new("tcp", "LISTEN", "0.0.0.0", 5432, null, "postgres", 23), new("tcp", "LISTEN", "0.0.0.0", 6379, null, "redis-server", 45),
                   new("tcp", "LISTEN", "127.0.0.11", dnsPort, null, null, null),
                   .. (twoDnsListeners ? new[] { new ListenerInfo("tcp", "LISTEN", "127.0.0.11", dnsPort + 1000, null, null, null) } : [])]
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

    private sealed class ConfigurableNginxCollector(Guid serverId, string? proxyTarget) : ICollector
    {
        public string? ProxyTarget { get; set; } = proxyTarget;
        public string Name => "nginx";
        public Task<bool> IsApplicableAsync(ServerContext context, CancellationToken cancellationToken) =>
            Task.FromResult(context.Server.Id == serverId);

        public async Task<CollectorOutput> CollectAsync(ServerContext context, CancellationToken cancellationToken)
        {
            await context.SshRunner.RunAsync(context.Server.Id, "nginx -T", new SshRunOptions(TimeSpan.FromSeconds(5)), cancellationToken);
            var proxies = ProxyTarget is null
                ? Array.Empty<NginxProxyInfo>()
                : [new NginxProxyInfo("proxy_pass", ProxyTarget, false, "/etc/nginx/conf.d/app.conf", 10)];
            var result = new NginxDiscoveryResult(
                [new NginxServerInfo("http", [new NginxSourceValue("80", "/etc/nginx/conf.d/app.conf", 2)],
                    [new NginxSourceValue("db.example.test", "/etc/nginx/conf.d/app.conf", 3)], [], proxies, "/etc/nginx/conf.d/app.conf", 1)],
                []);
            return new CollectorOutput(JsonSerializer.Serialize(result), string.Empty, 0, TimeSpan.Zero);
        }

        public CollectorResult Parse(CollectorOutput raw) => new(JsonDocument.Parse(raw.StandardOutput).RootElement.Clone());
    }

    private sealed class FailableListenerCollector(HashSet<Guid> failingServers) : ICollector
    {
        public string Name => "listeners";
        public Task<bool> IsApplicableAsync(ServerContext context, CancellationToken cancellationToken) => Task.FromResult(true);

        public async Task<CollectorOutput> CollectAsync(ServerContext context, CancellationToken cancellationToken)
        {
            await context.SshRunner.RunAsync(context.Server.Id, "ss -tulpnH", new SshRunOptions(TimeSpan.FromSeconds(5)), cancellationToken);
            if (failingServers.Contains(context.Server.Id))
            {
                return new CollectorOutput(string.Empty, "permission denied", 1, TimeSpan.Zero);
            }

            ListenerInfo[] baseListeners = context.Server.Name == "srv-db"
                ? [new("tcp", "LISTEN", "0.0.0.0", 5432, null, "postgres", 23), new("tcp", "LISTEN", "0.0.0.0", 6379, null, "redis-server", 45)]
                : [new("tcp", "LISTEN", "0.0.0.0", 5000, null, "docker-proxy", 77)];
            var all = baseListeners.Concat(ExtraListeners.GetValueOrDefault(context.Server.Id) ?? []).ToArray();
            return new CollectorOutput(JsonSerializer.Serialize(new ListenerDiscoveryResult(all)), string.Empty, 0, TimeSpan.Zero);
        }

        public CollectorResult Parse(CollectorOutput raw) => new(JsonDocument.Parse(raw.StandardOutput).RootElement.Clone());
    }

    private sealed class SteppingTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private long _ticks = start.UtcTicks;

        public override DateTimeOffset GetUtcNow() =>
            new(Interlocked.Add(ref _ticks, TimeSpan.FromSeconds(1).Ticks), TimeSpan.Zero);

        public void Advance(TimeSpan delta) => Interlocked.Add(ref _ticks, delta.Ticks);
    }
}
