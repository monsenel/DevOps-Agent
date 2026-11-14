using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Domain.Graph;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace DevOpsAgent.Infrastructure.Persistence;

public sealed class GraphRepository(AppDbContext dbContext) : IGraphRepository
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

    public async Task<GraphSnapshotDto?> GetAsync(Guid snapshotId, GraphView view, CancellationToken cancellationToken)
    {
        if (!await dbContext.Snapshots.AnyAsync(snapshot => snapshot.Id == snapshotId, cancellationToken))
        {
            return null;
        }

        var nodeQuery = dbContext.GraphNodes.AsNoTracking().Where(node => node.SnapshotId == snapshotId);
        nodeQuery = view == GraphView.Network
            ? nodeQuery.Where(node => node.Kind != GraphNodeKind.Service)
            : nodeQuery.Where(node => node.Kind != GraphNodeKind.Subnet);
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
}
