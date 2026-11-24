using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Domain.Discovery;
using Microsoft.EntityFrameworkCore;

namespace DevOpsAgent.Infrastructure.Persistence;

public sealed class SnapshotRepository(AppDbContext dbContext) : ISnapshotRepository
{
    // Failed and Cancelled runs have no trustworthy graph, so they are never a merge base or drift/topology source.
    public async Task<Snapshot?> GetLatestAsync(CancellationToken cancellationToken)
    {
        var snapshots = await GraphSnapshots().ToArrayAsync(cancellationToken);
        return snapshots.MaxBy(snapshot => snapshot.StartedAtUtc);
    }

    public async Task<Snapshot?> GetLatestRunAsync(CancellationToken cancellationToken)
    {
        var snapshots = await dbContext.Snapshots.AsNoTracking()
            .Where(snapshot => snapshot.FinishedAtUtc != null)
            .ToArrayAsync(cancellationToken);
        return snapshots.MaxBy(snapshot => snapshot.StartedAtUtc);
    }

    public async Task<IReadOnlyList<Snapshot>> ListAsync(CancellationToken cancellationToken)
    {
        var items = await GraphSnapshots().ToArrayAsync(cancellationToken);
        return items.OrderByDescending(snapshot => snapshot.StartedAtUtc).ToArray();
    }

    private IQueryable<Snapshot> GraphSnapshots() => dbContext.Snapshots.AsNoTracking()
        .Where(snapshot => snapshot.FinishedAtUtc != null
            && (snapshot.Status == SnapshotStatus.Completed || snapshot.Status == SnapshotStatus.Partial));

    public async Task<Snapshot> CreateAsync(
        IReadOnlyCollection<Guid> serverIds,
        DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken)
    {
        var snapshot = new Snapshot(serverIds, startedAtUtc);
        dbContext.Snapshots.Add(snapshot);
        await dbContext.SaveChangesAsync(cancellationToken);
        return snapshot;
    }

    public async Task CompleteAsync(
        Snapshot snapshot,
        IReadOnlyCollection<CollectorRun> collectorRuns,
        DateTimeOffset finishedAtUtc,
        int succeededServerCount,
        int failedServerCount,
        bool cancelled,
        CancellationToken cancellationToken)
    {
        if (cancelled)
        {
            snapshot.Cancel(finishedAtUtc, succeededServerCount, failedServerCount);
        }
        else
        {
            snapshot.Complete(finishedAtUtc, succeededServerCount, failedServerCount);
        }

        dbContext.CollectorRuns.AddRange(collectorRuns);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task FailAsync(
        Snapshot snapshot,
        IReadOnlyCollection<CollectorRun> collectorRuns,
        DateTimeOffset finishedAtUtc,
        int succeededServerCount,
        int failedServerCount,
        CancellationToken cancellationToken)
    {
        // The failure may have come from a half-applied SaveChanges; drop whatever the tracker still holds and
        // persist only the snapshot outcome and the collector runs gathered so far.
        dbContext.ChangeTracker.Clear();
        snapshot.Fail(finishedAtUtc, succeededServerCount, failedServerCount);
        dbContext.Snapshots.Update(snapshot);
        dbContext.CollectorRuns.AddRange(collectorRuns);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CollectorRun>> ListCollectorRunsAsync(Guid snapshotId, CancellationToken cancellationToken) =>
        await dbContext.CollectorRuns.AsNoTracking()
            .Where(run => run.SnapshotId == snapshotId)
            .ToArrayAsync(cancellationToken);

    public async Task<IReadOnlyList<CollectorRun>> ListLatestServerCollectorRunsAsync(Guid serverId, CancellationToken cancellationToken)
    {
        var usableSnapshotIds = await GraphSnapshots().Select(snapshot => snapshot.Id).ToArrayAsync(cancellationToken);
        if (usableSnapshotIds.Length == 0)
        {
            return [];
        }

        var runs = await dbContext.CollectorRuns.AsNoTracking()
            .Where(run => run.ServerId == serverId
                && usableSnapshotIds.Contains(run.SnapshotId)
                && run.Status == CollectorRunStatus.Succeeded
                && run.ResultJson != null)
            .ToArrayAsync(cancellationToken);

        return runs
            .GroupBy(run => run.CollectorName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(run => run.CreatedAtUtc).First())
            .ToArray();
    }

    public async Task<IReadOnlyDictionary<Guid, DateTimeOffset>> GetLastHostCollectionTimesAsync(
        IReadOnlyCollection<Guid> serverIds,
        CancellationToken cancellationToken)
    {
        if (serverIds.Count == 0)
        {
            return new Dictionary<Guid, DateTimeOffset>();
        }

        var usableSnapshotIds = await GraphSnapshots().Select(snapshot => snapshot.Id).ToArrayAsync(cancellationToken);
        if (usableSnapshotIds.Length == 0)
        {
            return new Dictionary<Guid, DateTimeOffset>();
        }

        var runs = await dbContext.CollectorRuns.AsNoTracking()
            .Where(run => serverIds.Contains(run.ServerId)
                && usableSnapshotIds.Contains(run.SnapshotId)
                && run.Status == CollectorRunStatus.Succeeded)
            .Select(run => new { run.ServerId, run.CollectorName, run.CreatedAtUtc })
            .ToArrayAsync(cancellationToken);

        var result = new Dictionary<Guid, DateTimeOffset>();
        foreach (var group in runs.GroupBy(run => run.ServerId))
        {
            var hostRun = group.Where(run => string.Equals(run.CollectorName, "host", StringComparison.Ordinal))
                .MaxBy(run => run.CreatedAtUtc);
            var chosen = hostRun ?? group.MaxBy(run => run.CreatedAtUtc);
            if (chosen is not null)
            {
                result[group.Key] = chosen.CreatedAtUtc;
            }
        }

        return result;
    }

    public async Task<IReadOnlyDictionary<Guid, CollectorRunStatus>> GetLatestCollectorStatusAtOrBeforeAsync(
        string collectorName,
        DateTimeOffset atOrBeforeUtc,
        IReadOnlyCollection<Guid> serverIds,
        CancellationToken cancellationToken)
    {
        if (serverIds.Count == 0)
        {
            return new Dictionary<Guid, CollectorRunStatus>();
        }

        var snapshots = await GraphSnapshots().ToArrayAsync(cancellationToken);
        var eligibleSnapshotIds = snapshots
            .Where(snapshot => snapshot.StartedAtUtc <= atOrBeforeUtc)
            .Select(snapshot => snapshot.Id)
            .ToArray();
        if (eligibleSnapshotIds.Length == 0)
        {
            return new Dictionary<Guid, CollectorRunStatus>();
        }

        var runs = await dbContext.CollectorRuns.AsNoTracking()
            .Where(run => serverIds.Contains(run.ServerId)
                && eligibleSnapshotIds.Contains(run.SnapshotId)
                && run.CollectorName == collectorName)
            .Select(run => new { run.ServerId, run.Status, run.CreatedAtUtc })
            .ToArrayAsync(cancellationToken);

        return runs.GroupBy(run => run.ServerId)
            .ToDictionary(group => group.Key, group => group.MaxBy(run => run.CreatedAtUtc)!.Status);
    }
}
