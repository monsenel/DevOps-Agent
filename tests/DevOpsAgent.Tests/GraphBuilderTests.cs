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
    public void Build_StripsCredentialsFromProxyTargetsInEvidenceAndNodes()
    {
        var data = ThreeServerFixtures()[0] with
        {
            Nginx = new NginxDiscoveryResult(
                [
                    new NginxServerInfo("http", [new NginxSourceValue("80", "/etc/nginx/nginx.conf", 3)],
                        [new NginxSourceValue("app.example.test", "/etc/nginx/nginx.conf", 4)], [],
                        [new NginxProxyInfo("proxy_pass", "http://svc:hunter2@10.9.9.9:8080/api", false, "/etc/nginx/nginx.conf", 5)],
                        "/etc/nginx/nginx.conf", 2)
                ], [])
        };
        var graph = new GraphBuilder().Build(Guid.NewGuid(), [data]);

        Assert.Contains(graph.Evidence, item => item.Collector == "nginx" && item.Description.Contains("proxy_pass", StringComparison.Ordinal));
        Assert.DoesNotContain(graph.Evidence, item => item.Description.Contains("hunter2", StringComparison.Ordinal));
        Assert.DoesNotContain(graph.Nodes, node => node.Name.Contains("hunter2", StringComparison.Ordinal)
            || node.AttributesJson.Contains("hunter2", StringComparison.Ordinal));
        Assert.Equal("10.9.9.9", NginxEndpointParser.Parse("http://u:p@10.9.9.9/").Address);
        Assert.DoesNotContain("secret", NginxEndpointParser.Parse("redis://:secret@$backend").Address, StringComparison.Ordinal);
        Assert.Equal("http://[::1]:8080", UrlUserInfo.Strip("http://user:secret@[::1]:8080"));
        Assert.Equal("mail me at a@b.c", UrlUserInfo.Strip("mail me at a@b.c"));
    }

    [Fact]
    public void Build_HostNodeCarriesTheMachineHostnameFromHostDiscovery()
    {
        var data = ThreeServerFixtures()[0];

        var graph = new GraphBuilder().Build(Guid.NewGuid(), [data]);

        using var discovered = System.Text.Json.JsonDocument.Parse(graph.Nodes.Single(node => node.Kind == GraphNodeKind.Host).AttributesJson);
        Assert.Equal("edge-a", discovered.RootElement.GetProperty("Hostname").GetString());
    }

    [Fact]
    public void Build_HostnameIsNullWhenHostDiscoveryDidNotRun()
    {
        var data = ThreeServerFixtures()[0] with { Host = null };

        var graph = new GraphBuilder().Build(Guid.NewGuid(), [data]);

        using var attributes = System.Text.Json.JsonDocument.Parse(graph.Nodes.Single(node => node.Kind == GraphNodeKind.Host).AttributesJson);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, attributes.RootElement.GetProperty("Hostname").ValueKind);
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

    [Fact]
    public async Task GraphRepositoryMergeReplacesUnknownWithDiscoveredServiceAndRemapsEvidence()
    {
        await using var database = await CreateDatabaseAsync();
        var observerId = Guid.NewGuid();
        var discoveredServerId = Guid.NewGuid();
        var previousSnapshot = new Snapshot([observerId], DateTimeOffset.UtcNow);
        var currentSnapshot = new Snapshot([observerId, discoveredServerId], DateTimeOffset.UtcNow);
        database.Context.Snapshots.AddRange(previousSnapshot, currentSnapshot);
        await database.Context.SaveChangesAsync();

        var observer = new GraphNode(previousSnapshot.Id, "host:observer", GraphNodeKind.Host,
            "observer", null, observerId, "{}");
        var unknown = new GraphNode(previousSnapshot.Id, "unknown:10.0.0.2:5432", GraphNodeKind.Unknown,
            "Unknown", null, observerId, "{\"Address\":\"10.0.0.2\",\"Port\":5432}");
        var observed = new GraphEdge(previousSnapshot.Id, observer.Id, unknown.Id, GraphEdgeKind.Observed, 5432, "tcp");
        var observedEvidence = new GraphEvidence(previousSnapshot.Id, null, observed.Id,
            "connections", "10.0.0.1:48000 → 10.0.0.2:5432");
        var repository = new GraphRepository(database.Context);
        await repository.SaveAsync(previousSnapshot.Id,
            new GraphBuildResult([observer, unknown], [observed], [], [observedEvidence]), CancellationToken.None);

        var discoveredHost = new GraphNode(currentSnapshot.Id, "host:database", GraphNodeKind.Host,
            "database", null, discoveredServerId, "{\"Addresses\":[\"10.0.0.2\"]}");
        var discoveredService = new GraphNode(currentSnapshot.Id, "service:postgres", GraphNodeKind.Service,
            "postgres", discoveredHost.Id, discoveredServerId, "{}");
        var listener = new GraphListener(currentSnapshot.Id, discoveredService.Id, "0.0.0.0", 5432,
            "tcp", "postgres", 430);
        await repository.MergeAsync(currentSnapshot.Id, previousSnapshot.Id,
            new GraphBuildResult([discoveredHost, discoveredService], [], [listener], []), CancellationToken.None);

        var merged = await repository.GetAsync(currentSnapshot.Id, GraphView.Service, CancellationToken.None);

        Assert.NotNull(merged);
        Assert.DoesNotContain(merged!.Nodes, node => node.Data.GetProperty("kind").GetString() == nameof(GraphNodeKind.Unknown));
        Assert.Contains(merged.Nodes, node => node.Data.GetProperty("label").GetString() == "observer");
        Assert.Contains(merged.Nodes, node => node.Data.GetProperty("label").GetString() == "database");
        var mergedEdge = Assert.Single(merged.Edges);
        Assert.Equal(5432, mergedEdge.Data.GetProperty("port").GetInt32());
        Assert.Equal("service:postgres", merged.Nodes.Single(node =>
            node.Data.GetProperty("id").GetString() == mergedEdge.Data.GetProperty("target").GetString())
            .Data.GetProperty("key").GetString());
        Assert.Contains(merged.Evidence, item => item.Collector == "connections"
            && item.Description == "10.0.0.1:48000 → 10.0.0.2:5432");
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
