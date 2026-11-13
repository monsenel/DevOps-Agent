using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Domain.Llm;
using Microsoft.EntityFrameworkCore;

namespace DevOpsAgent.Infrastructure.Persistence;

public sealed class LlmProviderRepository(AppDbContext dbContext) : ILlmProviderRepository
{
    public async Task<IReadOnlyList<LlmProvider>> ListAsync(CancellationToken cancellationToken) =>
        await dbContext.LlmProviders.AsNoTracking().OrderBy(provider => provider.Name).ToArrayAsync(cancellationToken);

    public Task<LlmProvider?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.LlmProviders.SingleOrDefaultAsync(provider => provider.Id == id, cancellationToken);

    public async Task AddAsync(LlmProvider provider, bool makeDefault, CancellationToken cancellationToken)
    {
        dbContext.LlmProviders.Add(provider);
        await PersistAsync(provider, makeDefault, cancellationToken);
    }

    public Task SaveAsync(LlmProvider provider, bool makeDefault, CancellationToken cancellationToken) =>
        PersistAsync(provider, makeDefault, cancellationToken);

    public async Task DeleteAsync(LlmProvider provider, CancellationToken cancellationToken)
    {
        dbContext.LlmProviders.Remove(provider);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task PersistAsync(LlmProvider provider, bool makeDefault, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        if (makeDefault)
        {
            await dbContext.LlmProviders
                .Where(existing => existing.Id != provider.Id && existing.IsDefault)
                .ExecuteUpdateAsync(update => update.SetProperty(existing => existing.IsDefault, false), cancellationToken);
        }

        provider.SetDefault(makeDefault);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
