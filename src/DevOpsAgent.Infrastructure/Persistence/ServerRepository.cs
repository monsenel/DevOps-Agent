using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Domain.Servers;
using Microsoft.EntityFrameworkCore;

namespace DevOpsAgent.Infrastructure.Persistence;

public sealed class ServerRepository(AppDbContext dbContext) : IServerRepository
{
    public async Task<IReadOnlyList<Server>> ListAsync(CancellationToken cancellationToken) =>
        await dbContext.Servers.AsNoTracking().OrderBy(server => server.Name).ToArrayAsync(cancellationToken);

    public async Task<Server?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        // A Blazor circuit keeps one DbContext for its whole life, and a tracking query does not refresh an entity it already
        // tracks. Other scopes write to servers too (capability probes, a rejected sudo password, T7.R24), so an entity that
        // is already tracked is reloaded: an edit must start from, and a change be compared against, what is stored now.
        var tracked = dbContext.Servers.Local.FirstOrDefault(server => server.Id == id);
        if (tracked is null) return await dbContext.Servers.SingleOrDefaultAsync(server => server.Id == id, cancellationToken);

        var entry = dbContext.Entry(tracked);
        await entry.ReloadAsync(cancellationToken);
        return entry.State == EntityState.Detached ? null : tracked;
    }

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
