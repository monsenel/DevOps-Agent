using DevOpsAgent.Domain.Chat;

namespace DevOpsAgent.Application.Abstractions;

public interface IChatConversationRepository
{
    Task<IReadOnlyList<ChatConversation>> ListAsync(CancellationToken cancellationToken);
    Task<ChatConversation?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task AddAsync(ChatConversation conversation, CancellationToken cancellationToken);
    Task SaveMessageAsync(ChatMessageRecord newMessage, CancellationToken cancellationToken);

    /// <summary>Saves changes to a conversation loaded with <see cref="GetAsync"/> (its effort).</summary>
    Task SaveAsync(CancellationToken cancellationToken);
}
