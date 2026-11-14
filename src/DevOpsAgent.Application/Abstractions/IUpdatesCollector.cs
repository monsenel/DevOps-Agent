using DevOpsAgent.Application.Updates;

namespace DevOpsAgent.Application.Abstractions;

public interface IUpdatesCollector
{
    Task<CollectedUpdateResult> CollectAsync(Guid serverId, CancellationToken cancellationToken);
}
