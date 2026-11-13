using DevOpsAgent.Domain.Servers;

namespace DevOpsAgent.Application.Abstractions;

public interface IServerRepository
{
    Task<IReadOnlyList<Server>> ListAsync(CancellationToken cancellationToken);
    Task<Server?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task AddAsync(Server server, CancellationToken cancellationToken);
    Task SaveAsync(Server server, CancellationToken cancellationToken);
    Task DeleteAsync(Server server, CancellationToken cancellationToken);
}

public interface IServerKeyStore
{
    IReadOnlyList<string> ListFileNames();
    bool Contains(string fileName);
    string GetFullPath(string fileName);
}
