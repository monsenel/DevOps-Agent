using System.Runtime.CompilerServices;
using System.Text;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Domain.Chat;
using Microsoft.Extensions.AI;

namespace DevOpsAgent.Application.Chat;

public sealed class ChatService(
    IChatConversationRepository conversations,
    ILlmProviderRepository providers,
    IChatClientFactory chatClientFactory,
    TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<ChatConversationSummary>> ListAsync(CancellationToken cancellationToken)
    {
        var items = await conversations.ListAsync(cancellationToken);
        return items
            .OrderByDescending(item => item.UpdatedAtUtc)
            .Select(item => new ChatConversationSummary(item.Id, item.Title, item.UpdatedAtUtc))
            .ToArray();
    }

    public async Task<IReadOnlyList<ChatMessageView>> GetMessagesAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        var conversation = await conversations.GetAsync(conversationId, cancellationToken);
        return conversation?.Messages
            .OrderBy(message => message.CreatedAtUtc)
            .Select(message => new ChatMessageView(message.Id, message.Role, message.Content, message.CreatedAtUtc))
            .ToArray() ?? [];
    }

    public async Task<Guid> CreateAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var conversation = new ChatConversation("New conversation", now);
        await conversations.AddAsync(conversation, cancellationToken);
        return conversation.Id;
    }

    public async IAsyncEnumerable<string> SendAsync(
        Guid conversationId,
        string text,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var prompt = text.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new ArgumentException("Enter a message before sending.", nameof(text));
        }

        var conversation = await conversations.GetAsync(conversationId, cancellationToken)
            ?? throw new InvalidOperationException("The conversation no longer exists.");
        var userMessage = conversation.AddMessage("user", prompt, timeProvider.GetUtcNow());
        await conversations.SaveMessageAsync(userMessage, cancellationToken);

        var provider = (await providers.ListAsync(cancellationToken)).SingleOrDefault(item => item.IsDefault)
            ?? throw new InvalidOperationException("Add and select a default LLM provider in Settings before starting a chat.");
        using var client = chatClientFactory.Create(provider);
        var history = conversation.Messages
            .OrderBy(message => message.CreatedAtUtc)
            .Select(message => new ChatMessage(
                message.Role == "assistant" ? ChatRole.Assistant : ChatRole.User,
                message.Content))
            .ToArray();
        var options = new ChatOptions
        {
            ModelId = provider.Model,
            Temperature = (float)provider.Temperature
        };
        var response = new StringBuilder();

        if (provider.StreamingEnabled)
        {
            await foreach (var update in client.GetStreamingResponseAsync(history, options, cancellationToken))
            {
                var chunk = update.Text;
                if (!string.IsNullOrEmpty(chunk))
                {
                    response.Append(chunk);
                    yield return chunk;
                }
            }
        }
        else
        {
            var completion = await client.GetResponseAsync(history, options, cancellationToken);
            var textContent = string.Concat(completion.Messages.Select(message => message.Text));
            response.Append(textContent);
            if (!string.IsNullOrEmpty(textContent))
            {
                yield return textContent;
            }
        }

        if (response.Length > 0)
        {
            var assistantMessage = conversation.AddMessage("assistant", response.ToString(), timeProvider.GetUtcNow());
            await conversations.SaveMessageAsync(assistantMessage, cancellationToken);
        }
    }
}
