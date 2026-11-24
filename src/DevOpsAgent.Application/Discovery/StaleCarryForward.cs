using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using DevOpsAgent.Domain.Graph;

namespace DevOpsAgent.Application.Discovery;

public sealed record StaleContainerSeed(
    Guid ServerId,
    string ContainerNodeKey,
    string TargetNodeKey,
    string Name,
    IReadOnlyList<string> Addresses,
    IReadOnlyList<DockerPublishedPort> PublishedPorts);

public sealed record StaleListenerSeed(
    Guid ServerId,
    string NodeKey,
    string Address,
    int Port,
    string Protocol,
    string? ProcessName);

public sealed record StaleSeedData(
    IReadOnlySet<Guid> StaleServerIds,
    IReadOnlyDictionary<Guid, IReadOnlyList<string>> HostAddresses,
    IReadOnlyDictionary<Guid, IReadOnlyList<StaleContainerSeed>> Containers,
    IReadOnlyDictionary<Guid, IReadOnlyList<StaleListenerSeed>> Listeners,
    IReadOnlyDictionary<string, GraphNode> NodesByKey);

/// <summary>
/// NFR-3 / T7.R16: a server that was unreachable during discovery keeps its nodes, listeners and stale-to-stale edges
/// from the last usable snapshot, flagged stale with the time its host collector last ran, instead of vanishing from the graph.
/// </summary>
public static class StaleCarryForward
{
    public const string StaleAttribute = "Stale";
    public const string LastSeenAttribute = "LastSeenUtc";
    public const string StaleEvidenceCollector = "stale";
    public const string StaleEdgeBadge = "stale · carried forward";
    public const string StaleEdgeEvidenceDescription = "Carried forward from the last reachable discovery; endpoint server was unreachable.";

    /// <summary>
    /// Extracts last-known host addresses, container IPs/published ports, and listeners for unreachable servers from the
    /// previous snapshot so reachable servers resolve fresh edges against them instead of creating Unknown nodes (T7.R16a).
    /// </summary>
    public static StaleSeedData ExtractStaleSeed(GraphBuildResult previous, IReadOnlySet<Guid> staleServerIds)
    {
        var hostAddresses = new Dictionary<Guid, IReadOnlyList<string>>();
        var containers = new Dictionary<Guid, IReadOnlyList<StaleContainerSeed>>();
        var listeners = new Dictionary<Guid, IReadOnlyList<StaleListenerSeed>>();
        var nodesByKey = new Dictionary<string, GraphNode>(StringComparer.Ordinal);
        if (staleServerIds.Count == 0)
        {
            return new StaleSeedData(staleServerIds, hostAddresses, containers, listeners, nodesByKey);
        }

        var staleNodes = previous.Nodes.Where(node => node.ServerId is Guid id && staleServerIds.Contains(id)).ToArray();
        var staleNodeById = staleNodes.ToDictionary(node => node.Id);
        foreach (var node in staleNodes)
        {
            nodesByKey.TryAdd(node.Key, node);
        }

        var composeChildByContainerId = staleNodes
            .Where(node => node.Kind == GraphNodeKind.Service && node.ParentNodeId is not null && node.Key.Contains(":compose:", StringComparison.Ordinal))
            .GroupBy(node => node.ParentNodeId!.Value)
            .ToDictionary(group => group.Key, group => group.First());

        foreach (var serverGroup in staleNodes.GroupBy(node => node.ServerId!.Value))
        {
            var serverId = serverGroup.Key;
            var ipSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var containerList = new List<StaleContainerSeed>();

            foreach (var node in serverGroup)
            {
                using var document = ParseObject(node.AttributesJson);
                if (document is null) continue;
                var attrs = document.RootElement;

                if (node.Kind == GraphNodeKind.Host)
                {
                    if (TryReadString(attrs, "Host") is { } hostVal && IPAddress.TryParse(hostVal, out _))
                    {
                        ipSet.Add(hostVal);
                    }

                    if (TryGetProp(attrs, "Addresses", out var addressesEl) && addressesEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in addressesEl.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                            {
                                ipSet.Add(item.GetString()!);
                            }
                        }
                    }
                }
                else if (node.Kind == GraphNodeKind.Container)
                {
                    var containerIps = new List<string>();
                    if (TryGetProp(attrs, "Networks", out var networksEl) && networksEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var net in networksEl.EnumerateArray())
                        {
                            if (TryReadString(net, "IPv4Address") is { Length: > 0 } ipv4) containerIps.Add(StripPrefix(ipv4));
                            if (TryReadString(net, "IPv6Address") is { Length: > 0 } ipv6) containerIps.Add(StripPrefix(ipv6));
                        }
                    }

                    var publishedPorts = new List<DockerPublishedPort>();
                    if (TryGetProp(attrs, "PublishedPorts", out var portsEl) && portsEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var portEl in portsEl.EnumerateArray())
                        {
                            var containerPort = TryReadInt(portEl, "ContainerPort");
                            var hostPort = TryReadInt(portEl, "HostPort");
                            var protocol = TryReadString(portEl, "Protocol") ?? "tcp";
                            var hostIp = TryReadString(portEl, "HostIp");
                            if (containerPort is int cp && hostPort is int hp)
                            {
                                publishedPorts.Add(new DockerPublishedPort(cp, protocol, hostIp, hp));
                            }
                        }
                    }

                    var targetKey = composeChildByContainerId.TryGetValue(node.Id, out var composeNode)
                        ? composeNode.Key
                        : node.Key;
                    containerList.Add(new StaleContainerSeed(
                        serverId,
                        node.Key,
                        targetKey,
                        node.Name,
                        containerIps.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                        publishedPorts));
                }
            }

            if (ipSet.Count > 0) hostAddresses[serverId] = ipSet.ToArray();
            if (containerList.Count > 0) containers[serverId] = containerList;
        }

        foreach (var group in previous.Listeners
                     .Where(listener => staleNodeById.ContainsKey(listener.NodeId))
                     .GroupBy(listener => staleNodeById[listener.NodeId].ServerId!.Value))
        {
            listeners[group.Key] = group.Select(listener =>
                new StaleListenerSeed(
                    group.Key,
                    staleNodeById[listener.NodeId].Key,
                    listener.Address,
                    listener.Port,
                    listener.Protocol,
                    listener.ProcessName)).ToArray();
        }

        return new StaleSeedData(staleServerIds, hostAddresses, containers, listeners, nodesByKey);
    }

    /// <param name="fresh">Graph built from this run's reachable servers (unreachable servers contribute only a bare host).</param>
    /// <param name="previous">Entities of the last usable snapshot, or null when there is none.</param>
    /// <param name="previousSeenAtUtc">Fallback "last seen" time when per-server host collector timestamps are unavailable.</param>
    public static GraphBuildResult Apply(Guid snapshotId, GraphBuildResult fresh, GraphBuildResult? previous,
        IReadOnlySet<Guid> staleServerIds, DateTimeOffset? previousSeenAtUtc) =>
        Apply(snapshotId, fresh, previous, staleServerIds, lastSeenByServer: null, fallbackSeenAtUtc: previousSeenAtUtc);

    /// <summary>
    /// Applies stale carry-forward using per-server host collector timestamps (T7.R16b) and carrying only stale-to-stale
    /// (non-fresh) edges marked stale with evidence (T7.R16a, T7.R16d).
    /// </summary>
    public static GraphBuildResult Apply(
        Guid snapshotId,
        GraphBuildResult fresh,
        GraphBuildResult? previous,
        IReadOnlySet<Guid> staleServerIds,
        IReadOnlyDictionary<Guid, DateTimeOffset>? lastSeenByServer,
        DateTimeOffset? fallbackSeenAtUtc = null)
    {
        if (staleServerIds.Count == 0) return fresh;

        var carriedServers = previous is null
            ? new HashSet<Guid>()
            : previous.Nodes.Where(node => node.ServerId is Guid id && staleServerIds.Contains(id)).Select(node => node.ServerId!.Value).ToHashSet();

        // A stale server with no history keeps its bare host node from this run, marked stale with no "last seen".
        var nodes = fresh.Nodes.Select(node => node.ServerId is Guid id && staleServerIds.Contains(id) && !carriedServers.Contains(id)
            ? Clone(node, snapshotId, node.ParentNodeId, MarkStale(node.AttributesJson, lastSeenUtc: null))
            : node).ToList();
        var freshRemap = fresh.Nodes.Zip(nodes).Where(pair => !ReferenceEquals(pair.First, pair.Second))
            .ToDictionary(pair => pair.First.Id, pair => pair.Second.Id);
        var edgeIds = new Dictionary<Guid, Guid>();
        var edges = fresh.Edges.Select(edge => Remap(edge, freshRemap, snapshotId, edgeIds)).ToList();
        var listeners = fresh.Listeners.Select(listener => freshRemap.TryGetValue(listener.NodeId, out var nodeId)
            ? new GraphListener(snapshotId, nodeId, listener.Address, listener.Port, listener.Protocol, listener.ProcessName, listener.ProcessId)
            : listener).ToList();
        var evidence = fresh.Evidence.Select(item => RemapEvidence(item, freshRemap, edgeIds, snapshotId)).ToList();
        if (previous is null || carriedServers.Count == 0) return new GraphBuildResult(nodes, edges, listeners, evidence);

        // Replace this run's partial view (bare host or seeded stale stubs) of each stale server with its last known state.
        var replaced = nodes.Where(node => node.ServerId is Guid id && carriedServers.Contains(id)).ToList();
        var replacedKeys = replaced.ToDictionary(node => node.Id, node => node.Key);
        nodes.RemoveAll(node => replacedKeys.ContainsKey(node.Id));

        var nodeByKey = nodes.ToDictionary(node => node.Key, StringComparer.Ordinal);
        var previousById = previous.Nodes.ToDictionary(node => node.Id);
        var carried = new Dictionary<Guid, GraphNode>();

        DateTimeOffset? ResolveLastSeen(Guid serverId) =>
            lastSeenByServer is not null && lastSeenByServer.TryGetValue(serverId, out var seen)
                ? seen
                : fallbackSeenAtUtc;

        foreach (var old in previous.Nodes.Where(node => node.ServerId is Guid id && carriedServers.Contains(id))
                     .OrderBy(node => Depth(node, previousById)))
        {
            if (nodeByKey.TryGetValue(old.Key, out var existing))
            {
                // Another reachable server already reported this node (e.g. a shared Unknown endpoint).
                carried[old.Id] = existing;
                continue;
            }

            var parentId = old.ParentNodeId is Guid oldParent
                ? carried.TryGetValue(oldParent, out var parent) ? parent.Id
                : previousById.TryGetValue(oldParent, out var oldParentNode) && nodeByKey.TryGetValue(oldParentNode.Key, out var freshParent) ? freshParent.Id
                : (Guid?)null
                : null;
            var clone = Clone(old, snapshotId, parentId, MarkStale(old.AttributesJson, ResolveLastSeen(old.ServerId!.Value)));
            nodes.Add(clone);
            nodeByKey[clone.Key] = clone;
            carried[old.Id] = clone;
        }

        // Fresh edges that pointed at the stale servers' bare or seeded nodes now point at the carried nodes with the same key.
        var replacedToCarried = replacedKeys.Where(pair => nodeByKey.ContainsKey(pair.Value))
            .ToDictionary(pair => pair.Key, pair => nodeByKey[pair.Value].Id);
        edgeIds.Clear();
        edges = edges.Select(edge => Remap(edge, replacedToCarried, snapshotId, edgeIds))
            .Where(edge => !replacedKeys.ContainsKey(edge.SourceNodeId) && !replacedKeys.ContainsKey(edge.TargetNodeId)).ToList();
        listeners.RemoveAll(listener => replacedKeys.ContainsKey(listener.NodeId));
        evidence.RemoveAll(item => item.NodeId is Guid nodeId && replacedKeys.ContainsKey(nodeId));
        evidence = evidence.Select(item => RemapEvidence(item, new Dictionary<Guid, Guid>(), edgeIds, snapshotId)).ToList();
        var liveEdgeIds = edges.Select(edge => edge.Id).ToHashSet();
        evidence.RemoveAll(item => item.EdgeId is Guid edgeId && !liveEdgeIds.Contains(edgeId));

        bool IsFreshServerNode(GraphNode node) =>
            node.ServerId is Guid id && !staleServerIds.Contains(id);

        GraphNode? ResolveOrCarryUnowned(Guid previousNodeId)
        {
            if (carried.TryGetValue(previousNodeId, out var node)) return node;
            if (!previousById.TryGetValue(previousNodeId, out var old)) return null;
            if (nodeByKey.TryGetValue(old.Key, out var current)) return current;
            if (old.ServerId is null && old.Kind is GraphNodeKind.Subnet or GraphNodeKind.Domain)
            {
                // Keep shared unowned Subnet/Domain nodes referenced solely by carried stale nodes.
                var clone = Clone(old, snapshotId, null, old.AttributesJson);
                nodes.Add(clone);
                nodeByKey[clone.Key] = clone;
                return clone;
            }

            return null;
        }

        // T7.R16a: carry ONLY stale-to-stale edges (including stale ↔ unowned Subnet/Domain).
        // Fresh-to-stale (and stale-to-fresh) edges are NOT carried: reachable servers must report their live edges against
        // the seeded IP index so removed proxy_pass directives are immediately visible instead of hidden until recovery.
        var carriedEdges = new Dictionary<Guid, GraphEdge>();
        var newlyCarriedEdgeIds = new HashSet<Guid>();
        foreach (var old in previous.Edges)
        {
            if (!carried.ContainsKey(old.SourceNodeId) && !carried.ContainsKey(old.TargetNodeId)) continue;
            if (!previousById.TryGetValue(old.SourceNodeId, out var oldSource)
                || !previousById.TryGetValue(old.TargetNodeId, out var oldTarget)) continue;
            if (IsFreshServerNode(oldSource) || IsFreshServerNode(oldTarget)) continue;

            if (ResolveOrCarryUnowned(old.SourceNodeId) is not { } source
                || ResolveOrCarryUnowned(old.TargetNodeId) is not { } target) continue;

            var existing = edges.FirstOrDefault(edge => edge.SourceNodeId == source.Id && edge.TargetNodeId == target.Id
                && edge.Kind == old.Kind && edge.Port == old.Port && edge.Protocol == old.Protocol);
            if (existing is null)
            {
                existing = new GraphEdge(snapshotId, source.Id, target.Id, old.Kind, old.Port, old.Protocol);
                edges.Add(existing);
                newlyCarriedEdgeIds.Add(existing.Id);
            }

            carriedEdges[old.Id] = existing;
        }

        foreach (var old in previous.Listeners.Where(listener => carried.ContainsKey(listener.NodeId)))
        {
            var owner = carried[old.NodeId];
            if (listeners.Any(listener => listener.NodeId == owner.Id && listener.Address == old.Address && listener.Port == old.Port
                && listener.Protocol == old.Protocol)) continue;
            listeners.Add(new GraphListener(snapshotId, owner.Id, old.Address, old.Port, old.Protocol, old.ProcessName, old.ProcessId));
        }

        foreach (var old in previous.Evidence)
        {
            Guid? nodeId = old.NodeId is Guid oldNode ? carried.GetValueOrDefault(oldNode)?.Id : null;
            Guid? edgeId = old.EdgeId is Guid oldEdge ? carriedEdges.GetValueOrDefault(oldEdge)?.Id : null;
            if (nodeId is null && edgeId is null) continue;
            if (old.NodeId is not null && nodeId is null || old.EdgeId is not null && edgeId is null) continue;
            evidence.Add(new GraphEvidence(snapshotId, nodeId, edgeId, old.Collector, old.Description));
        }

        // T7.R16d: mark every carried edge stale via evidence so graph DTOs style it dashed and detail panels explain it in text.
        foreach (var carriedEdgeId in newlyCarriedEdgeIds)
        {
            if (!evidence.Any(item => item.EdgeId == carriedEdgeId && item.Collector == StaleEvidenceCollector))
            {
                evidence.Add(new GraphEvidence(snapshotId, null, carriedEdgeId, StaleEvidenceCollector, StaleEdgeEvidenceDescription));
            }
        }

        return new GraphBuildResult(nodes, edges, listeners, evidence);
    }

    /// <summary>True when node attributes are equal apart from the stale markers (drift ignores staleness).</summary>
    public static bool SameIgnoringStale(string attributesBefore, string attributesAfter) =>
        WithoutStale(attributesBefore) == WithoutStale(attributesAfter);

    public static bool IsStale(JsonElement attributes) =>
        attributes.ValueKind == JsonValueKind.Object && attributes.TryGetProperty(StaleAttribute, out var stale) && stale.ValueKind == JsonValueKind.True;

    /// <summary>"stale · last seen 2026-11-15 10:21 UTC" (or "never discovered"), for the badge and node labels.</summary>
    public static string? Badge(JsonElement attributes)
    {
        if (!IsStale(attributes)) return null;
        return attributes.TryGetProperty(LastSeenAttribute, out var seen) && seen.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(seen.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var lastSeen)
            ? $"stale · last seen {lastSeen.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)}"
            : "stale · never discovered";
    }

    private static string WithoutStale(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject value) return json;
        value.Remove(StaleAttribute);
        value.Remove(LastSeenAttribute);
        return value.ToJsonString();
    }

    private static string MarkStale(string attributesJson, DateTimeOffset? lastSeenUtc)
    {
        var value = JsonNode.Parse(attributesJson) as JsonObject ?? new JsonObject();
        // A node that was already stale keeps the time it was really last seen.
        var alreadyStale = value[StaleAttribute] is JsonValue flag && flag.TryGetValue<bool>(out var stale) && stale;
        value[StaleAttribute] = true;
        if (!alreadyStale)
        {
            value.Remove(LastSeenAttribute);
            if (lastSeenUtc is { } seen) value[LastSeenAttribute] = seen.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        }

        return value.ToJsonString();
    }

    private static GraphNode Clone(GraphNode node, Guid snapshotId, Guid? parentId, string attributesJson) =>
        new(snapshotId, node.Key, node.Kind, node.Name, parentId, node.ServerId, attributesJson);

    // Entities are immutable, so a re-pointed edge is a new edge; edgeIds records old → new so evidence can follow.
    private static GraphEdge Remap(GraphEdge edge, IReadOnlyDictionary<Guid, Guid> nodeMap, Guid snapshotId, IDictionary<Guid, Guid> edgeIds)
    {
        var source = nodeMap.GetValueOrDefault(edge.SourceNodeId, edge.SourceNodeId);
        var target = nodeMap.GetValueOrDefault(edge.TargetNodeId, edge.TargetNodeId);
        if (source == edge.SourceNodeId && target == edge.TargetNodeId) return edge;
        var remapped = new GraphEdge(snapshotId, source, target, edge.Kind, edge.Port, edge.Protocol);
        edgeIds[edge.Id] = remapped.Id;
        return remapped;
    }

    private static GraphEvidence RemapEvidence(GraphEvidence item, IReadOnlyDictionary<Guid, Guid> nodeMap,
        IReadOnlyDictionary<Guid, Guid> edgeIds, Guid snapshotId)
    {
        var nodeId = item.NodeId is Guid node ? nodeMap.GetValueOrDefault(node, node) : (Guid?)null;
        var edgeId = item.EdgeId is Guid edge ? edgeIds.GetValueOrDefault(edge, edge) : (Guid?)null;
        return nodeId == item.NodeId && edgeId == item.EdgeId
            ? item
            : new GraphEvidence(snapshotId, nodeId, edgeId, item.Collector, item.Description);
    }

    private static int Depth(GraphNode node, IReadOnlyDictionary<Guid, GraphNode> byId)
    {
        var depth = 0;
        var seen = new HashSet<Guid>();
        for (var parent = node.ParentNodeId; parent is Guid id && seen.Add(id) && byId.TryGetValue(id, out var next); parent = next.ParentNodeId)
            depth++;
        return depth;
    }

    private static JsonDocument? ParseObject(string json)
    {
        try
        {
            var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object) return document;
            document.Dispose();
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGetProp(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = prop.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string? TryReadString(JsonElement element, string name) =>
        TryGetProp(element, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? TryReadInt(JsonElement element, string name) =>
        TryGetProp(element, name, out var value) && value.TryGetInt32(out var parsed) ? parsed : null;

    private static string StripPrefix(string address)
    {
        var slash = address.IndexOf('/');
        return slash < 0 ? address : address[..slash];
    }
}
