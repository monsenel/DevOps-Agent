using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Domain.Monitoring;

namespace DevOpsAgent.Application.Monitoring;

public sealed class RollupService(IMetricRepository metrics, TimeProvider timeProvider)
{
    public async Task RollAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var before = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute / 5 * 5, 0, TimeSpan.Zero);
        var cutoff = before.AddHours(-24);
        var samples = await metrics.GetUnrolledAsync(before, cancellationToken);
        var rollups = samples.Where(sample => !sample.Metric.StartsWith('_'))
            .GroupBy(sample => (sample.ServerId, sample.Metric,
                Bucket: new DateTimeOffset(sample.AtUtc.Year, sample.AtUtc.Month, sample.AtUtc.Day, sample.AtUtc.Hour,
                    sample.AtUtc.Minute / 5 * 5, 0, TimeSpan.Zero)))
            .Select(group => new MetricRollup(group.Key.ServerId, group.Key.Metric, group.Key.Bucket,
                group.Min(item => item.Value), group.Average(item => item.Value), group.Max(item => item.Value), group.Count()))
            .ToArray();
        await metrics.SaveRollupsAsync(rollups, cutoff, cancellationToken);
    }
}
