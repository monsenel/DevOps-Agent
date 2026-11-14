using DevOpsAgent.Domain.Monitoring;

namespace DevOpsAgent.Application.Abstractions;

public interface IHealthCheckRepository
{
    Task<IReadOnlyList<HealthCheck>> ListAsync(Guid serverId, CancellationToken cancellationToken);
    Task<IReadOnlyList<HealthCheck>> ListEnabledAsync(CancellationToken cancellationToken);
    Task<HealthCheck?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task AddAsync(HealthCheck healthCheck, CancellationToken cancellationToken);
    Task SaveAsync(CancellationToken cancellationToken);
    Task DeleteAsync(HealthCheck healthCheck, CancellationToken cancellationToken);
}
