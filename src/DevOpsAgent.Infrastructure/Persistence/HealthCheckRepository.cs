using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Domain.Monitoring;
using Microsoft.EntityFrameworkCore;

namespace DevOpsAgent.Infrastructure.Persistence;

public sealed class HealthCheckRepository(AppDbContext dbContext) : IHealthCheckRepository
{
    public async Task<IReadOnlyList<HealthCheck>> ListAsync(Guid serverId, CancellationToken cancellationToken) =>
        await dbContext.HealthChecks.Where(item => item.ServerId == serverId)
            .OrderBy(item => item.Name).ToArrayAsync(cancellationToken);

    public async Task<IReadOnlyList<HealthCheck>> ListEnabledAsync(CancellationToken cancellationToken) =>
        await dbContext.HealthChecks.Where(item => item.IsEnabled).ToArrayAsync(cancellationToken);

    public Task<HealthCheck?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.HealthChecks.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);

    public async Task AddAsync(HealthCheck healthCheck, CancellationToken cancellationToken)
    {
        dbContext.HealthChecks.Add(healthCheck);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveAsync(CancellationToken cancellationToken) =>
        _ = await dbContext.SaveChangesAsync(cancellationToken);

    public async Task DeleteAsync(HealthCheck healthCheck, CancellationToken cancellationToken)
    {
        dbContext.HealthChecks.Remove(healthCheck);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
