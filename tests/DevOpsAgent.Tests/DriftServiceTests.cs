using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Domain.Discovery;
using DevOpsAgent.Domain.Graph;
using DevOpsAgent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DevOpsAgent.Tests;

public sealed class DriftServiceTests
{
    [Fact]
    public async Task CompareAsyncReportsNodeListenerAndEdgeChangesAcrossSnapshots()
    {
        await using var database = await CreateDatabaseAsync();
        var serverId = Guid.NewGuid();
        var beforeSnapshot = new Snapshot([serverId], DateTimeOffset.UtcNow.AddHours(-1));
        var afterSnapshot = new Snapshot([serverId], DateTimeOffset.UtcNow);
        beforeSnapshot.Complete(DateTimeOffset.UtcNow.AddMinutes(-59), 1, 0);
        afterSnapshot.Complete(DateTimeOffset.UtcNow, 1, 0);
        database.Context.Snapshots.AddRange(beforeSnapshot, afterSnapshot);
        await database.Context.SaveChangesAsync();

        var oldHost = new GraphNode(beforeSnapshot.Id, "host:app", GraphNodeKind.Host, "app", null, serverId, "{}");
        var oldApi = new GraphNode(beforeSnapshot.Id, "service:api", GraphNodeKind.Service, "api", oldHost.Id, serverId, "{\"Image\":\"v1\"}");
        var oldDb = new GraphNode(beforeSnapshot.Id, "service:db", GraphNodeKind.Service, "db", oldHost.Id, serverId, "{}");
        var oldEdge = new GraphEdge(beforeSnapshot.Id, oldApi.Id, oldDb.Id, GraphEdgeKind.Declared, 5432, "tcp");
        var beforeGraph = new GraphBuildResult(
            [oldHost, oldApi, oldDb], [oldEdge],
            [new GraphListener(beforeSnapshot.Id, oldApi.Id, "0.0.0.0", 8080, "tcp", "kestrel", 100),
             new GraphListener(beforeSnapshot.Id, oldDb.Id, "0.0.0.0", 5432, "tcp", "postgres", 200)], []);

        var newHost = new GraphNode(afterSnapshot.Id, "host:app", GraphNodeKind.Host, "app", null, serverId, "{}");
        var newApi = new GraphNode(afterSnapshot.Id, "service:api", GraphNodeKind.Service, "api", newHost.Id, serverId, "{\"Image\":\"v2\"}");
        var newPostgres = new GraphNode(afterSnapshot.Id, "service:postgres", GraphNodeKind.Service, "postgres", newHost.Id, serverId, "{}");
        var newEdge = new GraphEdge(afterSnapshot.Id, newApi.Id, newPostgres.Id, GraphEdgeKind.Declared, 5433, "tcp");
        var afterGraph = new GraphBuildResult(
            [newHost, newApi, newPostgres], [newEdge],
            [new GraphListener(afterSnapshot.Id, newApi.Id, "0.0.0.0", 8080, "tcp", "kestrel", 101),
             new GraphListener(afterSnapshot.Id, newPostgres.Id, "0.0.0.0", 9000, "tcp", "node", 300)], []);

        var graphRepository = new GraphRepository(database.Context);
        await graphRepository.SaveAsync(beforeSnapshot.Id, beforeGraph, CancellationToken.None);
        await graphRepository.SaveAsync(afterSnapshot.Id, afterGraph, CancellationToken.None);
        var service = new DriftService(new SnapshotRepository(database.Context), graphRepository);

        var report = await service.CompareAsync(beforeSnapshot.Id, afterSnapshot.Id, CancellationToken.None);

        Assert.Contains(report.Changes, change => change.ChangeType == DriftChangeType.Added
            && change.EntityType == DriftEntityType.Node && change.Key == "service:postgres");
        Assert.Contains(report.Changes, change => change.ChangeType == DriftChangeType.Removed
            && change.EntityType == DriftEntityType.Node && change.Key == "service:db");
        Assert.Contains(report.Changes, change => change.ChangeType == DriftChangeType.Changed
            && change.EntityType == DriftEntityType.Node && change.Key == "service:api");
        Assert.Contains(report.Changes, change => change.ChangeType == DriftChangeType.Changed
            && change.EntityType == DriftEntityType.Listener && change.Description.Contains(":8080", StringComparison.Ordinal));
        Assert.Contains(report.NewListeners, change => change.Description.Contains(":9000", StringComparison.Ordinal));
        Assert.Contains(report.Changes, change => change.ChangeType == DriftChangeType.Removed
            && change.EntityType == DriftEntityType.Listener && change.Description.Contains(":5432", StringComparison.Ordinal));
        Assert.Contains(report.Changes, change => change.ChangeType == DriftChangeType.Added
            && change.EntityType == DriftEntityType.Edge && change.Description.Contains(":5433", StringComparison.Ordinal));
        Assert.Contains(report.Changes, change => change.ChangeType == DriftChangeType.Removed
            && change.EntityType == DriftEntityType.Edge && change.Description.Contains(":5432", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DiscoveryScheduleServiceUsesSixHourDefaultAndValidatesSavedInterval()
    {
        await using var database = await CreateDatabaseAsync();
        var service = new DiscoveryScheduleService(new AppSettingRepository(database.Context));

        Assert.Equal(6, await service.GetIntervalHoursAsync(CancellationToken.None));
        await service.SaveIntervalHoursAsync(12, CancellationToken.None);
        Assert.Equal(12, await service.GetIntervalHoursAsync(CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => service.SaveIntervalHoursAsync(0, CancellationToken.None));
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
}
