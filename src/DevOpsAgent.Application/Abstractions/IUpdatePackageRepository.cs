using DevOpsAgent.Domain.Updates;

namespace DevOpsAgent.Application.Abstractions;

public interface IUpdatePackageRepository
{
    Task<IReadOnlyList<UpdatePackage>> ListAsync(CancellationToken cancellationToken);
    Task ReplaceForServerAsync(Guid serverId, IReadOnlyCollection<UpdatePackage> packages, CancellationToken cancellationToken);
}
