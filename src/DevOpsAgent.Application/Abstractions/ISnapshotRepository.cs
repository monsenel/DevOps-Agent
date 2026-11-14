using DevOpsAgent.Domain.Discovery;

namespace DevOpsAgent.Application.Abstractions;

public interface ISnapshotRepository
{
    Task<Snapshot?> GetLatestAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<Snapshot>> ListAsync(CancellationToken cancellationToken);
    Task<Snapshot> CreateAsync(IReadOnlyCollection<Guid> serverIds, DateTimeOffset startedAtUtc, CancellationToken cancellationToken);
    Task CompleteAsync(
        Snapshot snapshot,
        IReadOnlyCollection<CollectorRun> collectorRuns,
        DateTimeOffset finishedAtUtc,
        int succeededServerCount,
        int failedServerCount,
        bool cancelled,
        CancellationToken cancellationToken);

    /// <summary>Marks a snapshot that hit an unexpected error as Failed so it never stays Running.</summary>
    Task FailAsync(
        Snapshot snapshot,
        IReadOnlyCollection<CollectorRun> collectorRuns,
        DateTimeOffset finishedAtUtc,
        int succeededServerCount,
        int failedServerCount,
        CancellationToken cancellationToken);
}
