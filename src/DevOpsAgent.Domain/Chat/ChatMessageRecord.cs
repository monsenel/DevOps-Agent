namespace DevOpsAgent.Domain.Chat;

public sealed class ChatMessageRecord
{
    private ChatMessageRecord()
    {
    }

    internal ChatMessageRecord(Guid conversationId, string role, string content, DateTimeOffset createdAtUtc)
    {
        Id = Guid.NewGuid();
        ConversationId = conversationId;
        Role = role;
        Content = content;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }
    public Guid ConversationId { get; private set; }
    public ChatConversation Conversation { get; private set; } = null!;
    public string Role { get; private set; } = string.Empty;
    public string Content { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public List<ChatAttachment> Attachments { get; private set; } = [];

    public ChatAttachment AddAttachment(string fileName, string contentType, ChatAttachmentKind kind, long sizeBytes, string content)
    {
        var attachment = new ChatAttachment(Id, fileName, contentType, kind, sizeBytes, content);
        Attachments.Add(attachment);
        return attachment;
    }
}
