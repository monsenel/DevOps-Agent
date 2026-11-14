using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Domain.Discovery;

namespace DevOpsAgent.Infrastructure.Persistence;

public sealed class SnapshotRepository(AppDbContext dbContext) : ISnapshotRepository
{
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
}
