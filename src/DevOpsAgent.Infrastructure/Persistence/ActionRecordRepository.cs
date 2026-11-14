using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Domain.Agent;

namespace DevOpsAgent.Infrastructure.Persistence;

public sealed class ActionRecordRepository(AppDbContext dbContext) : IActionRecordRepository
{
    public async Task AddAsync(ActionRecord action, CancellationToken cancellationToken)
    {
        dbContext.ActionRecords.Add(action);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
