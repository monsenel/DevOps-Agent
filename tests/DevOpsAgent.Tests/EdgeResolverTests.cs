using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Domain.Graph;
using DevOpsAgent.Domain.Servers;
using System.Text.Json;

namespace DevOpsAgent.Tests;

public sealed class EdgeResolverTests
{
    [Fact]
    public void Resolve_UsesDockerDnsOnlyForSharedNetworkAndMatchesAlias()
    {
        var source = Data("proxy", docker: Docker(
            Container("proxy", "nginx:latest", "edge", "proxy", Endpoint("front", "172.20.0.2", "nginx")),
            Container("api", "sample/api", "edge", "api", Endpoint("front", "172.20.0.3", "api", "api-alias"))));
        var unrelated = Data("other", docker: Docker(Container("db", "postgres", "other", "db",
            Endpoint("private", "172.21.0.3", "db"))));

        var result = new EdgeResolver().Resolve(new NginxEndpoint("api-alias", 8080, "http", false),
            source, [source, unrelated], new IpIndex());

        Assert.Equal($"service:{source.Server.Id:N}:compose:edge/api", result.NodeKey);
        Assert.False(result.IsUnknown);
        Assert.Contains("Docker DNS", result.Reason);
    }

    [Fact]
    public void Resolve_UsesEtcHostsThenGlobalIpIndexAndListener()
    {
        var target = Data("target", host: Host("10.0.0.2"), listeners: new ListenerDiscoveryResult(
            [new ListenerInfo("tcp", "LISTEN", "0.0.0.0", 9000, null, "api", 42)]));
        var source = Data("proxy", host: Host("10.0.0.1", new EtcHostsEntry("10.0.0.2", ["backend.local"])));
        var index = new IpIndex();
        index.Add("10.0.0.2", $"host:{target.Server.Id:N}");

        var result = new EdgeResolver().Resolve(new NginxEndpoint("backend.local", 9000, "tcp", false),
            source, [source, target], index);

        Assert.Equal($"service:{target.Server.Id:N}:process:api", result.NodeKey);
        Assert.False(result.IsUnknown);
        Assert.Contains("/etc/hosts", result.Reason);
    }

    [Fact]
    public void Resolve_UsesGetentThenGlobalIpIndexAndPublishedPort()
    {
        var source = Data("proxy");
        var target = Data("target", host: Host("10.0.0.2"), docker: Docker(Container("api", "sample/api", "prod", "api",
            Endpoint("private", "172.18.0.4", "api"), published: new DockerPublishedPort(3000, "tcp", "0.0.0.0", 8080))));
        source = source with { Nginx = new NginxDiscoveryResult([], [])
        {
            NameResolutions = new Dictionary<string, IReadOnlyList<string>> { ["backend.internal"] = ["10.0.0.2"] }
        } };
        var index = new IpIndex();
        index.Add("10.0.0.2", $"host:{target.Server.Id:N}");

        var result = new EdgeResolver().Resolve(new NginxEndpoint("backend.internal", 8080, "http", false),
            source, [source, target], index);

        Assert.Equal($"service:{target.Server.Id:N}:compose:prod/api", result.NodeKey);
        Assert.False(result.IsUnknown);
        Assert.Contains("published host port", result.Reason);
    }

    [Fact]
    public void Resolve_UsesDockerContainerIpFromGlobalIndex()
    {
        var target = Data("target", docker: Docker(Container("api", "sample/api", null, null,
            Endpoint("private", "172.18.0.4", "api"))));
        var index = new IpIndex();
        index.Add("172.18.0.4", $"container:{target.Server.Id:N}:api");

        var result = new EdgeResolver().Resolve(new NginxEndpoint("172.18.0.4", 3000, "tcp", false),
            Data("proxy"), [target], index);

        Assert.Equal($"container:{target.Server.Id:N}:api", result.NodeKey);
        Assert.False(result.IsUnknown);
    }

    [Fact]
    public void Resolve_ReturnsStableUnknownForVariablesAndUnmatchedTargets()
    {
        var source = Data("proxy");
        var resolver = new EdgeResolver();
        var unresolved = resolver.Resolve(NginxEndpointParser.Parse("http://$backend"), source, [source], new IpIndex());
        var unknown = resolver.Resolve(new NginxEndpoint("missing.internal", 80, "http", false),
            source, [source], new IpIndex());

        Assert.True(unresolved.IsUnknown);
        Assert.Contains("variable", unresolved.Reason);
        Assert.True(unknown.IsUnknown);
        Assert.Equal($"unknown:{source.Server.Id:N}:missing.internal:80", unknown.NodeKey);
    }

    [Fact]
    public void GraphBuilder_ExpandsUpstreamAndAddsDomainAndLineEvidence()
    {
        var target = Data("target", host: Host("10.0.0.2"), docker: Docker(Container("api", "sample/api", "prod", "api",
            Endpoint("private", "172.18.0.4", "api"), published: new DockerPublishedPort(3000, "tcp", "0.0.0.0", 8080))));
        var source = Data("proxy", host: Host("10.0.0.1")) with
        {
            Nginx = new NginxDiscoveryResult(
                [new NginxServerInfo("http", [new NginxSourceValue("80", "/etc/nginx/sites-enabled/app", 4)],
                    [new NginxSourceValue("app.example.test", "/etc/nginx/sites-enabled/app", 5)], [],
                    [new NginxProxyInfo("proxy_pass", "http://api_pool", false, "/etc/nginx/sites-enabled/app", 12)],
                    "/etc/nginx/sites-enabled/app", 3)],
                [new NginxUpstreamInfo("http", "api_pool",
                    [new NginxUpstreamServerInfo("10.0.0.2:8080", null, [], "/etc/nginx/conf.d/upstream.conf", 2)],
                    "/etc/nginx/conf.d/upstream.conf", 1)])
        };
        var result = new GraphBuilder().Build(Guid.NewGuid(), [source, target]);
        var targetService = result.Nodes.Single(node => node.Key == $"service:{target.Server.Id:N}:compose:prod/api");
        var edge = result.Edges.Single(item => item.Kind == GraphEdgeKind.Declared && item.TargetNodeId == targetService.Id);
        var domain = result.Nodes.Single(node => node.Key == "domain:app.example.test");
        var serverBlock = result.Nodes.Single(node => node.Key == $"service:{source.Server.Id:N}:nginx-server:/etc/nginx/sites-enabled/app:3");
        var upstreamNode = result.Nodes.Single(node => node.Key == $"service:{source.Server.Id:N}:nginx-upstream:api_pool");

        Assert.Contains(result.Edges, item => item.Kind == GraphEdgeKind.Declared && item.SourceNodeId == domain.Id && item.TargetNodeId == serverBlock.Id);
        Assert.Contains(result.Edges, item => item.Kind == GraphEdgeKind.Declared && item.SourceNodeId == serverBlock.Id && item.TargetNodeId == upstreamNode.Id);
        Assert.Contains(result.Edges, item => item.Kind == GraphEdgeKind.Declared && item.SourceNodeId == upstreamNode.Id && item.TargetNodeId == targetService.Id);
        Assert.Equal(8080, edge.Port);
        var evidence = result.Evidence.Single(item => item.EdgeId == edge.Id);
        Assert.Contains("/etc/nginx/sites-enabled/app:12", evidence.Description);
        Assert.Contains("/etc/nginx/conf.d/upstream.conf:2", evidence.Description);
        Assert.Contains("published host port", evidence.Description);
    }

    [Fact]
    public void GraphTraceResolver_TracesUrlAcrossDownstreamEdgesAndSkipsUnrelatedNodes()
    {
        var graph = new GraphSnapshotDto(Guid.NewGuid(), "service",
            [Node("domain-id", "Domain", "api.example.test"), Node("block-id", "Service", "server block · api.example.test"),
                Node("upstream-id", "Service", "upstream · api_pool"), Node("api-id", "Service", "api"),
                Node("db-id", "Service", "postgres"), Node("other-id", "Domain", "other.example.test")],
            [Edge("domain-edge", "domain-id", "block-id", "Declared"), Edge("block-edge", "block-id", "upstream-id", "Declared"),
                Edge("api-edge", "upstream-id", "api-id", "Declared"), Edge("db-edge", "api-id", "db-id", "Observed"),
                Edge("other-edge", "other-id", "db-id", "Declared")], [], []);

        var result = GraphTraceResolver.Resolve(graph, "https://API.example.test/v1/items?active=true");

        Assert.NotNull(result);
        Assert.Equal("api.example.test", result.Hostname);
        Assert.Equal(new[] { "domain-id", "block-id", "upstream-id", "api-id", "db-id" }.OrderBy(value => value),
            result.NodeIds.OrderBy(value => value));
        Assert.Equal(new[] { "domain-edge", "block-edge", "api-edge", "db-edge" }.OrderBy(value => value),
            result.EdgeIds.OrderBy(value => value));
        Assert.Null(GraphTraceResolver.Resolve(graph, "missing.example.test"));
    }

    private static ServerDiscoveryData Data(string name, HostDiscoveryResult? host = null,
        ListenerDiscoveryResult? listeners = null, DockerDiscoveryResult? docker = null) =>
        new(new Server(name, $"{name}.test", 22, "deploy", "key", "prod"), host, listeners, docker, null);

    private static HostDiscoveryResult Host(string address, params EtcHostsEntry[] hosts) => new("host",
        [new HostInterface(2, "eth0", null, "UP", 1500,
            [new HostAddress("inet", address, 24, $"{address[..(address.LastIndexOf('.') + 1)]}0/24", "global", null)])],
        [], hosts);

    private static DockerDiscoveryResult Docker(params DockerContainerInfo[] containers) => new(containers, []);

    private static DockerContainerInfo Container(string name, string image, string? project, string? service,
        DockerNetworkEndpoint endpoint, DockerPublishedPort? published = null) => new(name, name, image, "running", project,
        service, 0, [endpoint], published is null ? [] : [published], []);

    private static DockerNetworkEndpoint Endpoint(string network, string address, params string[] aliases) =>
        new(network, network, address, null, aliases);

    private static GraphElementDto Node(string id, string kind, string label) =>
        new(JsonSerializer.SerializeToElement(new { id, kind, label }));

    private static GraphElementDto Edge(string id, string source, string target, string kind) =>
        new(JsonSerializer.SerializeToElement(new { id, source, target, kind }));
}
