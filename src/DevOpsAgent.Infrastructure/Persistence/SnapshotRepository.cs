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
}
