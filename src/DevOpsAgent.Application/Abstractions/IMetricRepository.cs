using DevOpsAgent.Domain.Monitoring;

namespace DevOpsAgent.Application.Abstractions;

public interface IMetricRepository
{
    Task AddSamplesAsync(IReadOnlyCollection<MetricSample> samples, CancellationToken cancellationToken);
    Task<IReadOnlyList<MetricSample>> GetRecentAsync(Guid serverId, DateTimeOffset sinceUtc, CancellationToken cancellationToken);
    Task<IReadOnlyList<MetricSample>> GetLatestAsync(Guid serverId, CancellationToken cancellationToken);
    Task<IReadOnlyList<MetricSample>> GetUnrolledAsync(DateTimeOffset beforeUtc, CancellationToken cancellationToken);
    Task SaveRollupsAsync(IReadOnlyCollection<MetricRollup> rollups, DateTimeOffset rawPruneBeforeUtc, DateTimeOffset rollupPruneBeforeUtc, CancellationToken cancellationToken);
}
