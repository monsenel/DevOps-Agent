using DevOpsAgent.Domain.Agent;

namespace DevOpsAgent.Application.Abstractions;

public interface IActionRecordRepository
{
    Task<IReadOnlyList<ActionRecord>> ListAsync(CancellationToken cancellationToken);
    Task<ActionRecord?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task AddAsync(ActionRecord action, CancellationToken cancellationToken);
    Task SaveAsync(ActionRecord action, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically moves a Proposed action to Running (approve) or Rejected. Returns false when the action is no longer Proposed
    /// (another approver won the race), in which case nothing is changed. Tracked instances are refreshed on success.
    /// </summary>
    Task<bool> TryTransitionFromProposedAsync(
        Guid id,
        ActionRecordStatus to,
        string actor,
        DateTimeOffset atUtc,
        CancellationToken cancellationToken);
}
