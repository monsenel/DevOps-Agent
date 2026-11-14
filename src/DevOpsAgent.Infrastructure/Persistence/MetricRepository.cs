using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Domain.Monitoring;
using Microsoft.EntityFrameworkCore;

namespace DevOpsAgent.Infrastructure.Persistence;

public sealed class MetricRepository(AppDbContext dbContext) : IMetricRepository
{
    public async Task AddSamplesAsync(IReadOnlyCollection<MetricSample> samples, CancellationToken cancellationToken)
    {
        dbContext.MetricSamples.AddRange(samples);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MetricSample>> GetRecentAsync(Guid serverId, DateTimeOffset sinceUtc, CancellationToken cancellationToken)
    {
        var serverSamples = await dbContext.MetricSamples.AsNoTracking().Where(item => item.ServerId == serverId)
            .ToArrayAsync(cancellationToken);
        return serverSamples.Where(item => item.AtUtc >= sinceUtc).OrderBy(item => item.AtUtc).ToArray();
    }

    public async Task<IReadOnlyList<MetricSample>> GetLatestAsync(Guid serverId, CancellationToken cancellationToken)
    {
        var serverSamples = await dbContext.MetricSamples.AsNoTracking().Where(item => item.ServerId == serverId)
            .ToArrayAsync(cancellationToken);
        var latestAt = serverSamples.MaxBy(item => item.AtUtc)?.AtUtc;
        return latestAt is null ? [] : serverSamples.Where(item => item.AtUtc == latestAt).ToArray();
    }

    public async Task<IReadOnlyList<MetricSample>> GetUnrolledAsync(DateTimeOffset beforeUtc, CancellationToken cancellationToken)
    {
        var samples = await dbContext.MetricSamples.AsNoTracking().ToArrayAsync(cancellationToken);
        return samples.Where(item => item.AtUtc < beforeUtc).OrderBy(item => item.AtUtc).ToArray();
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

        var allSamples = await dbContext.MetricSamples.ToArrayAsync(cancellationToken);
        var staleSamples = allSamples.Where(item => item.AtUtc < rawPruneBeforeUtc).ToArray();
        dbContext.MetricSamples.RemoveRange(staleSamples);
        var allRollups = await dbContext.MetricRollups.ToArrayAsync(cancellationToken);
        var staleRollups = allRollups.Where(item => item.BucketStartUtc < rollupPruneBeforeUtc).ToArray();
        dbContext.MetricRollups.RemoveRange(staleRollups);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
