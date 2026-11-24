using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Agent;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Domain.Discovery;
using DevOpsAgent.Domain.Monitoring;
using DevOpsAgent.Domain.Servers;

namespace DevOpsAgent.Tests;

public sealed class AgentContextBuilderTests
{
    [Fact]
    public async Task Build_BroadDailyReviewIncludesWholeInventoryAndGraph()
    {
        var servers = new[]
        {
            new Server("srv-edge", "edge.test", 22, "dva", "id_ed25519", "staging"),
            new Server("srv-db", "db.test", 22, "dva", "id_ed25519", "staging")
        };
        var snapshot = new Snapshot(servers.Select(server => server.Id).ToArray(), DateTimeOffset.UtcNow);
        var snapshots = new FakeSnapshots(snapshot);
        using var node = JsonDocument.Parse("{\"id\":\"srv-db-node\",\"name\":\"srv-db\"}");
        var graphs = new FakeGraphs(new GraphSnapshotDto(snapshot.Id, "all", [new GraphElementDto(node.RootElement.Clone())], [], [], []));
        var builder = new AgentContextBuilder(new FakeServers(servers), snapshots,
            graphs, new FakeAlerts(), new FakeMetrics(), new DriftService(snapshots, graphs));

        var json = await builder.BuildAsync("anything I should look at?", CancellationToken.None);

        using var context = JsonDocument.Parse(json);
        var inventory = context.RootElement.GetProperty("servers").EnumerateArray().ToArray();
        Assert.Equal(2, inventory.Length);
        Assert.Contains(inventory, item => item.GetProperty("Name").GetString() == "srv-edge");
        Assert.Contains(inventory, item => item.GetProperty("Name").GetString() == "srv-db");
        var graphText = context.RootElement.GetProperty("graph").GetString();
        Assert.NotNull(graphText);
        using var graphJson = JsonDocument.Parse(graphText!);
        Assert.Single(graphJson.RootElement.GetProperty("nodes").EnumerateArray());
        Assert.Contains("No previous completed snapshot", context.RootElement.GetProperty("drift").GetString(), StringComparison.Ordinal);
    }

    private sealed class FakeServers(IReadOnlyList<Server> values) : IServerRepository
    {
        public Task<IReadOnlyList<Server>> ListAsync(CancellationToken cancellationToken) => Task.FromResult(values);
        public Task<Server?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(values.SingleOrDefault(server => server.Id == id));
        public Task AddAsync(Server server, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SaveAsync(Server server, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteAsync(Server server, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeSnapshots(Snapshot snapshot) : ISnapshotRepository
    {
        public Task<Snapshot?> GetLatestAsync(CancellationToken cancellationToken) => Task.FromResult<Snapshot?>(snapshot);
        public Task<Snapshot?> GetLatestRunAsync(CancellationToken cancellationToken) => Task.FromResult<Snapshot?>(snapshot);
        public Task<IReadOnlyList<Snapshot>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Snapshot>>([snapshot]);
        public Task<Snapshot> CreateAsync(IReadOnlyCollection<Guid> serverIds, DateTimeOffset startedAtUtc, CancellationToken cancellationToken) => Task.FromResult(snapshot);
        public Task CompleteAsync(Snapshot snapshot, IReadOnlyCollection<CollectorRun> collectorRuns, DateTimeOffset finishedAtUtc,
            int succeededServerCount, int failedServerCount, bool cancelled, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task FailAsync(Snapshot snapshot, IReadOnlyCollection<CollectorRun> collectorRuns, DateTimeOffset finishedAtUtc,
            int succeededServerCount, int failedServerCount, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeGraphs(GraphSnapshotDto graph) : IGraphRepository
    {
        public Task SaveAsync(Guid snapshotId, GraphBuildResult value, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task MergeAsync(Guid snapshotId, Guid previousSnapshotId, GraphBuildResult value, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<GraphSnapshotDto?> GetAsync(Guid snapshotId, GraphView view, CancellationToken cancellationToken) => Task.FromResult<GraphSnapshotDto?>(graph);
        public Task<GraphBuildResult?> GetEntitiesAsync(Guid snapshotId, CancellationToken cancellationToken) => Task.FromResult<GraphBuildResult?>(null);
    }

    private sealed class FakeAlerts : IAlertRepository
    {
        public Task<IReadOnlyList<Alert>> ListAsync(AlertState? state, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Alert>>([]);
        public Task<Alert?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<Alert?>(null);
        public Task<IReadOnlyList<Alert>> ListActiveAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Alert>>([]);
        public Task AddAsync(Alert alert, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SaveAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeMetrics : IMetricRepository
    {
        public Task AddSamplesAsync(IReadOnlyCollection<MetricSample> samples, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<MetricSample>> GetRecentAsync(Guid serverId, DateTimeOffset sinceUtc, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MetricSample>>([]);
        public Task<IReadOnlyList<MetricSample>> GetLatestAsync(Guid serverId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MetricSample>>([]);
        public Task<IReadOnlyList<MetricSample>> GetUnrolledAsync(DateTimeOffset beforeUtc, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MetricSample>>([]);
        public Task SaveRollupsAsync(IReadOnlyCollection<MetricRollup> rollups, DateTimeOffset rawPruneBeforeUtc,
            DateTimeOffset rollupPruneBeforeUtc, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
