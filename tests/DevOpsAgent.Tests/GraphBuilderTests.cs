using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Domain.Discovery;
using DevOpsAgent.Domain.Graph;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Infrastructure.Collectors;
using DevOpsAgent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DevOpsAgent.Tests;

public sealed class GraphBuilderTests
{
    [Fact]
    public void Build_ThreeServerFixturesCreateStableNodesAndContainment()
    {
        var data = ThreeServerFixtures();
        var builder = new GraphBuilder();

        var graph = builder.Build(Guid.NewGuid(), data);
        var repeated = builder.Build(Guid.NewGuid(), data);

        Assert.Equal(3, graph.Nodes.Count(node => node.Kind == GraphNodeKind.Host));
        Assert.Equal(12, graph.Nodes.Count(node => node.Kind == GraphNodeKind.Subnet));
        Assert.Equal(6, graph.Nodes.Count(node => node.Kind == GraphNodeKind.Container));
        Assert.Equal(18, graph.Nodes.Count(node => node.Kind == GraphNodeKind.Service));
        Assert.Equal(24, graph.Edges.Count(edge => edge.Kind == GraphEdgeKind.NetworkAttachment));
        Assert.Equal(15, graph.Listeners.Count);
        Assert.Equal(graph.Nodes.Select(node => node.Key).OrderBy(key => key),
            repeated.Nodes.Select(node => node.Key).OrderBy(key => key));

        var host = graph.Nodes.Single(node => node.Key == $"host:{data[0].Server.Id:N}");
        var container = graph.Nodes.Single(node => node.Key == $"container:{data[0].Server.Id:N}:api-svc");
        Assert.Equal(host.Id, container.ParentNodeId);
        var composeServiceKey = $"service:{data[0].Server.Id:N}:compose:app-stack/api";
        var composeService = graph.Nodes.Single(node => node.Key == composeServiceKey);
        Assert.Equal(container.Id, composeService.ParentNodeId);
        var systemdService = graph.Nodes.Single(node => node.Key == $"service:{data[0].Server.Id:N}:systemd:nginx.service");
        Assert.Equal(container.Id, systemdService.ParentNodeId);
        Assert.Contains(graph.Listeners, listener => listener.ProcessId == 220 && listener.NodeId == composeService.Id);
    }

    [Fact]
    public void IpIndexNormalizesAddressesAndPreservesMultipleOwners()
    {
        var index = new IpIndex();
        index.Add("2001:db8::10/64", "container:a");
        index.Add("2001:0db8:0:0:0:0:0:10", "container:b");

        Assert.Equal(new[] { "container:a", "container:b" }, index.Find("2001:db8::10"));
        Assert.Empty(index.Find("not-an-ip"));
    }

    [Fact]
    public async Task GraphRepositoryServesServiceAndNetworkViews()
    {
        await using var database = await CreateDatabaseAsync();
        var data = ThreeServerFixtures();
        var snapshot = new Snapshot(data.Select(item => item.Server.Id).ToArray(), DateTimeOffset.UtcNow);
        database.Context.Snapshots.Add(snapshot);
        await database.Context.SaveChangesAsync();
        var repository = new GraphRepository(database.Context);
        var builder = new GraphBuilder();
        var graph = builder.Build(snapshot.Id, data);
        await repository.SaveAsync(snapshot.Id, graph, CancellationToken.None);
        await repository.SaveAsync(snapshot.Id, builder.Build(snapshot.Id, data), CancellationToken.None);

        var serviceView = await repository.GetAsync(snapshot.Id, GraphView.Service, CancellationToken.None);
        var networkView = await repository.GetAsync(snapshot.Id, GraphView.Network, CancellationToken.None);

        Assert.NotNull(serviceView);
        Assert.NotNull(networkView);
        Assert.DoesNotContain(serviceView!.Nodes, node => node.Data.GetProperty("kind").GetString() == nameof(GraphNodeKind.Subnet));
        Assert.Contains(serviceView.Nodes, node => node.Data.GetProperty("kind").GetString() == nameof(GraphNodeKind.Service));
        Assert.Contains(networkView!.Nodes, node => node.Data.GetProperty("kind").GetString() == nameof(GraphNodeKind.Subnet));
        Assert.DoesNotContain(networkView.Nodes, node => node.Data.GetProperty("kind").GetString() == nameof(GraphNodeKind.Service));
        Assert.Contains(networkView.Edges, edge => edge.Data.GetProperty("kind").GetString() == nameof(GraphEdgeKind.NetworkAttachment));
        Assert.Null(await repository.GetAsync(Guid.NewGuid(), GraphView.Service, CancellationToken.None));
    }

    [Fact]
    public async Task GraphRepositoryMarksDeclaredEdgeAsBothWhenObservedEvidenceExists()
    {
        await using var database = await CreateDatabaseAsync();
        var serverId = Guid.NewGuid();
        var snapshot = new Snapshot([serverId], DateTimeOffset.UtcNow);
        database.Context.Snapshots.Add(snapshot);
        await database.Context.SaveChangesAsync();
        var source = new GraphNode(snapshot.Id, "host:source", GraphNodeKind.Host, "source", null, serverId, "{}");
        var target = new GraphNode(snapshot.Id, "service:target", GraphNodeKind.Service, "target", source.Id, serverId, "{}");
        var declared = new GraphEdge(snapshot.Id, source.Id, target.Id, GraphEdgeKind.Declared, 5432, "tcp");
        var evidence = new GraphEvidence(snapshot.Id, null, declared.Id, "connections", "Observed socket pair.");
        var repository = new GraphRepository(database.Context);

        await repository.SaveAsync(snapshot.Id, new GraphBuildResult([source, target], [declared], [], [evidence]), CancellationToken.None);
        var graph = await repository.GetAsync(snapshot.Id, GraphView.Service, CancellationToken.None);

        Assert.NotNull(graph);
        Assert.Equal("Both", Assert.Single(graph!.Edges).Data.GetProperty("kind").GetString());
        Assert.Equal("Observed socket pair.", Assert.Single(graph.Evidence).Description);
    }

    private static IReadOnlyList<ServerDiscoveryData> ThreeServerFixtures()
    {
        var addresses = Fixture("host-ip-addr.json");
        var routes = Fixture("host-ip-route.json");
        var hosts = Fixture("host-etc-hosts.txt");
        var containers = Fixture("docker-inspect.json");
        var networks = Fixture("docker-network-inspect.json");
        var listeners = ListenerCollectorParser.Parse(Fixture("ss-tulpn.txt"));
        var units = SystemdCollectorParser.Parse(Fixture("systemd-list-units.txt"),
            new Dictionary<string, string>
            {
                ["nginx.service"] = Fixture("systemd-show-nginx.txt"),
                ["sshd.service"] = Fixture("systemd-show-sshd.txt"),
                ["postgresql.service"] = Fixture("systemd-show-postgresql.txt")
            }, new HashSet<int> { 220, 1000 });
        var docker = DockerCollectorParser.Parse(containers, networks);

        return
        [
            CreateServerData("edge-a", "198.51.100.", "2001:db8:2::", addresses, routes, hosts, listeners, docker, units),
            CreateServerData("edge-b", "203.0.113.", "2001:db8:3::", addresses, routes, hosts, listeners, docker, units),
            CreateServerData("edge-c", "192.0.2.", "2001:db8:1::", addresses, routes, hosts, listeners, docker, units)
        ];
    }

    private static ServerDiscoveryData CreateServerData(
        string name,
        string ipv4Prefix,
        string ipv6Prefix,
        string addresses,
        string routes,
        string hosts,
        IReadOnlyList<ListenerInfo> listeners,
        DockerDiscoveryResult docker,
        SystemdDiscoveryResult units)
    {
        var ipv6Original = "2001:db8:1::";
        var host = HostCollectorParser.Parse(
            addresses.Replace("192.0.2.", ipv4Prefix, StringComparison.Ordinal)
                .Replace(ipv6Original, ipv6Prefix, StringComparison.Ordinal),
            routes.Replace("192.0.2.", ipv4Prefix, StringComparison.Ordinal)
                .Replace(ipv6Original, ipv6Prefix, StringComparison.Ordinal),
            $"{name}\n",
            hosts.Replace("192.0.2.", ipv4Prefix, StringComparison.Ordinal));
        var server = new Server(name, $"{name}.example.internal", 22, "deploy", "id_ed25519", "prod");
        return new ServerDiscoveryData(server, host, new ListenerDiscoveryResult(listeners), docker, units);
    }

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

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
