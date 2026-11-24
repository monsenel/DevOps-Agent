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

    /// <summary>Collector runs recorded for a specific snapshot.</summary>
    Task<IReadOnlyList<CollectorRun>> ListCollectorRunsAsync(Guid snapshotId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CollectorRun>>([]);

    /// <summary>
    /// The collection time of the most recent succeeded <c>host</c> collector run (falling back to any succeeded collector run)
    /// for each requested server in a usable snapshot, used as the real "last seen" timestamp when carrying stale nodes forward (T7.R16).
    /// </summary>
    Task<IReadOnlyDictionary<Guid, DateTimeOffset>> GetLastHostCollectionTimesAsync(
        IReadOnlyCollection<Guid> serverIds,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, DateTimeOffset>>(new Dictionary<Guid, DateTimeOffset>());

    /// <summary>
    /// Latest recorded status of the named collector for each requested server in usable snapshots started at or before
    /// <paramref name="atOrBeforeUtc"/> (used to determine listener eligibility across merge snapshots, T7.R16).
    /// </summary>
    Task<IReadOnlyDictionary<Guid, CollectorRunStatus>> GetLatestCollectorStatusAtOrBeforeAsync(
        string collectorName,
        DateTimeOffset atOrBeforeUtc,
        IReadOnlyCollection<Guid> serverIds,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, CollectorRunStatus>>(new Dictionary<Guid, CollectorRunStatus>());
}
