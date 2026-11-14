using DevOpsAgent.Domain.Updates;

namespace DevOpsAgent.Application.Abstractions;

public interface IUpdateScanRepository
{
    Task<IReadOnlyList<UpdateScan>> ListAsync(CancellationToken cancellationToken);
    Task SaveAsync(UpdateScan scan, CancellationToken cancellationToken);
}
