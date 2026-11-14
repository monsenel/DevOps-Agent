using DevOpsAgent.Domain.Discovery;

namespace DevOpsAgent.Application.Abstractions;

public interface ISnapshotRepository
{
    Task<Snapshot> CreateAsync(IReadOnlyCollection<Guid> serverIds, DateTimeOffset startedAtUtc, CancellationToken cancellationToken);
    Task CompleteAsync(
        Snapshot snapshot,
        IReadOnlyCollection<CollectorRun> collectorRuns,
        DateTimeOffset finishedAtUtc,
        int succeededServerCount,
        int failedServerCount,
        bool cancelled,
        CancellationToken cancellationToken);
}
