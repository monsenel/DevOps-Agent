using System.Text.Json;
using DevOpsAgent.Application.Agent;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Domain.Discovery;
using DevOpsAgent.Domain.Graph;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DevOpsAgent.Tests;

/// <summary>T8.7: the impact section of approval cards, on a graph shaped like the lab (domain → nginx → upstream → container).</summary>
public sealed class ImpactServiceTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private AppDbContext _context = null!;
    private Server _edge = null!;
    private Server _app = null!;
    private Server _db = null!;
    private Snapshot _snapshot = null!;
    private readonly FakeClock _clock = new(DateTimeOffset.UtcNow);

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();
        _context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        await _context.Database.EnsureCreatedAsync();
        _edge = new Server("srv-edge", "127.0.0.1", 2201, "dva", "key", "staging");
        _app = new Server("srv-app", "10.88.20.20", 22, "dva", "key", "staging");
        _db = new Server("srv-db", "10.88.20.30", 22, "dva", "key", "staging");
        _context.Servers.AddRange(_edge, _app, _db);
        _snapshot = new Snapshot([_edge.Id, _app.Id, _db.Id], _clock.GetUtcNow().AddMinutes(-10));
        _snapshot.Complete(_clock.GetUtcNow().AddMinutes(-9), 3, 0);
        _context.Snapshots.Add(_snapshot);
        await _context.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private GraphNode Node(GraphNodeKind kind, string name, Server? server, GraphNode? parent = null, string key = "", object? attributes = null) =>
        new(_snapshot.Id, key.Length > 0 ? key : $"{kind}:{name}:{server?.Name}", kind, name, parent?.Id, server?.Id, JsonSerializer.Serialize(attributes ?? new { }));

    private async Task<ImpactService> LabAsync(bool staleApp = false)
    {
        var edgeHost = Node(GraphNodeKind.Host, "srv-edge", _edge);
        var appHost = Node(GraphNodeKind.Host, "srv-app", _app, attributes: staleApp ? new { Stale = true, LastSeenUtc = "2026-11-15T07:13:00Z" } : null);
        var dbHost = Node(GraphNodeKind.Host, "srv-db", _db);
        var nginx = Node(GraphNodeKind.Service, "nginx", _edge, edgeHost, $"service:{_edge.Id:N}:process:nginx");
        var apiBlock = Node(GraphNodeKind.Service, "server block · api.test.local", _edge, nginx, $"service:{_edge.Id:N}:nginx-server:/etc/nginx/conf.d/demo.conf:6",
            new { FilePath = "/etc/nginx/conf.d/demo.conf" });
        var upstream = Node(GraphNodeKind.Service, "upstream · api_backend", _edge, apiBlock, $"service:{_edge.Id:N}:nginx-upstream:api_backend",
            new { FilePath = "/etc/nginx/conf.d/demo.conf" });
        var appBlock = Node(GraphNodeKind.Service, "server block · app.test.local", _edge, nginx, $"service:{_edge.Id:N}:nginx-server:/etc/nginx/conf.d/other.conf:1",
            new { FilePath = "/etc/nginx/conf.d/other.conf" });
        var apiDomain = Node(GraphNodeKind.Domain, "api.test.local", _edge);
        var appDomain = Node(GraphNodeKind.Domain, "app.test.local", _edge);
        var api = Node(GraphNodeKind.Container, "api", _app, appHost, attributes: staleApp ? new { Stale = true, LastSeenUtc = "2026-11-15T07:13:00Z" } : null);
        var web = Node(GraphNodeKind.Container, "web", _app, appHost);
        var worker = Node(GraphNodeKind.Container, "worker-pg", _app, appHost);
        var postgres = Node(GraphNodeKind.Service, "postgres", _db, dbHost);
        var outsider = Node(GraphNodeKind.Unknown, "Unknown", null, attributes: new { Address = "172.26.0.2" });
        GraphEdge Edge(GraphNode source, GraphNode target, GraphEdgeKind kind = GraphEdgeKind.Declared) => new(_snapshot.Id, source.Id, target.Id, kind);
        await new GraphRepository(_context).SaveAsync(_snapshot.Id, new GraphBuildResult(
            [edgeHost, appHost, dbHost, nginx, apiBlock, upstream, appBlock, apiDomain, appDomain, api, web, worker, postgres, outsider],
            [
                Edge(apiDomain, apiBlock), Edge(appDomain, appBlock), Edge(apiBlock, upstream), Edge(upstream, api),
                Edge(appBlock, web), Edge(worker, postgres, GraphEdgeKind.Observed), Edge(outsider, postgres, GraphEdgeKind.Observed),
                Edge(edgeHost, appHost, GraphEdgeKind.NetworkAttachment)
            ], [], []), CancellationToken.None);
        return new ImpactService(new ServerRepository(_context), new SnapshotRepository(_context), new GraphRepository(_context), _clock);
    }

    [Fact]
    public async Task RestartContainer_ListsTheNginxChainTheDomainAndTheEdgeServer()
    {
        var impact = await (await LabAsync()).ForAsync("restart_container", """{"container":"api"}""", _app.Id, default);

        Assert.True(impact.TargetFound);
        Assert.Equal("container api on srv-app", impact.Target);
        Assert.Equal([("upstream · api_backend", 1), ("server block · api.test.local", 2)], impact.Dependents.Select(item => (item.Name, item.Level)));
        Assert.Equal(["api.test.local"], impact.Domains);
        Assert.Equal(["srv-edge"], impact.Servers);
        Assert.Empty(impact.Warnings);
    }

    [Fact]
    public async Task RestartingNginx_OrEditingItsConfig_ReachesEveryDomainItServes()
    {
        var service = await LabAsync();

        var restart = await service.ForAsync("restart_service", """{"unit":"nginx.service"}""", _edge.Id, default);
        var edit = await service.ForAsync("edit_config", """{"path":"/etc/nginx/conf.d/demo.conf"}""", _edge.Id, default);

        Assert.Equal(["api.test.local", "app.test.local"], restart.Domains);
        Assert.Equal(["api.test.local", "app.test.local"], edit.Domains); // nginx reloads: all of it
        Assert.Empty(restart.Dependents); // only domains point at nginx here
    }

    [Fact]
    public async Task WholeServerActions_CountOutsideDependents_AndUnknownClients()
    {
        var impact = await (await LabAsync()).ForAsync("apply_updates", """{"packages":["libpq5"]}""", _db.Id, default);

        Assert.Contains(impact.Dependents, item => item is { Name: "worker-pg", Server: "srv-app", Level: 1 });
        Assert.Contains(impact.Dependents, item => item is { Name: "Unknown · 172.26.0.2", Server: null, Level: 1 });
        Assert.Equal(["srv-app"], impact.Servers);
        Assert.Empty(impact.Domains);
    }

    [Fact]
    public async Task StaleTargets_OldGraphs_MissingTargets_AndNoDiscovery_AreSaid()
    {
        var service = await LabAsync(staleApp: true);
        _clock.Advance(TimeSpan.FromHours(30));

        var stale = await service.ForAsync("restart_container", """{"container":"api"}""", _app.Id, default);
        Assert.Contains(stale.Warnings, item => item.Contains("api is stale", StringComparison.Ordinal) && item.Contains("2026-11-15T07:13:00Z", StringComparison.Ordinal));
        Assert.Contains(stale.Warnings, item => item.Contains("older than 24 h", StringComparison.Ordinal));

        var missing = await service.ForAsync("restart_container", """{"container":"nope"}""", _app.Id, default);
        Assert.False(missing.TargetFound);
        Assert.Contains(missing.Warnings, item => item.Contains("not in the latest discovery graph", StringComparison.Ordinal));

        await using var empty = new SqliteConnection("Data Source=:memory:");
        await empty.OpenAsync();
        await using var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(empty).Options);
        await context.Database.EnsureCreatedAsync();
        var none = await new ImpactService(new ServerRepository(context), new SnapshotRepository(context), new GraphRepository(context), _clock)
            .ForAsync("run_shell", """{"command":"id"}""", Guid.NewGuid(), default);
        Assert.Contains(none.Warnings, item => item.Contains("No discovery has run yet", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpdateCards_NameWhatEachPackageRestarts_AndWarnForDockerAndNginx()
    {
        var service = await LabAsync();

        var app = await service.ForAsync("apply_updates", """{"packages":["docker-ce","docker-ce-cli","libssl3"]}""", _app.Id, default);
        var edge = await service.ForAsync("apply_updates", """{"packages":["nginx-common","openssl"]}""", _edge.Id, default);
        var db = await service.ForAsync("apply_updates", """{"packages":["postgresql-15"]}""", _db.Id, default);

        var docker = app.Packages!.Single(item => item.Package == "docker-ce");
        Assert.True(docker.RestartsDockerOrNginx);
        Assert.Equal(["api", "web", "worker-pg"], docker.Services);
        Assert.False(app.Packages!.Single(item => item.Package == "docker-ce-cli").RestartsDockerOrNginx);
        Assert.Equal("no service in the graph", app.Packages!.Single(item => item.Package == "libssl3").Effect);
        Assert.Contains(app.Warnings, item => item.Contains("docker-ce restarts the Docker daemon on srv-app: 3 containers restart", StringComparison.Ordinal));

        var nginx = edge.Packages!.Single(item => item.Package == "nginx-common");
        Assert.True(nginx.RestartsDockerOrNginx);
        Assert.Equal(["api.test.local", "app.test.local"], nginx.Services);
        Assert.Contains(edge.Warnings, item => item.Contains("nginx-common restarts nginx on srv-edge: api.test.local, app.test.local are briefly unavailable", StringComparison.Ordinal));

        Assert.Equal(["postgres"], db.Packages!.Single().Services);
        Assert.Equal("restarts postgres", db.Packages!.Single().Effect);
        Assert.DoesNotContain(db.Warnings, item => item.Contains("restarts", StringComparison.Ordinal));
        Assert.Null((await service.ForAsync("restart_container", """{"container":"api"}""", _app.Id, default)).Packages);
    }

    [Fact]
    public async Task TheAgentIsToldToCheckImpactBeforeProposingAChange()
    {
        await using var chat = await ChatServiceHarness.CreateAsync();
        await chat.SendAsync(await chat.Service.CreateAsync(default), "hello");

        var system = chat.Requests[0][0].Text;
        Assert.Contains("Before proposing a mutating action", system, StringComparison.Ordinal);
        Assert.Contains("call query_graph", system, StringComparison.Ordinal);
    }

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
