using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Domain.Updates;
using Microsoft.EntityFrameworkCore;

namespace DevOpsAgent.Infrastructure.Persistence;

public sealed class UpdatePackageRepository(AppDbContext dbContext) : IUpdatePackageRepository
{
    public async Task<IReadOnlyList<UpdatePackage>> ListAsync(CancellationToken cancellationToken) =>
        await dbContext.UpdatePackages.AsNoTracking().OrderBy(item => item.Name).ToArrayAsync(cancellationToken);

    public async Task ReplaceForServerAsync(Guid serverId, IReadOnlyCollection<UpdatePackage> packages, CancellationToken cancellationToken)
    {
        var existing = await dbContext.UpdatePackages.Where(item => item.ServerId == serverId).ToArrayAsync(cancellationToken);
        dbContext.UpdatePackages.RemoveRange(existing);
        dbContext.UpdatePackages.AddRange(packages);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
