using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Domain.Agent;
using Microsoft.EntityFrameworkCore;

namespace DevOpsAgent.Infrastructure.Persistence;

public sealed class ActionRecordRepository(AppDbContext dbContext) : IActionRecordRepository
{
    public async Task<IReadOnlyList<ActionRecord>> ListAsync(CancellationToken cancellationToken) =>
        (await dbContext.ActionRecords.AsNoTracking().ToArrayAsync(cancellationToken))
            .OrderByDescending(action => action.CreatedAtUtc).ToArray();

    public async Task<ActionRecord?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        await dbContext.ActionRecords.SingleOrDefaultAsync(action => action.Id == id, cancellationToken);

    public async Task AddAsync(ActionRecord action, CancellationToken cancellationToken)
    {
        dbContext.ActionRecords.Add(action);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveAsync(ActionRecord action, CancellationToken cancellationToken)
    {
        dbContext.ActionRecords.Update(action);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
