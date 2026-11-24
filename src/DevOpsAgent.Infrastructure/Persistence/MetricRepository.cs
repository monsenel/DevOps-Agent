using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Domain.Monitoring;
using Microsoft.EntityFrameworkCore;

namespace DevOpsAgent.Infrastructure.Persistence;

public sealed class MetricRepository(AppDbContext dbContext) : IMetricRepository
{
    public async Task AddSamplesAsync(IReadOnlyCollection<MetricSample> samples, CancellationToken cancellationToken)
    {
        dbContext.MetricSamples.AddRange(samples);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // Do not leave rejected rows tracked: they would make the next server's save in this scope fail as well.
            foreach (var sample in samples) dbContext.Entry(sample).State = EntityState.Detached;
            throw;
        }
    }

    public async Task<IReadOnlyList<MetricSample>> GetRecentAsync(Guid serverId, DateTimeOffset sinceUtc, CancellationToken cancellationToken)
    {
        // Time predicates run in SQL: timestamps are integer ticks (UtcMillisecondsConverter, T7.R7).
        return await dbContext.MetricSamples.AsNoTracking()
            .Where(item => item.ServerId == serverId && item.AtUtc >= sinceUtc)
            .OrderBy(item => item.AtUtc)
            .ToArrayAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MetricSample>> GetLatestAsync(Guid serverId, CancellationToken cancellationToken)
    {
        var latestAt = await dbContext.MetricSamples.AsNoTracking().Where(item => item.ServerId == serverId)
            .Select(item => (DateTimeOffset?)item.AtUtc).MaxAsync(cancellationToken);
        if (latestAt is null) return [];
        return await dbContext.MetricSamples.AsNoTracking()
            .Where(item => item.ServerId == serverId && item.AtUtc == latestAt.Value)
            .ToArrayAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MetricSample>> GetUnrolledAsync(DateTimeOffset beforeUtc, CancellationToken cancellationToken)
    {
        return await dbContext.MetricSamples.AsNoTracking().Where(item => item.AtUtc < beforeUtc)
            .OrderBy(item => item.AtUtc).ToArrayAsync(cancellationToken);
    }

    public async Task SaveRollupsAsync(IReadOnlyCollection<MetricRollup> rollups, DateTimeOffset rawPruneBeforeUtc,
        DateTimeOffset rollupPruneBeforeUtc, CancellationToken cancellationToken)
    {
        var serverIds = rollups.Select(item => item.ServerId).Distinct().ToArray();
        var existingRollups = serverIds.Length == 0
            ? []
            : await dbContext.MetricRollups.Where(item => serverIds.Contains(item.ServerId)).ToArrayAsync(cancellationToken);
        var rollupIndex = existingRollups.ToDictionary(item => (item.ServerId, item.Metric, item.BucketStartUtc));
        foreach (var rollup in rollups)
        {
            if (!rollupIndex.TryGetValue((rollup.ServerId, rollup.Metric, rollup.BucketStartUtc), out var existing))
            {
                dbContext.MetricRollups.Add(rollup);
                rollupIndex.Add((rollup.ServerId, rollup.Metric, rollup.BucketStartUtc), rollup);
            }
            else
            {
                dbContext.Entry(existing).Property(nameof(MetricRollup.Minimum)).CurrentValue = rollup.Minimum;
                dbContext.Entry(existing).Property(nameof(MetricRollup.Average)).CurrentValue = rollup.Average;
                dbContext.Entry(existing).Property(nameof(MetricRollup.Maximum)).CurrentValue = rollup.Maximum;
                dbContext.Entry(existing).Property(nameof(MetricRollup.SampleCount)).CurrentValue = rollup.SampleCount;
            }
        }

        var staleSamples = await dbContext.MetricSamples.Where(item => item.AtUtc < rawPruneBeforeUtc).ToArrayAsync(cancellationToken);
        dbContext.MetricSamples.RemoveRange(staleSamples);
        var staleRollups = await dbContext.MetricRollups.Where(item => item.BucketStartUtc < rollupPruneBeforeUtc)
            .ToArrayAsync(cancellationToken);
        dbContext.MetricRollups.RemoveRange(staleRollups);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MetricRollup>> GetRollupsAsync(Guid serverId, DateTimeOffset sinceUtc, CancellationToken cancellationToken)
    {
        return await dbContext.MetricRollups.AsNoTracking()
            .Where(item => item.ServerId == serverId && item.BucketStartUtc >= sinceUtc)
            .OrderBy(item => item.BucketStartUtc)
            .ToArrayAsync(cancellationToken);
    }
}
