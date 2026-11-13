namespace DevOpsAgent.Domain.Chat;

public sealed class ChatConversation
{
    private ChatConversation()
    {
    }

    public ChatConversation(string title, DateTimeOffset createdAtUtc)
    {
        Id = Guid.NewGuid();
        Title = title;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public List<ChatMessageRecord> Messages { get; private set; } = [];

    public ChatMessageRecord AddMessage(string role, string content, DateTimeOffset createdAtUtc)
    {
        var message = new ChatMessageRecord(Id, role, content, createdAtUtc);
        Messages.Add(message);
        UpdatedAtUtc = createdAtUtc;

        if (role == "user" && Messages.Count == 1)
        {
            Title = content.Length <= 72 ? content : string.Concat(content.AsSpan(0, 69), "…");
        }

        return message;
    }
}
