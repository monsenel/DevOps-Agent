using DevOpsAgent.Application.Discovery;

namespace DevOpsAgent.Application.Abstractions;

public interface IGraphRepository
{
    Task SaveAsync(Guid snapshotId, GraphBuildResult graph, CancellationToken cancellationToken);
    Task<GraphSnapshotDto?> GetAsync(Guid snapshotId, GraphView view, CancellationToken cancellationToken);
}
