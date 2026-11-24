using DevOpsAgent.Application.Agent;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Application.Updates;
using DevOpsAgent.Domain.Discovery;
using DevOpsAgent.Domain.Graph;
using DevOpsAgent.Domain.Monitoring;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Domain.Updates;
using DevOpsAgent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevOpsAgent.Tests;

public sealed class RollingUpdatePlannerTests
{
    [Fact]
    public void PlansLeavesFirstThenAppTierThenReverseProxyLastWithGroupedIndependentServers()
    {
        var dbServer = Guid.NewGuid();
        var workerServer = Guid.NewGuid();
        var app1 = Guid.NewGuid();
        var app2 = Guid.NewGuid();
        var edge = Guid.NewGuid();

        RollingServerInput[] servers =
        [
            new(edge, "srv-edge", "prod", 3, 1, false, ["https-main (Https)"]),
            new(app1, "srv-app-1", "prod", 2, 1, false, ["api-1 (Http)"]),
            new(app2, "srv-app-2", "prod", 2, 0, false, []),
            new(dbServer, "srv-db", "prod", 1, 1, true, ["pg-port (Tcp)"]),
            new(workerServer, "srv-worker", "prod", 1, 0, false, [])
        ];

        // Edge -> App1, Edge -> App2, App1 -> DB, App2 -> DB; Worker is independent leaf
        (Guid, Guid)[] edges =
        [
            (edge, app1),
            (edge, app2),
            (app1, dbServer),
            (app2, dbServer)
        ];

        var plan = RollingUpdatePlanner.Plan(servers, edges, new HashSet<Guid> { edge });

        Assert.False(plan.HasCycleWarning);
        Assert.Equal(3, plan.Waves.Count);

        // Wave 1: independent leaves (srv-db and srv-worker)
        Assert.Equal(["srv-db", "srv-worker"], plan.Waves[0].Servers.Select(s => s.ServerName).ToArray());
        Assert.Contains("Pause after Wave 1", plan.Waves[0].VerificationGate.Summary, StringComparison.Ordinal);
        Assert.Contains(plan.Waves[0].VerificationGate.Checks, c => c.Contains("pg-port (Tcp)", StringComparison.Ordinal));

        // Wave 2: application tier (srv-app-1 and srv-app-2 grouped together)
        Assert.Equal(["srv-app-1", "srv-app-2"], plan.Waves[1].Servers.Select(s => s.ServerName).ToArray());
        Assert.Contains(plan.Waves[1].VerificationGate.Checks, c => c.Contains("api-1 (Http)", StringComparison.Ordinal));
        Assert.Contains(plan.Waves[1].VerificationGate.Checks, c => c.Contains("srv-app-2: verify SSH reachability", StringComparison.Ordinal));

        // Wave 3: reverse proxy / edge last
        var lastWaveServer = Assert.Single(plan.Waves[2].Servers);
        Assert.Equal("srv-edge", lastWaveServer.ServerName);
        Assert.Equal("Reverse proxy / edge", lastWaveServer.TierRole);
        Assert.Contains("Final verification", plan.Waves[2].VerificationGate.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void HandlesCyclicGraphWithoutDeadlockingAndIncludesEveryServer()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var edge = Guid.NewGuid();

        RollingServerInput[] servers =
        [
            new(a, "srv-a", "prod", 1, 0, false, []),
            new(b, "srv-b", "prod", 1, 0, false, []),
            new(edge, "srv-edge", "prod", 1, 0, false, [])
        ];

        var plan = RollingUpdatePlanner.Plan(servers, [(a, b), (b, a), (edge, a)], new HashSet<Guid> { edge });

        Assert.True(plan.HasCycleWarning);
        Assert.Equal(3, plan.TotalServers);
        Assert.Equal("srv-edge", plan.Waves[^1].Servers.Single().ServerName);
    }

    [Fact]
    public async Task BuildPlanAsyncReadsDiscoverySnapshotHealthChecksAndUpdates()
    {
        await using var db = await CreateDatabaseAsync();
        var edge = new Server("srv-edge", "10.0.0.1", 22, "dva", "id_ed25519", "prod");
        var app = new Server("srv-app", "10.0.0.2", 22, "dva", "id_ed25519", "prod");
        db.Context.Servers.AddRange(edge, app);
        db.Context.HealthChecks.Add(new HealthCheck(app.Id, "app-http", HealthCheckType.Http, "http://10.0.0.2:8080/health", 60));
        db.Context.UpdatePackages.Add(new UpdatePackage(app.Id, "openssl", "3.0.1", "3.0.2", "Debian-Security", true, DateTimeOffset.UtcNow));

        var snapshot = new Snapshot([edge.Id, app.Id], DateTimeOffset.UtcNow.AddMinutes(-5));
        snapshot.Complete(DateTimeOffset.UtcNow, 2, 0);
        db.Context.Snapshots.Add(snapshot);

        var domainNode = new GraphNode(snapshot.Id, "domain:api.example.com", GraphNodeKind.Domain, "api.example.com", null, null, "{}");
        var edgeHost = new GraphNode(snapshot.Id, $"host:{edge.Id:N}", GraphNodeKind.Host, edge.Name, null, edge.Id, "{}");
        var edgeNginx = new GraphNode(snapshot.Id, $"service:{edge.Id:N}:nginx", GraphNodeKind.Service, "nginx", edgeHost.Id, edge.Id, "{}");
        var appHost = new GraphNode(snapshot.Id, $"host:{app.Id:N}", GraphNodeKind.Host, app.Name, null, app.Id, "{}");
        var appSvc = new GraphNode(snapshot.Id, $"container:{app.Id:N}:api", GraphNodeKind.Container, "api", appHost.Id, app.Id, "{}");
        db.Context.GraphNodes.AddRange(domainNode, edgeHost, edgeNginx, appHost, appSvc);
        db.Context.GraphEdges.AddRange(
            new GraphEdge(snapshot.Id, domainNode.Id, edgeNginx.Id, GraphEdgeKind.Declared, 443, "tcp"),
            new GraphEdge(snapshot.Id, edgeNginx.Id, appSvc.Id, GraphEdgeKind.Declared, 8080, "tcp"));
        await db.Context.SaveChangesAsync();

        var settings = new AppSettingRepository(db.Context);
        var serverRepo = new ServerRepository(db.Context);
        var planner = new RollingUpdatePlanner(
            serverRepo,
            new SnapshotRepository(db.Context),
            new GraphRepository(db.Context),
            new UpdatePackageRepository(db.Context),
            new HealthCheckRepository(db.Context),
            new ExecutionModeService(settings, serverRepo, NullLogger<ExecutionModeService>.Instance));

        var plan = await planner.BuildPlanAsync(CancellationToken.None);

        Assert.True(plan.BasedOnDiscoveryGraph);
        Assert.Equal(snapshot.Id, plan.SnapshotId);
        Assert.Equal(2, plan.Waves.Count);
        Assert.Equal("srv-app", Assert.Single(plan.Waves[0].Servers).ServerName);
        Assert.Equal(1, plan.Waves[0].Servers[0].SecurityUpdateCount);
        Assert.Equal("srv-edge", Assert.Single(plan.Waves[1].Servers).ServerName);
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
        public async ValueTask DisposeAsync() { await Context.DisposeAsync(); await connection.DisposeAsync(); }
    }
}
