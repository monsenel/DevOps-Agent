namespace DevOpsAgent.Application.Chat;

public sealed record ChatConversationSummary(Guid Id, string Title, DateTimeOffset UpdatedAtUtc);

public sealed record ChatMessageView(Guid Id, string Role, string Content, DateTimeOffset CreatedAtUtc);
