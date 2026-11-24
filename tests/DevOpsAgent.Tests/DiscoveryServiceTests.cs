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
    public async Task FailedAndCancelledSnapshotsAreNeverTheGraphBaseButStayTheLatestRun()
    {
        await using var database = await CreateDatabaseAsync();
        var repository = new SnapshotRepository(database.Context);
        var start = new DateTimeOffset(2026, 11, 14, 8, 0, 0, TimeSpan.Zero);
        var good = await repository.CreateAsync([], start, CancellationToken.None);
        await repository.CompleteAsync(good, [], start.AddMinutes(1), 1, 0, cancelled: false, CancellationToken.None);
        var partial = await repository.CreateAsync([], start.AddHours(1), CancellationToken.None);
        await repository.CompleteAsync(partial, [], start.AddHours(1).AddMinutes(1), 1, 1, cancelled: false, CancellationToken.None);
        var failed = await repository.CreateAsync([], start.AddHours(2), CancellationToken.None);
        await repository.FailAsync(failed, [], start.AddHours(2).AddMinutes(1), 0, 0, CancellationToken.None);
        var cancelled = await repository.CreateAsync([], start.AddHours(3), CancellationToken.None);
        await repository.CompleteAsync(cancelled, [], start.AddHours(3).AddMinutes(1), 0, 0, cancelled: true, CancellationToken.None);

        Assert.Equal(partial.Id, (await repository.GetLatestAsync(CancellationToken.None))!.Id);
        Assert.Equal(cancelled.Id, (await repository.GetLatestRunAsync(CancellationToken.None))!.Id);
        Assert.Equal([partial.Id, good.Id], (await repository.ListAsync(CancellationToken.None)).Select(item => item.Id));
    }

    [Fact]
    public async Task RunServerAndMergeAsync_MergesIntoTheLastUsableSnapshotNotAFailedRun()
    {
        await using var database = await CreateDatabaseAsync();
        var existing = AddServer(database.Context, "existing");
        var added = AddServer(database.Context, "added");
        var graphs = new RecordingGraphRepository(new GraphRepository(database.Context));
        var service = new DiscoveryService(new ServerRepository(database.Context), new SnapshotRepository(database.Context), graphs,
            new GraphBuilder(), new UnusedSshRunner(), [new RecordingCollector()], TimeProvider.System);
        var good = await service.RunAsync(progress: null, CancellationToken.None);
        var failed = await new SnapshotRepository(database.Context).CreateAsync([existing.Id], DateTimeOffset.UtcNow.AddMinutes(5), CancellationToken.None);
        await new SnapshotRepository(database.Context).FailAsync(failed, [], DateTimeOffset.UtcNow.AddMinutes(6), 0, 0, CancellationToken.None);

        _ = await service.RunServerAndMergeAsync(added.Id, progress: null, CancellationToken.None);

        Assert.Equal(good.Id, graphs.LastMergeBase);
    }

    private sealed class RecordingGraphRepository(IGraphRepository inner) : IGraphRepository
    {
        public Guid? LastMergeBase { get; private set; }

        public Task SaveAsync(Guid snapshotId, GraphBuildResult graph, CancellationToken cancellationToken) =>
            inner.SaveAsync(snapshotId, graph, cancellationToken);

        public Task MergeAsync(Guid snapshotId, Guid previousSnapshotId, GraphBuildResult graph, CancellationToken cancellationToken)
        {
            LastMergeBase = previousSnapshotId;
            return inner.MergeAsync(snapshotId, previousSnapshotId, graph, cancellationToken);
        }

        public Task<GraphSnapshotDto?> GetAsync(Guid snapshotId, GraphView view, CancellationToken cancellationToken) =>
            inner.GetAsync(snapshotId, view, cancellationToken);

        public Task<GraphBuildResult?> GetEntitiesAsync(Guid snapshotId, CancellationToken cancellationToken) =>
            inner.GetEntitiesAsync(snapshotId, cancellationToken);
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

    [Fact]
    public async Task RunAsync_GraphPersistFailureMarksSnapshotFailedAndRethrows()
    {
        await using var database = await CreateDatabaseAsync();
        AddServer(database.Context, "edge");
        var service = new DiscoveryService(new ServerRepository(database.Context), new SnapshotRepository(database.Context),
            new ThrowingGraphRepository(), new GraphBuilder(), new UnusedSshRunner(),
            [new SuccessfulCollector("host", "hostname: edge")], TimeProvider.System);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunAsync(progress: null, CancellationToken.None));

        var snapshot = await database.Context.Snapshots.AsNoTracking().SingleAsync();
        Assert.Equal(SnapshotStatus.Failed, snapshot.Status);
        Assert.NotNull(snapshot.FinishedAtUtc);
        Assert.Equal(1, await database.Context.CollectorRuns.CountAsync());
    }

    private sealed class ThrowingGraphRepository : IGraphRepository
    {
        public Task SaveAsync(Guid snapshotId, GraphBuildResult graph, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("simulated graph persistence failure");

        public Task MergeAsync(Guid snapshotId, Guid previousSnapshotId, GraphBuildResult graph, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("simulated graph persistence failure");

        public Task<GraphSnapshotDto?> GetAsync(Guid snapshotId, GraphView view, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<GraphBuildResult?> GetEntitiesAsync(Guid snapshotId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
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
