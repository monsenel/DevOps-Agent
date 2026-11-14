using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Application.Monitoring;
using DevOpsAgent.Domain.Discovery;
using DevOpsAgent.Domain.Monitoring;
using DevOpsAgent.Domain.Servers;

namespace DevOpsAgent.Tests;

public sealed class HealthCheckServiceTests
{
    [Fact]
    public async Task CrudValidatesTargetsAndPreservesLatestStatus()
    {
        var fixture = new ServiceFixture();
        var created = await fixture.Service.CreateAsync(fixture.Server.Id,
            new HealthCheckInput("Web", HealthCheckType.Http, "http://app.example.test/health", 60), CancellationToken.None);

        Assert.NotNull(created);
        Assert.Equal(HealthCheckStatus.Unknown, created.Status);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.CreateAsync(fixture.Server.Id,
            new HealthCheckInput("Bad scheme", HealthCheckType.Https, "http://app.example.test/", 60), CancellationToken.None));
        await fixture.Service.RunDueAsync(CancellationToken.None);
        var checkedItem = Assert.Single(await fixture.Service.ListAsync(fixture.Server.Id, CancellationToken.None));
        Assert.Equal(HealthCheckStatus.Healthy, checkedItem.Status);
        Assert.Equal(204, checkedItem.StatusCode);

        var updated = await fixture.Service.UpdateAsync(created.Id,
            new HealthCheckInput("Web updated", HealthCheckType.Http, "http://app.example.test/status", 120, false), CancellationToken.None);
        Assert.Equal("Web updated", updated?.Name);
        Assert.Equal(HealthCheckStatus.Healthy, updated?.Status);
        Assert.True(await fixture.Service.DeleteAsync(created.Id, CancellationToken.None));
        Assert.Empty(await fixture.Service.ListAsync(fixture.Server.Id, CancellationToken.None));
    }

    [Fact]
    public async Task ScheduledChecksRunOnlyWhenTheirIntervalIsDue()
    {
        var fixture = new ServiceFixture();
        _ = await fixture.Service.CreateAsync(fixture.Server.Id,
            new HealthCheckInput("API", HealthCheckType.Http, "http://app.example.test/", 60), CancellationToken.None);

        await fixture.Service.RunDueAsync(CancellationToken.None);
        fixture.Clock.Set(fixture.Clock.GetUtcNow().AddSeconds(30));
        await fixture.Service.RunDueAsync(CancellationToken.None);
        Assert.Equal(1, fixture.Probe.Count);
        fixture.Clock.Set(fixture.Clock.GetUtcNow().AddSeconds(31));
        await fixture.Service.RunDueAsync(CancellationToken.None);
        Assert.Equal(2, fixture.Probe.Count);
    }

    [Fact]
    public async Task SuggestionUsesHttpsServerNamesAndTcpListenersFromLatestGraph()
    {
        var fixture = new ServiceFixture(includeGraph: true);

        var suggestions = await fixture.Service.SuggestAsync(fixture.Server.Id, CancellationToken.None);

        Assert.Contains(suggestions, item => item.Type == HealthCheckType.Https && item.Target == "https://app.example.test/");
        Assert.Contains(suggestions, item => item.Type == HealthCheckType.Tcp && item.Target == "192.0.2.20:5432");
    }

    private sealed class ServiceFixture
    {
        private readonly FakeCheckRepository _checkRepository = new();
        public Server Server { get; } = new("app", "192.0.2.20", 22, "ops", "id_ed25519", "prod");
        public MutableTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);
        public FakeProbe Probe { get; } = new();
        public HealthCheckService Service { get; }

        public ServiceFixture(bool includeGraph = false)
        {
            var snapshots = new FakeSnapshotRepository(includeGraph ? new Snapshot([Server.Id], Clock.GetUtcNow()) : null);
            var graph = includeGraph ? CreateGraph(Server.Id, snapshots.Snapshot!.Id) : null;
            Service = new HealthCheckService(_checkRepository, new FakeServerRepository(Server), snapshots,
                new FakeGraphRepository(graph), Probe, Clock);
        }
    }

    private sealed class FakeCheckRepository : IHealthCheckRepository
    {
        private readonly Dictionary<Guid, HealthCheck> _items = [];
        public Task<IReadOnlyList<HealthCheck>> ListAsync(Guid serverId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<HealthCheck>>(_items.Values.Where(item => item.ServerId == serverId).OrderBy(item => item.Name).ToArray());
        public Task<IReadOnlyList<HealthCheck>> ListEnabledAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<HealthCheck>>(_items.Values.Where(item => item.IsEnabled).ToArray());
        public Task<HealthCheck?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(_items.GetValueOrDefault(id));
        public Task AddAsync(HealthCheck healthCheck, CancellationToken cancellationToken)
        {
            _items.Add(healthCheck.Id, healthCheck);
            return Task.CompletedTask;
        }
        public Task SaveAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteAsync(HealthCheck healthCheck, CancellationToken cancellationToken)
        {
            _items.Remove(healthCheck.Id);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeProbe : IHealthProbe
    {
        public int Count { get; private set; }
        public Task<HealthProbeResult> ProbeAsync(HealthCheckType type, string target, CancellationToken cancellationToken)
        {
            Count++;
            return Task.FromResult(new HealthProbeResult(true, 204, type == HealthCheckType.Https ? 45 : null, null));
        }
    }

    private sealed class FakeServerRepository(Server server) : IServerRepository
    {
        public Task<IReadOnlyList<Server>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Server>>([server]);
        public Task<Server?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(id == server.Id ? server : null);
        public Task AddAsync(Server item, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SaveAsync(Server item, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteAsync(Server item, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeSnapshotRepository(Snapshot? snapshot) : ISnapshotRepository
    {
        public Snapshot? Snapshot { get; } = snapshot;
        public Task<Snapshot?> GetLatestAsync(CancellationToken cancellationToken) => Task.FromResult(Snapshot);
        public Task<IReadOnlyList<Snapshot>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Snapshot>>(Snapshot is null ? [] : [Snapshot]);
        public Task<Snapshot> CreateAsync(IReadOnlyCollection<Guid> serverIds, DateTimeOffset startedAtUtc, CancellationToken cancellationToken) =>
            Task.FromResult(new Snapshot(serverIds, startedAtUtc));
        public Task CompleteAsync(Snapshot value, IReadOnlyCollection<CollectorRun> collectorRuns, DateTimeOffset finishedAtUtc,
            int succeededServerCount, int failedServerCount, bool cancelled, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeGraphRepository(GraphSnapshotDto? graph) : IGraphRepository
    {
        public Task SaveAsync(Guid snapshotId, GraphBuildResult value, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task MergeAsync(Guid snapshotId, Guid previousSnapshotId, GraphBuildResult value, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<GraphSnapshotDto?> GetAsync(Guid snapshotId, GraphView view, CancellationToken cancellationToken) => Task.FromResult(graph);
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Set(DateTimeOffset value) => _now = value;
    }

    private static GraphSnapshotDto CreateGraph(Guid serverId, Guid snapshotId)
    {
        var domainId = Guid.NewGuid().ToString("N");
        var blockId = Guid.NewGuid().ToString("N");
        var hostNodeId = Guid.NewGuid();
        var hostId = hostNodeId.ToString("N");
        var nodes = new[]
        {
            Node(domainId, "Domain", "app.example.test", null),
            Node(blockId, "Service", "server block · app.example.test", serverId.ToString("N")),
            Node(hostId, "Host", "app", serverId.ToString("N"))
        }.Select(value => new GraphElementDto(value)).ToArray();
        var edges = new[]
        {
            new GraphElementDto(JsonElement(JsonSerializer.Serialize(new { source = domainId, target = blockId, protocol = "https", port = 443 })))
        };
        return new GraphSnapshotDto(snapshotId, "all", nodes, edges,
            [new GraphListenerDto(Guid.NewGuid(), hostNodeId, "0.0.0.0", 5432, "tcp", "postgres", 555)], []);
    }

    private static JsonElement Node(string id, string kind, string label, string? serverId) =>
        JsonElement(JsonSerializer.Serialize(new { id, kind, label, serverId }));

    private static JsonElement JsonElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
