using DevOpsAgent.Domain.Monitoring;

namespace DevOpsAgent.Application.Abstractions;

public interface IAlertRepository
{
    Task<IReadOnlyList<Alert>> ListAsync(AlertState? state, CancellationToken cancellationToken);
    Task<Alert?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<Alert>> ListActiveAsync(CancellationToken cancellationToken);
    Task AddAsync(Alert alert, CancellationToken cancellationToken);
    Task SaveAsync(CancellationToken cancellationToken);
}
