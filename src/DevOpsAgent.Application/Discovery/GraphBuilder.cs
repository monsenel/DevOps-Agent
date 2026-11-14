using System.Text.Json;
using DevOpsAgent.Domain.Discovery;
using DevOpsAgent.Domain.Graph;

namespace DevOpsAgent.Application.Discovery;

public sealed class GraphBuilder
{
    public GraphBuildResult Build(Guid snapshotId, IReadOnlyCollection<ServerDiscoveryData> serverData)
    {
        var nodes = new List<GraphNode>();
        var edges = new List<GraphEdge>();
        var graphListeners = new List<GraphListener>();
        var evidence = new List<GraphEvidence>();
        var nodeByKey = new Dictionary<string, GraphNode>(StringComparer.Ordinal);
        var subnetByKey = new Dictionary<string, GraphNode>(StringComparer.Ordinal);
        var ipIndex = new IpIndex();
        var serverStates = new List<ServerGraphState>();

        foreach (var data in serverData.OrderBy(item => item.Server.Id))
        {
            var server = data.Server;
            var hostKey = $"host:{server.Id:N}";
            var hostAddresses = data.Host is { Interfaces: not null } hostResult
                ? hostResult.Interfaces.SelectMany(iface => iface.Addresses).ToArray()
                : [];
            var host = AddNode(hostKey, GraphNodeKind.Host, server.Name, null, server.Id,
                new
                {
                    server.Host,
                    server.Port,
                    server.User,
                    server.Group,
                    Tags = ParseTags(server.TagsJson),
                    Addresses = hostAddresses.Select(address => address.Address).Distinct(StringComparer.Ordinal).ToArray()
                });
            if (data.Host is not null)
            {
                evidence.Add(new GraphEvidence(snapshotId, host.Id, null, "host", "Host identity and interface inventory."));
            }

            foreach (var address in hostAddresses)
            {
                ipIndex.Add(address.Address, hostKey);
                if (!string.Equals(address.Scope, "global", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var subnetKey = $"subnet:{address.Family}:{address.SubnetCidr}";
                if (!subnetByKey.TryGetValue(subnetKey, out var subnet))
                {
                    subnet = AddNode(subnetKey, GraphNodeKind.Subnet, address.SubnetCidr, null, null,
                        new { address.Family, address.SubnetCidr });
                    subnetByKey.Add(subnetKey, subnet);
                    evidence.Add(new GraphEvidence(snapshotId, subnet.Id, null, "host", "Subnet derived from interface address and prefix."));
                }

                AddNetworkAttachment(host, subnet);
            }

            var containerByMainPid = new Dictionary<int, GraphNode>();
            var containerById = new Dictionary<string, GraphNode>(StringComparer.Ordinal);
            var composeServiceByContainerId = new Dictionary<string, GraphNode>(StringComparer.Ordinal);
            if (data.Docker is not null)
            {
                foreach (var container in data.Docker.Containers)
                {
                    var containerKey = $"container:{server.Id:N}:{container.Name}";
                    var networkEndpoints = container.Networks.ToList();
                    foreach (var network in data.Docker.Networks)
                    {
                        var endpoint = network.Containers.FirstOrDefault(item => item.Id == container.Id);
                        if (endpoint is not null && !networkEndpoints.Any(item => item.NetworkName == network.Name))
                        {
                            networkEndpoints.Add(new DockerNetworkEndpoint(network.Name, network.Id,
                                endpoint.IPv4Address, endpoint.IPv6Address, endpoint.Aliases));
                        }
                    }

                    var containerNode = AddNode(containerKey, GraphNodeKind.Container, container.Name, host.Id, server.Id,
                        new
                        {
                            container.Id,
                            container.Image,
                            container.State,
                            container.ComposeProject,
                            container.ComposeService,
                            container.MainPid,
                            Networks = networkEndpoints,
                            container.PublishedPorts,
                            container.EnvironmentVariableNames
                        });
                    containerById[container.Id] = containerNode;
                    if (container.MainPid > 0)
                    {
                        containerByMainPid.TryAdd(container.MainPid, containerNode);
                    }

                    foreach (var endpoint in networkEndpoints)
                    {
                        if (endpoint.IPv4Address is not null)
                        {
                            ipIndex.Add(endpoint.IPv4Address, containerKey);
                        }

                        if (endpoint.IPv6Address is not null)
                        {
                            ipIndex.Add(endpoint.IPv6Address, containerKey);
                        }
                    }

                    evidence.Add(new GraphEvidence(snapshotId, containerNode.Id, null, "docker", "Container inspect and network endpoint data."));
                    if (!string.IsNullOrWhiteSpace(container.ComposeService))
                    {
                        var project = string.IsNullOrWhiteSpace(container.ComposeProject) ? "default" : container.ComposeProject;
                        var serviceKey = $"service:{server.Id:N}:compose:{project}/{container.ComposeService}";
                        var service = AddNode(serviceKey, GraphNodeKind.Service, container.ComposeService,
                            containerNode.Id, server.Id,
                            new { container.ComposeProject, container.ComposeService, container.Image });
                        composeServiceByContainerId[container.Id] = service;
                        evidence.Add(new GraphEvidence(snapshotId, service.Id, null, "docker", "Compose service label."));
                    }
                }

                foreach (var network in data.Docker.Networks)
                {
                    foreach (var cidr in network.Subnets)
                    {
                        var separator = cidr.IndexOf('/');
                        var ipText = separator < 0 ? cidr : cidr[..separator];
                        if (!System.Net.IPAddress.TryParse(ipText, out var ipAddress))
                        {
                            continue;
                        }

                        var family = ipAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "inet" : "inet6";
                        var subnetKey = $"subnet:docker:{server.Id:N}:{network.Name}:{family}:{cidr}";
                        if (!subnetByKey.TryGetValue(subnetKey, out var subnet))
                        {
                            subnet = AddNode(subnetKey, GraphNodeKind.Subnet, cidr, null, server.Id,
                                new { Family = family, SubnetCidr = cidr, Network = network.Name, network.Driver });
                            subnetByKey.Add(subnetKey, subnet);
                            evidence.Add(new GraphEvidence(snapshotId, subnet.Id, null, "docker", "Subnet from Docker network IPAM configuration."));
                        }

                        AddNetworkAttachment(host, subnet);
                        foreach (var endpoint in network.Containers)
                        {
                            if (containerById.TryGetValue(endpoint.Id, out var endpointNode))
                            {
                                AddNetworkAttachment(endpointNode, subnet);
                            }
                        }
                    }
                }
            }

            var systemdServiceByPid = new Dictionary<int, GraphNode>();
            if (data.Systemd is not null)
            {
                foreach (var unit in data.Systemd.Units)
                {
                    var parent = containerByMainPid.TryGetValue(unit.MainPid, out var container)
                        ? container
                        : host;
                    var serviceKey = $"service:{server.Id:N}:systemd:{unit.Name}";
                    var service = AddNode(serviceKey, GraphNodeKind.Service, unit.Name,
                        parent.Id, server.Id,
                        new { unit.MainPid, unit.ExecStart, unit.FragmentPath, unit.ListenerPids });
                    systemdServiceByPid[unit.MainPid] = service;
                    evidence.Add(new GraphEvidence(snapshotId, service.Id, null, "systemd", "Running unit owns one or more listener PIDs."));
                }
            }

            if (data.Listeners is not null)
            {
                foreach (var listener in data.Listeners.Listeners)
                {
                    GraphNode owner;
                    if (listener.ProcessId is > 0 && containerByMainPid.TryGetValue(listener.ProcessId.Value, out var containerNode))
                    {
                        owner = data.Docker?.Containers.FirstOrDefault(item => item.MainPid == listener.ProcessId.Value) is { } dockerContainer &&
                            composeServiceByContainerId.TryGetValue(dockerContainer.Id, out var composeService)
                                ? composeService
                                : containerNode;
                    }
                    else if (listener.ProcessId is > 0 && systemdServiceByPid.TryGetValue(listener.ProcessId.Value, out var systemdService))
                    {
                        owner = systemdService;
                    }
                    else if (!string.IsNullOrWhiteSpace(listener.ProcessName))
                    {
                        var processKey = $"service:{server.Id:N}:process:{listener.ProcessName}";
                        owner = nodeByKey.TryGetValue(processKey, out var knownProcess)
                            ? knownProcess
                            : AddNode(processKey, GraphNodeKind.Service, listener.ProcessName, host.Id, server.Id,
                                new { listener.ProcessName, listener.ProcessId });
                    }
                    else if (listener.Address != "*" && ipIndex.Find(listener.Address).Count == 1 &&
                             nodeByKey.TryGetValue(ipIndex.Find(listener.Address)[0], out var addressOwner))
                    {
                        owner = addressOwner;
                    }
                    else
                    {
                        owner = host;
                    }

                    graphListeners.Add(new GraphListener(snapshotId, owner.Id, listener.Address, listener.Port,
                        listener.Protocol, listener.ProcessName, listener.ProcessId));
                    evidence.Add(new GraphEvidence(snapshotId, owner.Id, null, "listeners",
                        $"{listener.Protocol} {listener.Address}:{listener.Port} ({listener.ProcessName ?? "unknown process"})."));
                }
            }

            serverStates.Add(new ServerGraphState(data, host, containerByMainPid, containerById, composeServiceByContainerId,
                systemdServiceByPid));
        }

        var edgeResolver = new EdgeResolver();
        foreach (var state in serverStates.Where(state => state.Data.Nginx is not null))
        {
            var nginx = state.Data.Nginx!;
            var nginxNode = GetNginxNode(state);
            foreach (var serverBlock in nginx.Servers)
            {
                var blockName = serverBlock.ServerNames.Select(value => value.Value).FirstOrDefault()
                    ?? $"default server · {serverBlock.FilePath ?? "nginx"}:{serverBlock.Line}";
                var blockKey = $"service:{state.Data.Server.Id:N}:nginx-server:{serverBlock.FilePath}:{serverBlock.Line}";
                var blockNode = AddNode(blockKey, GraphNodeKind.Service, $"server block · {blockName}",
                    nginxNode.Id, state.Data.Server.Id,
                    new { serverBlock.Context, serverBlock.FilePath, serverBlock.Line, ServerNames = serverBlock.ServerNames.Select(value => value.Value).ToArray() });
                var listenPort = serverBlock.Listens.Select(value => TryParseListenPort(value.Value))
                    .FirstOrDefault(port => port.HasValue) ?? (serverBlock.Listens.Any(value => value.Value == "ssl") ? 443 : 80);
                var listenProtocol = listenPort == 443 || serverBlock.Listens.Any(value => value.Value == "ssl") ? "https" : "http";
                foreach (var serverName in serverBlock.ServerNames.Where(value => IsDomainName(value.Value)))
                {
                    var normalizedName = serverName.Value.Trim().ToLowerInvariant();
                    var domain = AddNode($"domain:{normalizedName}", GraphNodeKind.Domain, serverName.Value,
                        null, null, new { Domain = serverName.Value });
                    var edge = AddDeclaredEdge(domain, blockNode, listenPort, listenProtocol);
                    evidence.Add(new GraphEvidence(snapshotId, null, edge.Id, "nginx",
                        FormatEvidence(serverName.FilePath, serverName.Line, $"server_name {serverName.Value};")));
                }

                foreach (var proxy in serverBlock.Proxies)
                {
                    var target = NginxEndpointParser.Parse(proxy.Target);
                    var upstream = nginx.Upstreams.FirstOrDefault(item =>
                        string.Equals(item.Name, target.Address, StringComparison.OrdinalIgnoreCase));
                    var targets = upstream is null
                        ? new[] { (Endpoint: target, Server: (NginxUpstreamServerInfo?)null) }
                        : upstream.Servers.Select(server =>
                            (Endpoint: NginxEndpointParser.Parse(server.Address), Server: (NginxUpstreamServerInfo?)server)).ToArray();
                    if (targets.Length == 0) targets = [(target, null)];
                    var sourceNode = blockNode;
                    if (upstream is not null)
                    {
                        var upstreamNode = AddNode($"service:{state.Data.Server.Id:N}:nginx-upstream:{upstream.Name.ToLowerInvariant()}",
                            GraphNodeKind.Service, $"upstream · {upstream.Name}", blockNode.Id, state.Data.Server.Id,
                            new { upstream.Name, upstream.FilePath, upstream.Line, Context = upstream.Context });
                        var upstreamEdge = AddDeclaredEdge(blockNode, upstreamNode, null, target.Protocol);
                        evidence.Add(new GraphEvidence(snapshotId, null, upstreamEdge.Id, "nginx",
                            FormatEvidence(proxy.FilePath, proxy.Line, $"{proxy.Directive} {proxy.Target}; upstream {upstream.Name}")));
                        sourceNode = upstreamNode;
                    }

                    foreach (var (endpoint, upstreamServer) in targets)
                    {
                        var resolution = edgeResolver.Resolve(endpoint, state.Data, serverData, ipIndex);
                        if (!nodeByKey.TryGetValue(resolution.NodeKey, out var targetNode))
                        {
                            targetNode = resolution.IsUnknown
                                ? AddNode(resolution.NodeKey, GraphNodeKind.Unknown, "Unknown", null, state.Data.Server.Id,
                                    new { Address = resolution.TargetName, endpoint.Port, endpoint.Protocol, resolution.Reason, endpoint.IsUnresolved })
                                : AddNode($"unknown:{state.Data.Server.Id:N}:{endpoint.Address}:{endpoint.Port?.ToString() ?? "none"}",
                                    GraphNodeKind.Unknown, "Unknown", null, state.Data.Server.Id,
                                    new { Address = endpoint.Address, endpoint.Port, endpoint.Protocol, resolution.Reason, endpoint.IsUnresolved });
                        }

                        var edge = AddDeclaredEdge(sourceNode, targetNode, endpoint.Port, endpoint.Protocol);
                        var upstreamEvidence = upstreamServer is null
                            ? string.Empty
                            : $" {upstreamServer.FilePath ?? "nginx configuration"}:{upstreamServer.Line}: server {upstreamServer.Address};";
                        evidence.Add(new GraphEvidence(snapshotId, null, edge.Id, "nginx",
                            FormatEvidence(proxy.FilePath, proxy.Line,
                                $"{proxy.Directive} {proxy.Target};{upstreamEvidence} {resolution.Reason}")));
                    }
                }
            }
        }

        foreach (var state in serverStates.Where(state => state.Data.Connections is not null))
        {
            foreach (var note in state.Data.Connections!.Notes)
            {
                evidence.Add(new GraphEvidence(snapshotId, state.Host.Id, null, "connections", note));
            }

            foreach (var connection in state.Data.Connections!.Connections)
            {
                var localServerNode = FindLocalListenerNode(state.Data, connection.LocalAddress, connection.LocalPort);
                var peerServerNode = FindPeerListenerNode(connection.PeerAddress, connection.PeerPort);
                if ((localServerNode is null) == (peerServerNode is null)) continue;

                GraphNode sourceNode;
                GraphNode targetNode;
                int serverPort;
                if (localServerNode is not null)
                {
                    var peerOwner = FindUniqueOwnerNode(connection.PeerAddress);
                    sourceNode = peerOwner ?? AddObservedUnknown(state.Data.Server.Id, connection.PeerAddress, connection.PeerPort);
                    targetNode = localServerNode;
                    serverPort = connection.LocalPort;
                }
                else
                {
                    sourceNode = FindConnectionSourceNode(state, connection);
                    targetNode = peerServerNode!;
                    serverPort = connection.PeerPort;
                }

                var seenAt = state.Data.Connections.CollectedAtUtc is { } collectedAt
                    ? $"seen-at {collectedAt:O}; "
                    : "seen during the discovery snapshot; ";
                AddObservedEdge(sourceNode, targetNode, serverPort,
                    $"{seenAt}tcp {connection.LocalAddress}:{connection.LocalPort} ↔ {connection.PeerAddress}:{connection.PeerPort}; collected in {connection.Namespace} network namespace{(connection.ContainerName is null ? string.Empty : $" ({connection.ContainerName})") }.");
            }
        }

        return new GraphBuildResult(nodes, edges, graphListeners, evidence);

        GraphNode AddNode(
            string key,
            GraphNodeKind kind,
            string name,
            Guid? parentNodeId,
            Guid? serverId,
            object attributes)
        {
            if (nodeByKey.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var node = new GraphNode(snapshotId, key, kind, name, parentNodeId, serverId, JsonSerializer.Serialize(attributes));
            nodeByKey.Add(key, node);
            nodes.Add(node);
            return node;
        }

        void AddNetworkAttachment(GraphNode source, GraphNode target)
        {
            if (!edges.Any(edge => edge.SourceNodeId == source.Id && edge.TargetNodeId == target.Id &&
                edge.Kind == GraphEdgeKind.NetworkAttachment))
            {
                edges.Add(new GraphEdge(snapshotId, source.Id, target.Id, GraphEdgeKind.NetworkAttachment));
            }
        }

        GraphEdge AddDeclaredEdge(GraphNode source, GraphNode target, int? port, string? protocol)
        {
            var existing = edges.FirstOrDefault(edge => edge.SourceNodeId == source.Id && edge.TargetNodeId == target.Id
                && edge.Port == port && edge.Protocol == protocol && edge.Kind == GraphEdgeKind.Declared);
            if (existing is not null) return existing;
            var edge = new GraphEdge(snapshotId, source.Id, target.Id, GraphEdgeKind.Declared, port, protocol);
            edges.Add(edge);
            return edge;
        }

        GraphNode GetNginxNode(ServerGraphState state) =>
            state.SystemdServiceByPid.Values.FirstOrDefault(node => node.Name.Contains("nginx", StringComparison.OrdinalIgnoreCase))
            ?? state.ComposeServiceByContainerId.Values.FirstOrDefault(node => node.Name.Contains("nginx", StringComparison.OrdinalIgnoreCase)
                || node.Name.Contains("proxy", StringComparison.OrdinalIgnoreCase))
            ?? nodeByKey.Values.FirstOrDefault(node => node.ServerId == state.Data.Server.Id
                && node.Kind == GraphNodeKind.Service && node.Name.Contains("nginx", StringComparison.OrdinalIgnoreCase))
            ?? state.Host;

        GraphNode FindConnectionSourceNode(ServerGraphState state, EstablishedConnectionInfo connection)
        {
            if (connection.Namespace == "container" && connection.ContainerName is not null
                && state.Data.Docker?.Containers.FirstOrDefault(container => container.Name == connection.ContainerName) is { } container)
            {
                return nodeByKey.GetValueOrDefault(GetComposeServiceNodeKey(state.Data, container))
                    ?? nodeByKey.GetValueOrDefault(GetContainerNodeKey(state.Data, container)) ?? state.Host;
            }

            var localOwners = ipIndex.Find(connection.LocalAddress)
                .Where(key => !key.StartsWith("subnet:", StringComparison.Ordinal)).ToArray();
            return localOwners.Length == 1 && nodeByKey.TryGetValue(localOwners[0], out var localNode)
                ? localNode
                : state.Host;
        }

        GraphNode? FindLocalListenerNode(ServerDiscoveryData data, string address, int port) =>
            data.Listeners?.Listeners.Where(listener => listener.Port == port && ListenerAddressMatches(listener.Address, address))
                .Select(listener => FindListenerNode(data, listener)).FirstOrDefault()
            ?? data.Docker?.Containers.Where(container => container.Networks.Any(network => network.IPv4Address == address || network.IPv6Address == address)
                    && container.PublishedPorts.Any(published => published.ContainerPort == port && published.Protocol == "tcp"))
                .Select(container => nodeByKey.GetValueOrDefault(GetComposeServiceNodeKey(data, container))
                    ?? nodeByKey.GetValueOrDefault(GetContainerNodeKey(data, container))).OfType<GraphNode>().FirstOrDefault();

        GraphNode? FindPeerListenerNode(string address, int port)
        {
            var owners = ipIndex.Find(address).Where(key => !key.StartsWith("subnet:", StringComparison.Ordinal)).ToArray();
            var matchingServers = new List<GraphNode>();
            foreach (var ownerKey in owners)
            {
                if (!TryGetOwnerServerId(ownerKey, out var ownerServerId)) continue;
                var targetState = serverStates.FirstOrDefault(item => item.Data.Server.Id == ownerServerId);
                if (targetState is null) continue;
                var listener = targetState.Data.Listeners?.Listeners.FirstOrDefault(item => item.Port == port
                    && ListenerAddressMatches(item.Address, address));
                if (listener is not null)
                {
                    matchingServers.Add(FindListenerNode(targetState.Data, listener));
                    continue;
                }

                if (ownerKey.StartsWith("host:", StringComparison.Ordinal))
                {
                    var published = targetState.Data.Docker?.Containers
                        .Where(item => item.PublishedPorts.Any(value => value.HostPort == port && value.Protocol == "tcp"))
                        .ToArray() ?? [];
                    matchingServers.AddRange(published.Select(item => nodeByKey.GetValueOrDefault(GetComposeServiceNodeKey(targetState.Data, item))
                        ?? nodeByKey.GetValueOrDefault(GetContainerNodeKey(targetState.Data, item))).OfType<GraphNode>());
                }
                else if (TryGetContainer(targetState.Data, ownerKey) is { } container
                    && container.PublishedPorts.Any(value => value.ContainerPort == port && value.Protocol == "tcp"))
                {
                    var serviceNode = nodeByKey.GetValueOrDefault(GetComposeServiceNodeKey(targetState.Data, container))
                        ?? nodeByKey.GetValueOrDefault(GetContainerNodeKey(targetState.Data, container));
                    if (serviceNode is not null) matchingServers.Add(serviceNode);
                }
            }

            return matchingServers.DistinctBy(node => node.Id).Count() == 1
                ? matchingServers.DistinctBy(node => node.Id).Single()
                : null;
        }

        GraphNode? FindUniqueOwnerNode(string address)
        {
            var owners = ipIndex.Find(address).Where(key => !key.StartsWith("subnet:", StringComparison.Ordinal))
                .Select(key => nodeByKey.GetValueOrDefault(key)).Where(node => node is not null).DistinctBy(node => node!.Id).ToArray();
            return owners.Length == 1 ? owners[0] : null;
        }

        GraphNode FindListenerNode(ServerDiscoveryData data, ListenerInfo listener)
        {
            if (listener.ProcessId is > 0)
            {
                var container = data.Docker?.Containers.FirstOrDefault(item => item.MainPid == listener.ProcessId.Value);
                if (container is not null)
                {
                    return nodeByKey.GetValueOrDefault(GetComposeServiceNodeKey(data, container))
                        ?? nodeByKey.GetValueOrDefault(GetContainerNodeKey(data, container))!;
                }

                var unit = data.Systemd?.Units.FirstOrDefault(item => item.MainPid == listener.ProcessId.Value);
                if (unit is not null && nodeByKey.TryGetValue($"service:{data.Server.Id:N}:systemd:{unit.Name}", out var systemdNode))
                {
                    return systemdNode;
                }
            }

            if (!string.IsNullOrWhiteSpace(listener.ProcessName)
                && nodeByKey.TryGetValue($"service:{data.Server.Id:N}:process:{listener.ProcessName}", out var processNode))
            {
                return processNode;
            }

            return nodeByKey.GetValueOrDefault($"host:{data.Server.Id:N}")!;
        }

        void AddObservedEdge(GraphNode source, GraphNode target, int port, string description)
        {
            var declared = edges.FirstOrDefault(edge => edge.SourceNodeId == source.Id && edge.TargetNodeId == target.Id
                && edge.Port == port && IsTcpProtocol(edge.Protocol) && edge.Kind == GraphEdgeKind.Declared);
            if (declared is not null)
            {
                evidence.Add(new GraphEvidence(snapshotId, null, declared.Id, "connections", description));
                return;
            }

            var existing = edges.FirstOrDefault(edge => edge.SourceNodeId == source.Id && edge.TargetNodeId == target.Id
                && edge.Port == port && edge.Protocol == "tcp" && edge.Kind == GraphEdgeKind.Observed);
            var observed = existing ?? new GraphEdge(snapshotId, source.Id, target.Id, GraphEdgeKind.Observed, port, "tcp");
            if (existing is null) edges.Add(observed);
            evidence.Add(new GraphEvidence(snapshotId, null, observed.Id, "connections", description));
        }

        GraphNode AddObservedUnknown(Guid serverId, string address, int port) =>
            AddNode($"unknown:observed:{address}:{port}", GraphNodeKind.Unknown, "Unknown", null, serverId,
                new { Address = address, Port = port, Reason = "Remote endpoint was observed but is not inventoried." });

        static bool ListenerAddressMatches(string listenerAddress, string connectionAddress) =>
            listenerAddress is "*" or "0.0.0.0" or "::" || string.Equals(listenerAddress.Trim('%'), connectionAddress.Trim('%'), StringComparison.OrdinalIgnoreCase);

        bool TryGetOwnerServerId(string key, out Guid id)
        {
            id = Guid.Empty;
            var separator = key.IndexOf(':');
            return separator >= 0 && Guid.TryParseExact(key[(separator + 1)..].Split(':', 2)[0], "N", out id);
        }

        DockerContainerInfo? TryGetContainer(ServerDiscoveryData data, string ownerKey)
        {
            var separator = ownerKey.IndexOf(':');
            if (separator < 0) return null;
            var remainder = ownerKey[(separator + 1)..];
            var parts = remainder.Split(':', 2);
            return parts.Length == 2 ? data.Docker?.Containers.FirstOrDefault(item => item.Name == parts[1]) : null;
        }

        static string GetContainerNodeKey(ServerDiscoveryData data, DockerContainerInfo container) =>
            $"container:{data.Server.Id:N}:{container.Name}";

        static string GetComposeServiceNodeKey(ServerDiscoveryData data, DockerContainerInfo container) =>
            string.IsNullOrWhiteSpace(container.ComposeService)
                ? GetContainerNodeKey(data, container)
                : $"service:{data.Server.Id:N}:compose:{(string.IsNullOrWhiteSpace(container.ComposeProject) ? "default" : container.ComposeProject)}/{container.ComposeService}";

        static bool IsTcpProtocol(string? protocol) => protocol is "tcp" or "http" or "https" or "grpc" or "grpcs";

        static bool IsDomainName(string value) => !string.IsNullOrWhiteSpace(value)
            && value != "_" && !value.StartsWith('~') && !value.Contains('$');

        static int? TryParseListenPort(string value)
        {
            var candidate = value.Trim();
            if (candidate.StartsWith('[') && candidate.IndexOf(']') is var closing && closing >= 0
                && closing + 2 < candidate.Length && candidate[closing + 1] == ':')
            {
                candidate = candidate[(closing + 2)..];
            }
            else if (candidate.LastIndexOf(':') is var separator && separator >= 0)
            {
                candidate = candidate[(separator + 1)..];
            }

            return int.TryParse(candidate, out var port) && port is > 0 and <= 65535 ? port : null;
        }

        static string FormatEvidence(string? filePath, int line, string snippet) =>
            $"{filePath ?? "nginx configuration"}:{line}: {snippet}";
    }

    private sealed record ServerGraphState(
        ServerDiscoveryData Data,
        GraphNode Host,
        IReadOnlyDictionary<int, GraphNode> ContainerByMainPid,
        IReadOnlyDictionary<string, GraphNode> ContainerById,
        IReadOnlyDictionary<string, GraphNode> ComposeServiceByContainerId,
        IReadOnlyDictionary<int, GraphNode> SystemdServiceByPid);

    private static IReadOnlyList<string> ParseTags(string tagsJson)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(tagsJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
