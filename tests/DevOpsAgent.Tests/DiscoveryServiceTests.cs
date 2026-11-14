using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Domain.Discovery;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DevOpsAgent.Tests;

public sealed class DiscoveryServiceTests
{
    [Fact]
    public async Task RunAsync_PersistsSnapshotAndCollectorOutput()
    {
        await using var database = await CreateDatabaseAsync();
        var server = AddServer(database.Context, "edge");
        var collector = new SuccessfulCollector("host", "hostname: edge");
        var service = CreateService(database.Context, collector);
        var progress = new RecordingProgress();

        var snapshot = await service.RunAsync(progress, CancellationToken.None);
        var persistedRun = await database.Context.CollectorRuns.SingleAsync();

        Assert.Equal(SnapshotStatus.Completed, snapshot.Status);
        Assert.Equal(1, snapshot.SucceededServerCount);
        Assert.Equal(server.Id, persistedRun.ServerId);
        Assert.Equal("hostname: edge", persistedRun.RawOutput);
        Assert.Equal(CollectorRunStatus.Succeeded, persistedRun.Status);
        Assert.Contains(progress.Events, item => item.Stage == DiscoveryProgressStage.CollectorCompleted);
        Assert.Contains(progress.Events, item => item.Stage == DiscoveryProgressStage.Completed);
    }

    [Fact]
    public async Task RunAsync_OneFailingServerProducesPartialSnapshotAndContinuesOtherServers()
    {
        await using var database = await CreateDatabaseAsync();
        var failingServer = AddServer(database.Context, "broken");
        var healthyServer = AddServer(database.Context, "healthy");
        var collector = new ServerSpecificCollector(failingServer.Id);
        var service = CreateService(database.Context, collector);

        var snapshot = await service.RunAsync(progress: null, CancellationToken.None);
        var runs = await database.Context.CollectorRuns.OrderBy(run => run.ServerId).ToArrayAsync();

        Assert.Equal(SnapshotStatus.Partial, snapshot.Status);
        Assert.Equal(1, snapshot.SucceededServerCount);
        Assert.Equal(1, snapshot.FailedServerCount);
        Assert.Equal(2, runs.Length);
        Assert.Equal(CollectorRunStatus.Failed, runs.Single(run => run.ServerId == failingServer.Id).Status);
        Assert.Equal(CollectorRunStatus.Succeeded, runs.Single(run => run.ServerId == healthyServer.Id).Status);
    }

    [Fact]
    public async Task RunAsync_RunsCollectorsSequentiallyWithinEachServer()
    {
        await using var database = await CreateDatabaseAsync();
        var server = AddServer(database.Context, "edge");
        var state = new CollectorSequence();
        var service = CreateService(database.Context,
            new SequenceCollector("first", state, first: true),
            new SequenceCollector("second", state, first: false));

        var snapshot = await service.RunAsync(progress: null, CancellationToken.None);
        var runs = await database.Context.CollectorRuns.Where(run => run.ServerId == server.Id).ToArrayAsync();

        Assert.Equal(SnapshotStatus.Completed, snapshot.Status);
        Assert.Equal(2, runs.Length);
        Assert.All(runs, run => Assert.Equal(CollectorRunStatus.Succeeded, run.Status));
    }

    [Fact]
    public async Task RunServerAndMergeAsyncCollectsOnlyRequestedServerAndKeepsInventoryInSnapshot()
    {
        await using var database = await CreateDatabaseAsync();
        var existingServer = AddServer(database.Context, "existing");
        var addedServer = AddServer(database.Context, "added");
        var collector = new RecordingCollector();
        var service = CreateService(database.Context, collector);
        _ = await service.RunAsync(progress: null, CancellationToken.None);
        collector.ServerIds.Clear();

        var snapshot = await service.RunServerAndMergeAsync(addedServer.Id, progress: null, CancellationToken.None);
        var snapshotServerIds = JsonSerializer.Deserialize<Guid[]>(snapshot.ServerIdsJson);

        Assert.Equal([addedServer.Id], collector.ServerIds);
        Assert.NotNull(snapshotServerIds);
        Assert.Contains(existingServer.Id, snapshotServerIds!);
        Assert.Contains(addedServer.Id, snapshotServerIds!);
        Assert.Equal(SnapshotStatus.Completed, snapshot.Status);
    }

    private static DiscoveryService CreateService(AppDbContext dbContext, params ICollector[] collectors) => new(
        new ServerRepository(dbContext),
        new SnapshotRepository(dbContext),
        new GraphRepository(dbContext),
        new GraphBuilder(),
        new UnusedSshRunner(),
        collectors,
        TimeProvider.System);

    private static Server AddServer(AppDbContext dbContext, string name)
    {
        var server = new Server(name, $"{name}.example.internal", 22, "deploy", "id_ed25519", "prod");
        dbContext.Servers.Add(server);
        dbContext.SaveChanges();
        return server;
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

    private sealed class SuccessfulCollector(string name, string output) : ICollector
    {
        public string Name => name;

        public Task<bool> IsApplicableAsync(ServerContext context, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<CollectorOutput> CollectAsync(ServerContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new CollectorOutput(output, string.Empty, 0, TimeSpan.FromMilliseconds(2)));

        public CollectorResult Parse(CollectorOutput raw) =>
            new(JsonSerializer.SerializeToElement(new { text = raw.StandardOutput }));
    }

    private sealed class RecordingCollector : ICollector
    {
        public string Name => "recording";
        public List<Guid> ServerIds { get; } = [];

        public Task<bool> IsApplicableAsync(ServerContext context, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<CollectorOutput> CollectAsync(ServerContext context, CancellationToken cancellationToken)
        {
            ServerIds.Add(context.Server.Id);
            return Task.FromResult(new CollectorOutput("ok", string.Empty, 0, TimeSpan.Zero));
        }

        public CollectorResult Parse(CollectorOutput raw) => new();
    }

    private sealed class ServerSpecificCollector(Guid failingServerId) : ICollector
    {
        public string Name => "probe";

        public Task<bool> IsApplicableAsync(ServerContext context, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<CollectorOutput> CollectAsync(ServerContext context, CancellationToken cancellationToken) =>
            context.Server.Id == failingServerId
                ? throw new InvalidOperationException("simulated collector failure")
                : Task.FromResult(new CollectorOutput("ok", string.Empty, 0, TimeSpan.Zero));

        public CollectorResult Parse(CollectorOutput raw) => new();
    }

    private sealed class RecordingProgress : IProgress<DiscoveryProgress>
    {
        public List<DiscoveryProgress> Events { get; } = [];
        public void Report(DiscoveryProgress value) => Events.Add(value);
    }

    private sealed class CollectorSequence
    {
        public bool FirstCompleted { get; set; }
    }

    private sealed class SequenceCollector(string name, CollectorSequence sequence, bool first) : ICollector
    {
        public string Name => name;

        public Task<bool> IsApplicableAsync(ServerContext context, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<CollectorOutput> CollectAsync(ServerContext context, CancellationToken cancellationToken)
        {
            if (first)
            {
                sequence.FirstCompleted = true;
                return Task.FromResult(new CollectorOutput("first", string.Empty, 0, TimeSpan.Zero));
            }

            if (!sequence.FirstCompleted)
            {
                throw new InvalidOperationException("The second collector ran before the first collector completed.");
            }

            return Task.FromResult(new CollectorOutput("second", string.Empty, 0, TimeSpan.Zero));
        }

        public CollectorResult Parse(CollectorOutput raw) => new();
    }

    private sealed class UnusedSshRunner : ISshRunner
    {
        public Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options,
            CancellationToken cancellationToken) => throw new NotSupportedException("Fake collectors do not use SSH.");
    }
}
