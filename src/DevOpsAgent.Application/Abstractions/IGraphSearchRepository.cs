using DevOpsAgent.Domain.Graph;

namespace DevOpsAgent.Application.Abstractions;

/// <summary>Lightweight snapshot projection for global search without loading edges or evidence (T7.R30).</summary>
public sealed record GraphSearchSnapshot(
    IReadOnlyList<GraphNode> Nodes,
    IReadOnlyList<GraphListener> Listeners);

/// <summary>Optional fast-path read implemented by graph repositories so global search avoids loading edges and evidence.</summary>
public interface IGraphSearchRepository
{
    Task<GraphSearchSnapshot?> GetSearchDataAsync(Guid snapshotId, CancellationToken cancellationToken);
}
