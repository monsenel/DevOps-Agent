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

    public async Task<bool> TryTransitionFromProposedAsync(
        Guid id,
        ActionRecordStatus to,
        string actor,
        DateTimeOffset atUtc,
        CancellationToken cancellationToken)
    {
        if (to is not (ActionRecordStatus.Running or ActionRecordStatus.Rejected))
        {
            throw new ArgumentOutOfRangeException(nameof(to), to, "Only Running and Rejected can be claimed from Proposed.");
        }

        var query = dbContext.ActionRecords.Where(action => action.Id == id && action.Status == ActionRecordStatus.Proposed);
        var changed = to == ActionRecordStatus.Running
            ? await query.ExecuteUpdateAsync(setters => setters
                .SetProperty(action => action.Status, ActionRecordStatus.Running)
                .SetProperty(action => action.ApprovedBy, actor)
                .SetProperty(action => action.ApprovedAtUtc, atUtc), cancellationToken)
            : await query.ExecuteUpdateAsync(setters => setters
                .SetProperty(action => action.Status, ActionRecordStatus.Rejected)
                .SetProperty(action => action.ApprovedBy, actor)
                .SetProperty(action => action.ApprovedAtUtc, atUtc)
                .SetProperty(action => action.CompletedAtUtc, atUtc), cancellationToken);
        if (changed == 0) return false;

        // ExecuteUpdate bypasses the change tracker; refresh any tracked copy so later saves do not overwrite the claim.
        foreach (var entry in dbContext.ChangeTracker.Entries<ActionRecord>().Where(entry => entry.Entity.Id == id).ToArray())
        {
            await entry.ReloadAsync(cancellationToken);
        }

        return true;
    }
}
