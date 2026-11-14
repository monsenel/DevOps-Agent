namespace DevOpsAgent.Domain.Agent;

public enum ExecutionMode
{
    ReadOnly,
    Confirm,
    Auto
}

public enum ActionRecordStatus
{
    Proposed,
    Approved,
    Rejected,
    Running,
    Succeeded,
    Failed
}

public sealed class ActionRecord
{
    private ActionRecord()
    {
    }

    public ActionRecord(
        string toolName,
        string group,
        string serverIdsJson,
        string argumentsJson,
        Guid? conversationId,
        DateTimeOffset createdAtUtc)
    {
        Id = Guid.NewGuid();
        ToolName = toolName;
        Group = group;
        ServerIdsJson = serverIdsJson;
        ArgumentsJson = argumentsJson;
        ConversationId = conversationId;
        Status = ActionRecordStatus.Proposed;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }
    public string ToolName { get; private set; } = string.Empty;
    public string Group { get; private set; } = string.Empty;
    public string ServerIdsJson { get; private set; } = "[]";
    public string ArgumentsJson { get; private set; } = "{}";
    public Guid? ConversationId { get; private set; }
    public ActionRecordStatus Status { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
}
