using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Domain.Chat;
using Microsoft.EntityFrameworkCore;

namespace DevOpsAgent.Infrastructure.Persistence;

public sealed class ChatConversationRepository(AppDbContext dbContext) : IChatConversationRepository
{
    public async Task<IReadOnlyList<ChatConversation>> ListAsync(CancellationToken cancellationToken) =>
        await dbContext.ChatConversations
            .AsNoTracking()
            .ToArrayAsync(cancellationToken);

    public Task<ChatConversation?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.ChatConversations
            .Include(conversation => conversation.Messages)
            .ThenInclude(message => message.Attachments)
            .SingleOrDefaultAsync(conversation => conversation.Id == id, cancellationToken);

    public async Task AddAsync(ChatConversation conversation, CancellationToken cancellationToken)
    {
        dbContext.ChatConversations.Add(conversation);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public Task SaveAsync(CancellationToken cancellationToken) => dbContext.SaveChangesAsync(cancellationToken);

    public async Task SaveMessageAsync(ChatMessageRecord newMessage, CancellationToken cancellationToken)
    {
        dbContext.ChatMessages.Add(newMessage);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
