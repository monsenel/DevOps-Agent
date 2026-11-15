namespace DevOpsAgent.Domain.Chat;

public enum ChatAttachmentKind
{
    Text,
    Image
}

public enum ChatEffort
{
    Low,
    Medium,
    High
}

/// <summary>
/// A file attached to a user message (T8.5). Text is stored already redacted; an image is stored as base64 and only accepted
/// for models that take images.
/// </summary>
public sealed class ChatAttachment
{
    private ChatAttachment()
    {
    }

    public ChatAttachment(Guid messageId, string fileName, string contentType, ChatAttachmentKind kind, long sizeBytes, string content)
    {
        Id = Guid.NewGuid();
        MessageId = messageId;
        FileName = fileName;
        ContentType = contentType;
        Kind = kind;
        SizeBytes = sizeBytes;
        Content = content;
    }

    public Guid Id { get; private set; }
    public Guid MessageId { get; private set; }
    public string FileName { get; private set; } = string.Empty;
    public string ContentType { get; private set; } = string.Empty;
    public ChatAttachmentKind Kind { get; private set; }
    public long SizeBytes { get; private set; }

    /// <summary>Redacted text, or base64 for an image.</summary>
    public string Content { get; private set; } = string.Empty;
}
