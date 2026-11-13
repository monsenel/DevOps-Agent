using DevOpsAgent.Domain.Chat;

namespace DevOpsAgent.Application.Abstractions;

public interface IChatConversationRepository
{
    Task<IReadOnlyList<ChatConversation>> ListAsync(CancellationToken cancellationToken);
    Task<ChatConversation?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task AddAsync(ChatConversation conversation, CancellationToken cancellationToken);
    Task SaveAsync(ChatConversation conversation, CancellationToken cancellationToken);
}
