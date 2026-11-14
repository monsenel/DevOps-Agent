using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Domain.Monitoring;
using Microsoft.EntityFrameworkCore;

namespace DevOpsAgent.Infrastructure.Persistence;

public sealed class AlertRepository(AppDbContext dbContext) : IAlertRepository
{
    public async Task<IReadOnlyList<Alert>> ListAsync(AlertState? state, CancellationToken cancellationToken)
    {
        var query = dbContext.Alerts.AsQueryable();
        if (state is not null) query = query.Where(item => item.State == state);
        var items = await query.ToArrayAsync(cancellationToken);
        return items.OrderByDescending(item => item.LastSeenAtUtc).ToArray();
    }

    public Task<Alert?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.Alerts.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Alert>> ListActiveAsync(CancellationToken cancellationToken) =>
        await dbContext.Alerts.Where(item => item.State != AlertState.Resolved).ToArrayAsync(cancellationToken);

    public async Task AddAsync(Alert alert, CancellationToken cancellationToken)
    {
        dbContext.Alerts.Add(alert);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveAsync(CancellationToken cancellationToken) =>
        _ = await dbContext.SaveChangesAsync(cancellationToken);
}
