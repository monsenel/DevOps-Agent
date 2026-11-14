using DevOpsAgent.Domain.Agent;

namespace DevOpsAgent.Application.Abstractions;

public interface IActionRecordRepository
{
    Task<IReadOnlyList<ActionRecord>> ListAsync(CancellationToken cancellationToken);
    Task<ActionRecord?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task AddAsync(ActionRecord action, CancellationToken cancellationToken);
    Task SaveAsync(ActionRecord action, CancellationToken cancellationToken);
}
