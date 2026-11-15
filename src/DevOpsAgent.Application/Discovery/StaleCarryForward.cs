using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DevOpsAgent.Domain.Graph;

namespace DevOpsAgent.Application.Discovery;

/// <summary>
/// NFR-3: a server that was unreachable during discovery keeps its nodes, listeners and edges from the last usable snapshot,
/// flagged stale with the time they were last seen, instead of vanishing from the graph (and showing up as drift).
/// </summary>
public static class StaleCarryForward
{
    public const string StaleAttribute = "Stale";
    public const string LastSeenAttribute = "LastSeenUtc";

    /// <param name="fresh">Graph built from this run's reachable servers (unreachable servers contribute only a bare host).</param>
    /// <param name="previous">Entities of the last usable snapshot, or null when there is none.</param>
    /// <param name="previousSeenAtUtc">When the previous snapshot was taken: the "last seen" time of nodes that were live in it.</param>
    public static GraphBuildResult Apply(Guid snapshotId, GraphBuildResult fresh, GraphBuildResult? previous,
        IReadOnlySet<Guid> staleServerIds, DateTimeOffset? previousSeenAtUtc)
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

        // Replace this run's partial view of each stale server with its last known state.
        var replaced = nodes.Where(node => node.ServerId is Guid id && carriedServers.Contains(id)).ToList();
        var replacedKeys = replaced.ToDictionary(node => node.Id, node => node.Key);
        nodes.RemoveAll(node => replacedKeys.ContainsKey(node.Id));

        var nodeByKey = nodes.ToDictionary(node => node.Key, StringComparer.Ordinal);
        var previousById = previous.Nodes.ToDictionary(node => node.Id);
        var carried = new Dictionary<Guid, GraphNode>();
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
            var clone = Clone(old, snapshotId, parentId, MarkStale(old.AttributesJson, previousSeenAtUtc));
            nodes.Add(clone);
            nodeByKey[clone.Key] = clone;
            carried[old.Id] = clone;
        }

        // Fresh edges that pointed at the stale servers' bare nodes now point at the carried nodes with the same key.
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

        GraphNode? Resolve(Guid previousNodeId) => carried.TryGetValue(previousNodeId, out var node) ? node
            : previousById.TryGetValue(previousNodeId, out var old) && nodeByKey.TryGetValue(old.Key, out var current) ? current
            : null;

        var carriedEdges = new Dictionary<Guid, GraphEdge>();
        foreach (var old in previous.Edges.Where(edge => carried.ContainsKey(edge.SourceNodeId) || carried.ContainsKey(edge.TargetNodeId)))
        {
            if (Resolve(old.SourceNodeId) is not { } source || Resolve(old.TargetNodeId) is not { } target) continue;
            var existing = edges.FirstOrDefault(edge => edge.SourceNodeId == source.Id && edge.TargetNodeId == target.Id
                && edge.Kind == old.Kind && edge.Port == old.Port && edge.Protocol == old.Protocol);
            if (existing is null)
            {
                existing = new GraphEdge(snapshotId, source.Id, target.Id, old.Kind, old.Port, old.Protocol);
                edges.Add(existing);
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
}
