using System.Text.Json;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Application.Search;
using DevOpsAgent.Domain.Discovery;
using DevOpsAgent.Domain.Graph;
using DevOpsAgent.Domain.Monitoring;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DevOpsAgent.Tests;

/// <summary>T8.2: the top bar's search over servers, graph nodes, listeners and open alerts.</summary>
public sealed class GlobalSearchServiceTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private AppDbContext _context = null!;
    private GlobalSearchService _search = null!;
    private Server _edge = null!;
    private Server _db = null!;
    private GraphNode _postgres = null!;
    private GraphNode _domain = null!;
    private Alert _alert = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();
        _context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        await _context.Database.EnsureCreatedAsync();
        _edge = new Server("srv-edge", "127.0.0.1", 2201, "dva", "key", "staging");
        _db = new Server("srv-db", "db.lab.internal", 22, "dvapw", "key", "staging");
        _context.Servers.AddRange(_edge, _db);
        var snapshot = new Snapshot([_edge.Id, _db.Id], DateTimeOffset.UtcNow.AddMinutes(-5));
        snapshot.Complete(DateTimeOffset.UtcNow, 2, 0);
        _context.Snapshots.Add(snapshot);
        await _context.SaveChangesAsync();

        var dbHost = new GraphNode(snapshot.Id, $"host:{_db.Id:N}", GraphNodeKind.Host, "srv-db", null, _db.Id,
            JsonSerializer.Serialize(new { Addresses = new[] { "10.88.20.30", "172.18.0.2" } }));
        _postgres = new GraphNode(snapshot.Id, "container:pg", GraphNodeKind.Container, "postgres", dbHost.Id, _db.Id, "{}");
        _domain = new GraphNode(snapshot.Id, "domain:api.test.local", GraphNodeKind.Domain, "api.test.local", null, _edge.Id, "{}");
        var unknown = new GraphNode(snapshot.Id, "unknown:172.26.0.2", GraphNodeKind.Unknown, "Unknown", null, _edge.Id,
            JsonSerializer.Serialize(new { Address = "172.26.0.2" }));
        var extra = Enumerable.Range(0, 30).Select(index =>
            new GraphNode(snapshot.Id, $"container:worker-{index}", GraphNodeKind.Container, $"worker-{index:00}", dbHost.Id, _db.Id, "{}")).ToArray();
        var listeners = new[]
        {
            new GraphListener(snapshot.Id, _postgres.Id, "0.0.0.0", 5432, "tcp", "postgres", 41),
            new GraphListener(snapshot.Id, dbHost.Id, "0.0.0.0", 54321, "tcp", "other", 42)
        };
        await new GraphRepository(_context).SaveAsync(snapshot.Id,
            new GraphBuildResult([dbHost, _postgres, _domain, unknown, .. extra], [], listeners, []), CancellationToken.None);

        _alert = new Alert("container-state:x", "container-state", AlertSeverity.High, _db.Id, "postgres", "Container postgres is exited", "", DateTimeOffset.UtcNow);
        var resolved = new Alert("container-state:y", "container-state", AlertSeverity.High, _db.Id, "postgres-old", "Container postgres-old is exited", "", DateTimeOffset.UtcNow);
        resolved.Resolve(DateTimeOffset.UtcNow);
        _context.Alerts.AddRange(_alert, resolved);
        await _context.SaveChangesAsync();

        _search = new GlobalSearchService(new ServerRepository(_context), new SnapshotRepository(_context), new GraphRepository(_context),
            new AlertRepository(_context));
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task FindsServersByName_HostAndDiscoveredIp()
    {
        Assert.Equal($"/servers/{_db.Id}", Single(await _search.SearchAsync("srv-db", default), SearchResultKind.Server).Url);
        Assert.Equal("srv-edge", Single(await _search.SearchAsync("127.0.0.1", default), SearchResultKind.Server).Title);
        var byIp = Single(await _search.SearchAsync("10.88.20.30", default), SearchResultKind.Server);
        Assert.Equal("srv-db", byIp.Title);
        Assert.Contains("10.88.20.30", byIp.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindsNodes_AndOpensTopologyFocusedOnThem()
    {
        var domain = Single(await _search.SearchAsync("api.test", default), SearchResultKind.Node);
        Assert.Equal($"/topology?node={_domain.Id:N}", domain.Url);
        Assert.Equal("Domain on srv-edge", domain.Detail);
        var unknown = Single(await _search.SearchAsync("172.26.0.2", default), SearchResultKind.Node);
        Assert.Equal("Unknown · 172.26.0.2", unknown.Title);
    }

    [Fact]
    public async Task FindsListenersByExactPort_WithOrWithoutColon()
    {
        foreach (var query in new[] { "5432", ":5432" })
        {
            var groups = await _search.SearchAsync(query, default);
            var listener = Single(groups, SearchResultKind.Listener);
            Assert.Equal(":5432 tcp", listener.Title);
            Assert.Contains("postgres on srv-db", listener.Detail, StringComparison.Ordinal);
            Assert.Equal($"/topology?node={_postgres.Id:N}", listener.Url);
        }
    }

    [Fact]
    public async Task FindsOnlyOpenAlerts_AndLinksToTheAlert()
    {
        var groups = await _search.SearchAsync("exited", default);
        var alert = Single(groups, SearchResultKind.Alert);
        Assert.Equal($"/alerts?alert={_alert.Id}", alert.Url);
    }

    [Fact]
    public async Task GroupsByType_BestMatchesFirst_AtMostTwenty()
    {
        var groups = await _search.SearchAsync("postgres", default);
        Assert.Equal([SearchResultKind.Node, SearchResultKind.Alert], groups.Select(group => group.Kind));
        Assert.Equal("postgres", groups[0].Items[0].Title);

        var many = await _search.SearchAsync("worker", default);
        Assert.Equal(GlobalSearchService.MaxResults, many.Sum(group => group.Items.Count));
        Assert.Empty(await _search.SearchAsync("   ", default));
        Assert.Empty(await _search.SearchAsync("no-such-thing", default));
    }

    private static SearchResult Single(IReadOnlyList<SearchResultGroup> groups, SearchResultKind kind) =>
        Assert.Single(Assert.Single(groups, group => group.Kind == kind).Items);
}
