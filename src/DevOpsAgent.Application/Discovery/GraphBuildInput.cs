using DevOpsAgent.Domain.Servers;

namespace DevOpsAgent.Application.Discovery;

public sealed record ServerDiscoveryData(
    Server Server,
    HostDiscoveryResult? Host,
    ListenerDiscoveryResult? Listeners,
    DockerDiscoveryResult? Docker,
    SystemdDiscoveryResult? Systemd,
    NginxDiscoveryResult? Nginx = null,
    ConnectionDiscoveryResult? Connections = null,
    FirewallDiscoveryResult? Firewall = null,
    TlsDiscoveryResult? Tls = null,
    ReverseProxyDiscoveryResult? ReverseProxy = null,
    AppConfigDiscoveryResult? AppConfig = null);

public sealed record GraphBuildResult(
    IReadOnlyList<DevOpsAgent.Domain.Graph.GraphNode> Nodes,
    IReadOnlyList<DevOpsAgent.Domain.Graph.GraphEdge> Edges,
    IReadOnlyList<DevOpsAgent.Domain.Graph.GraphListener> Listeners,
    IReadOnlyList<DevOpsAgent.Domain.Graph.GraphEvidence> Evidence);
