using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Domain.Graph;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace DevOpsAgent.Infrastructure.Persistence;

public sealed class GraphRepository(AppDbContext dbContext) : IGraphRepository, IGraphSearchRepository
{
    public async Task SaveAsync(Guid snapshotId, GraphBuildResult graph, CancellationToken cancellationToken)
    {
        var previousEvidence = await dbContext.GraphEvidence.Where(item => item.SnapshotId == snapshotId).ToArrayAsync(cancellationToken);
        var previousListeners = await dbContext.GraphListeners.Where(item => item.SnapshotId == snapshotId).ToArrayAsync(cancellationToken);
        var previousEdges = await dbContext.GraphEdges.Where(item => item.SnapshotId == snapshotId).ToArrayAsync(cancellationToken);
        var previousNodes = await dbContext.GraphNodes.Where(item => item.SnapshotId == snapshotId).ToArrayAsync(cancellationToken);
        dbContext.GraphEvidence.RemoveRange(previousEvidence);
        dbContext.GraphListeners.RemoveRange(previousListeners);
        dbContext.GraphEdges.RemoveRange(previousEdges);
        dbContext.GraphNodes.RemoveRange(previousNodes);
        await dbContext.SaveChangesAsync(cancellationToken);

        dbContext.GraphNodes.AddRange(graph.Nodes);
        dbContext.GraphEdges.AddRange(graph.Edges);
        dbContext.GraphListeners.AddRange(graph.Listeners);
        dbContext.GraphEvidence.AddRange(graph.Evidence);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task MergeAsync(Guid snapshotId, Guid previousSnapshotId, GraphBuildResult graph, CancellationToken cancellationToken)
    {
        var previousNodes = await dbContext.GraphNodes.AsNoTracking()
            .Where(node => node.SnapshotId == previousSnapshotId).ToArrayAsync(cancellationToken);
        var previousEdges = await dbContext.GraphEdges.AsNoTracking()
            .Where(edge => edge.SnapshotId == previousSnapshotId).ToArrayAsync(cancellationToken);
        var previousListeners = await dbContext.GraphListeners.AsNoTracking()
            .Where(listener => listener.SnapshotId == previousSnapshotId).ToArrayAsync(cancellationToken);
        var previousEvidence = await dbContext.GraphEvidence.AsNoTracking()
            .Where(item => item.SnapshotId == previousSnapshotId).ToArrayAsync(cancellationToken);

        var nodes = graph.Nodes.ToList();
        var edges = graph.Edges.ToList();
        var listeners = graph.Listeners.ToList();
        var evidence = graph.Evidence.ToList();
        var previousNodeById = previousNodes.ToDictionary(node => node.Id);
        var nodeMap = new Dictionary<Guid, GraphNode>();

        foreach (var previousNode in previousNodes.OrderBy(node => GetParentDepth(node)))
        {
            if (TryFindReplacement(previousNode, graph) is { } replacement)
            {
                nodeMap[previousNode.Id] = replacement;
                continue;
            }

            if (nodes.FirstOrDefault(node => node.Key == previousNode.Key) is { } sameKey)
            {
                nodeMap[previousNode.Id] = sameKey;
                continue;
            }

            Guid? parentId = previousNode.ParentNodeId is Guid oldParent && nodeMap.TryGetValue(oldParent, out var parent)
                ? parent.Id
                : null;
            var clone = new GraphNode(snapshotId, previousNode.Key, previousNode.Kind, previousNode.Name,
                parentId, previousNode.ServerId, previousNode.AttributesJson);
            nodes.Add(clone);
            nodeMap[previousNode.Id] = clone;
        }

        var edgeMap = new Dictionary<Guid, GraphEdge>();
        foreach (var previousEdge in previousEdges)
        {
            if (!nodeMap.TryGetValue(previousEdge.SourceNodeId, out var source)
                || !nodeMap.TryGetValue(previousEdge.TargetNodeId, out var target)) continue;

            var merged = edges.FirstOrDefault(edge => edge.SourceNodeId == source.Id && edge.TargetNodeId == target.Id
                && edge.Kind == previousEdge.Kind && edge.Port == previousEdge.Port && edge.Protocol == previousEdge.Protocol);
            if (merged is null)
            {
                merged = new GraphEdge(snapshotId, source.Id, target.Id, previousEdge.Kind, previousEdge.Port, previousEdge.Protocol);
                edges.Add(merged);
            }

            edgeMap[previousEdge.Id] = merged;
        }

        foreach (var previousListener in previousListeners)
        {
            if (!nodeMap.TryGetValue(previousListener.NodeId, out var owner)) continue;
            if (listeners.Any(listener => listener.NodeId == owner.Id && listener.Address == previousListener.Address
                && listener.Port == previousListener.Port && listener.Protocol == previousListener.Protocol
                && listener.ProcessId == previousListener.ProcessId)) continue;
            listeners.Add(new GraphListener(snapshotId, owner.Id, previousListener.Address, previousListener.Port,
                previousListener.Protocol, previousListener.ProcessName, previousListener.ProcessId));
        }

        foreach (var previousItem in previousEvidence)
        {
            var nodeId = previousItem.NodeId is Guid oldNode && nodeMap.TryGetValue(oldNode, out var mappedNode)
                ? mappedNode.Id
                : (Guid?)null;
            var edgeId = previousItem.EdgeId is Guid oldEdge && edgeMap.TryGetValue(oldEdge, out var mappedEdge)
                ? mappedEdge.Id
                : (Guid?)null;
            if (previousItem.NodeId is not null && nodeId is null || previousItem.EdgeId is not null && edgeId is null) continue;
            if (evidence.Any(item => item.NodeId == nodeId && item.EdgeId == edgeId
                && item.Collector == previousItem.Collector && item.Description == previousItem.Description)) continue;
            evidence.Add(new GraphEvidence(snapshotId, nodeId, edgeId, previousItem.Collector, previousItem.Description));
        }

        await SaveAsync(snapshotId, new GraphBuildResult(nodes, edges, listeners, evidence), cancellationToken);

        int GetParentDepth(GraphNode node)
        {
            var depth = 0;
            var parentId = node.ParentNodeId;
            var seen = new HashSet<Guid>();
            while (parentId is Guid id && seen.Add(id) && previousNodeById.TryGetValue(id, out var parent))
            {
                depth++;
                parentId = parent.ParentNodeId;
            }
            return depth;
        }
    }

    public async Task<GraphBuildResult?> GetEntitiesAsync(Guid snapshotId, CancellationToken cancellationToken)
    {
        if (!await dbContext.Snapshots.AnyAsync(snapshot => snapshot.Id == snapshotId, cancellationToken)) return null;
        return new GraphBuildResult(
            await dbContext.GraphNodes.AsNoTracking().Where(node => node.SnapshotId == snapshotId).ToArrayAsync(cancellationToken),
            await dbContext.GraphEdges.AsNoTracking().Where(edge => edge.SnapshotId == snapshotId).ToArrayAsync(cancellationToken),
            await dbContext.GraphListeners.AsNoTracking().Where(listener => listener.SnapshotId == snapshotId).ToArrayAsync(cancellationToken),
            await dbContext.GraphEvidence.AsNoTracking().Where(item => item.SnapshotId == snapshotId).ToArrayAsync(cancellationToken));
    }

    public async Task<GraphSearchSnapshot?> GetSearchDataAsync(Guid snapshotId, CancellationToken cancellationToken)
    {
        if (!await dbContext.Snapshots.AnyAsync(snapshot => snapshot.Id == snapshotId, cancellationToken)) return null;
        return new GraphSearchSnapshot(
            await dbContext.GraphNodes.AsNoTracking().Where(node => node.SnapshotId == snapshotId).ToArrayAsync(cancellationToken),
            await dbContext.GraphListeners.AsNoTracking().Where(listener => listener.SnapshotId == snapshotId).ToArrayAsync(cancellationToken));
    }

    public async Task<GraphSnapshotDto?> GetAsync(Guid snapshotId, GraphView view, CancellationToken cancellationToken)
    {
        if (!await dbContext.Snapshots.AnyAsync(snapshot => snapshot.Id == snapshotId, cancellationToken))
        {
            return null;
        }

        var nodeQuery = dbContext.GraphNodes.AsNoTracking().Where(node => node.SnapshotId == snapshotId);
        nodeQuery = view switch
        {
            GraphView.Network => nodeQuery.Where(node => node.Kind != GraphNodeKind.Service),
            GraphView.Service => nodeQuery.Where(node => node.Kind != GraphNodeKind.Subnet),
            _ => nodeQuery
        };
        var nodeRows = await nodeQuery.OrderBy(node => node.Key).ToArrayAsync(cancellationToken);
        var nodeIds = nodeRows.Select(node => node.Id).ToHashSet();
        var edgeRows = await dbContext.GraphEdges.AsNoTracking()
            .Where(edge => edge.SnapshotId == snapshotId && nodeIds.Contains(edge.SourceNodeId) && nodeIds.Contains(edge.TargetNodeId))
            .OrderBy(edge => edge.Kind).ThenBy(edge => edge.SourceNodeId)
            .ToArrayAsync(cancellationToken);
        var edgeIds = edgeRows.Select(edge => edge.Id).ToHashSet();
        var listenerRows = await dbContext.GraphListeners.AsNoTracking()
            .Where(listener => listener.SnapshotId == snapshotId && nodeIds.Contains(listener.NodeId))
            .OrderBy(listener => listener.Port).ThenBy(listener => listener.Protocol)
            .ToArrayAsync(cancellationToken);
        var evidenceRows = await dbContext.GraphEvidence.AsNoTracking()
            .Where(item => item.SnapshotId == snapshotId &&
                ((item.NodeId != null && nodeIds.Contains(item.NodeId.Value)) ||
                 (item.EdgeId != null && edgeIds.Contains(item.EdgeId.Value))))
            .ToArrayAsync(cancellationToken);
        var observedEdgeIds = evidenceRows.Where(item => item.EdgeId is not null && item.Collector == "connections")
            .Select(item => item.EdgeId!.Value).ToHashSet();

        return new GraphSnapshotDto(
            snapshotId,
            view.ToString().ToLowerInvariant(),
            nodeRows.Select(node => new GraphElementDto(JsonSerializer.SerializeToElement(new
            {
                id = node.Id.ToString("N"),
                key = node.Key,
                kind = node.Kind.ToString(),
                label = node.Name,
                parent = node.ParentNodeId?.ToString("N"),
                serverId = node.ServerId?.ToString("N"),
                attributes = JsonDocument.Parse(node.AttributesJson).RootElement.Clone()
            }))).ToArray(),
            edgeRows.Select(edge => new GraphElementDto(JsonSerializer.SerializeToElement(new
            {
                id = edge.Id.ToString("N"),
                source = edge.SourceNodeId.ToString("N"),
                target = edge.TargetNodeId.ToString("N"),
                kind = edge.Kind == GraphEdgeKind.Declared && observedEdgeIds.Contains(edge.Id) ? "Both" : edge.Kind.ToString(),
                port = edge.Port,
                protocol = edge.Protocol
            }))).ToArray(),
            listenerRows.Select(listener => new GraphListenerDto(listener.Id, listener.NodeId, listener.Address,
                listener.Port, listener.Protocol, listener.ProcessName, listener.ProcessId)).ToArray(),
            evidenceRows.Select(item => new GraphEvidenceDto(item.Id, item.NodeId, item.EdgeId,
                item.Collector, item.Description)).ToArray());
    }

    private static GraphNode? TryFindReplacement(GraphNode previousNode, GraphBuildResult incoming)
    {
        if (previousNode.Kind != GraphNodeKind.Unknown) return null;
        using var document = JsonDocument.Parse(previousNode.AttributesJson);
        var attributes = document.RootElement;
        var address = GetString(attributes, "Address");
        if (address is null) return null;
        var port = GetInt(attributes, "Port");
        var matches = incoming.Nodes.Where(node => NodeHasAddress(node, address)).ToArray();
        if (matches.Length == 0) return null;

        var matchIds = matches.Select(node => node.Id).ToHashSet();
        if (port is int targetPort)
        {
            var listenerMatches = incoming.Listeners.Where(listener => listener.Port == targetPort
                    && IsWithin(listener.NodeId, matchIds, incoming.Nodes))
                .Select(listener => incoming.Nodes.FirstOrDefault(node => node.Id == listener.NodeId))
                .Where(node => node is not null).DistinctBy(node => node!.Id).ToArray();
            if (listenerMatches.Length == 1) return listenerMatches[0];
        }

        if (matches.Length == 1)
        {
            var container = matches[0];
            if (container.Kind == GraphNodeKind.Container)
            {
                var composeServices = incoming.Nodes.Where(node => node.Kind == GraphNodeKind.Service
                    && node.ParentNodeId == container.Id).ToArray();
                if (composeServices.Length == 1) return composeServices[0];
            }
            return container;
        }

        return matches.FirstOrDefault(node => node.Kind == GraphNodeKind.Host);
    }

    private static bool NodeHasAddress(GraphNode node, string address)
    {
        using var document = JsonDocument.Parse(node.AttributesJson);
        var attributes = document.RootElement;
        if (string.Equals(GetString(attributes, "Host"), address, StringComparison.OrdinalIgnoreCase)) return true;
        if (node.Kind == GraphNodeKind.Host && TryGetProperty(attributes, "Addresses", out var addresses)
            && addresses.ValueKind == JsonValueKind.Array)
        {
            return addresses.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String
                && string.Equals(value.GetString(), address, StringComparison.OrdinalIgnoreCase));
        }

        if (node.Kind == GraphNodeKind.Container && TryGetProperty(attributes, "Networks", out var networks)
            && networks.ValueKind == JsonValueKind.Array)
        {
            return networks.EnumerateArray().Any(network =>
                string.Equals(GetString(network, "IPv4Address")?.Split('/')[0], address, StringComparison.OrdinalIgnoreCase)
                || string.Equals(GetString(network, "IPv6Address")?.Split('/')[0], address, StringComparison.OrdinalIgnoreCase));
        }

        return false;
    }

    private static bool IsWithin(Guid nodeId, IReadOnlySet<Guid> roots, IReadOnlyCollection<GraphNode> nodes)
    {
        var byId = nodes.ToDictionary(node => node.Id);
        var current = byId.GetValueOrDefault(nodeId);
        while (current is not null)
        {
            if (roots.Contains(current.Id)) return true;
            current = current.ParentNodeId is Guid parent ? byId.GetValueOrDefault(parent) : null;
        }
        return false;
    }

    private static string? GetString(JsonElement element, string property) =>
        TryGetProperty(element, property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? GetInt(JsonElement element, string property) =>
        TryGetProperty(element, property, out var value) && value.TryGetInt32(out var parsed) ? parsed : null;

    private static bool TryGetProperty(JsonElement element, string property, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var candidate in element.EnumerateObject())
            {
                if (string.Equals(candidate.Name, property, StringComparison.OrdinalIgnoreCase))
                {
                    value = candidate.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }
}
