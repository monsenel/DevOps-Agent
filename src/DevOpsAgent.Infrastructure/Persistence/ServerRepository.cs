using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Domain.Servers;
using Microsoft.EntityFrameworkCore;

namespace DevOpsAgent.Infrastructure.Persistence;

public sealed class ServerRepository(AppDbContext dbContext) : IServerRepository
{
    public async Task<IReadOnlyList<Server>> ListAsync(CancellationToken cancellationToken) =>
        await dbContext.Servers.AsNoTracking().OrderBy(server => server.Name).ToArrayAsync(cancellationToken);

    public Task<Server?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.Servers.SingleOrDefaultAsync(server => server.Id == id, cancellationToken);

    public async Task AddAsync(Server server, CancellationToken cancellationToken)
    {
        dbContext.Servers.Add(server);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveAsync(Server server, CancellationToken cancellationToken) =>
        _ = await dbContext.SaveChangesAsync(cancellationToken);

    public async Task DeleteAsync(Server server, CancellationToken cancellationToken)
    {
        dbContext.Servers.Remove(server);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
