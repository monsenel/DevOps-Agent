using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Domain.Updates;
using Microsoft.EntityFrameworkCore;

namespace DevOpsAgent.Infrastructure.Persistence;

public sealed class UpdateScanRepository(AppDbContext dbContext) : IUpdateScanRepository
{
    public async Task<IReadOnlyList<UpdateScan>> ListAsync(CancellationToken cancellationToken) =>
        await dbContext.UpdateScans.AsNoTracking().ToArrayAsync(cancellationToken);

    public async Task SaveAsync(UpdateScan scan, CancellationToken cancellationToken)
    {
        var existing = await dbContext.UpdateScans.FindAsync([scan.ServerId], cancellationToken);
        if (existing is null) dbContext.UpdateScans.Add(scan);
        else dbContext.Entry(existing).CurrentValues.SetValues(scan);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
