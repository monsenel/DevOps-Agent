namespace DevOpsAgent.Application.Chat;

public sealed record ChatConversationSummary(Guid Id, string Title, DateTimeOffset UpdatedAtUtc,
    DevOpsAgent.Domain.Chat.ChatEffort Effort = DevOpsAgent.Domain.Chat.ChatEffort.Medium);

public sealed record ChatMessageView(Guid Id, string Role, string Content, DateTimeOffset CreatedAtUtc,
    IReadOnlyList<ChatAttachmentView>? Attachments = null);
