using DevOpsAgent.Application.Discovery;

namespace DevOpsAgent.Application.Abstractions;

public interface IGraphRepository
{
    Task SaveAsync(Guid snapshotId, GraphBuildResult graph, CancellationToken cancellationToken);
    Task MergeAsync(Guid snapshotId, Guid previousSnapshotId, GraphBuildResult graph, CancellationToken cancellationToken);
    Task<GraphSnapshotDto?> GetAsync(Guid snapshotId, GraphView view, CancellationToken cancellationToken);

    /// <summary>The stored entities of a snapshot's graph, for carrying unreachable servers forward (NFR-3).</summary>
    Task<GraphBuildResult?> GetEntitiesAsync(Guid snapshotId, CancellationToken cancellationToken);
}
