using DevOpsAgent.Domain.Agent;

namespace DevOpsAgent.Application.Abstractions;

public interface IActionRecordRepository
{
    Task AddAsync(ActionRecord action, CancellationToken cancellationToken);
}
