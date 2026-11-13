using DevOpsAgent.Domain.Llm;

namespace DevOpsAgent.Application.Abstractions;

public interface ILlmProviderRepository
{
    Task<IReadOnlyList<LlmProvider>> ListAsync(CancellationToken cancellationToken);
    Task<LlmProvider?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task AddAsync(LlmProvider provider, bool makeDefault, CancellationToken cancellationToken);
    Task SaveAsync(LlmProvider provider, bool makeDefault, CancellationToken cancellationToken);
    Task DeleteAsync(LlmProvider provider, CancellationToken cancellationToken);
}
