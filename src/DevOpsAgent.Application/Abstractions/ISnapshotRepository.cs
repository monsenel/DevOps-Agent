using DevOpsAgent.Domain.Discovery;

namespace DevOpsAgent.Application.Abstractions;

public interface ISnapshotRepository
{
    /// <summary>The newest snapshot that carries a usable graph (Completed or Partial); the merge base and drift/topology source.</summary>
    Task<Snapshot?> GetLatestAsync(CancellationToken cancellationToken);

    /// <summary>The newest finished run of any status (including Failed and Cancelled); used to schedule the next attempt.</summary>
    Task<Snapshot?> GetLatestRunAsync(CancellationToken cancellationToken);

    /// <summary>Finished snapshots that carry a usable graph (Completed or Partial).</summary>
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
