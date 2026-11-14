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
    public string? ApprovedBy { get; private set; }
    public DateTimeOffset? ApprovedAtUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }
    public string CommandsJson { get; private set; } = "{}";
    public string ResultsJson { get; private set; } = "[]";
    public int? ExitCode { get; private set; }
    public long? DurationMilliseconds { get; private set; }

    public void Approve(string approvedBy, DateTimeOffset approvedAtUtc)
    {
        EnsureProposed();
        ApprovedBy = approvedBy;
        ApprovedAtUtc = approvedAtUtc;
        Status = ActionRecordStatus.Approved;
    }

    public void Reject(string rejectedBy, DateTimeOffset rejectedAtUtc)
    {
        EnsureProposed();
        ApprovedBy = rejectedBy;
        ApprovedAtUtc = rejectedAtUtc;
        CompletedAtUtc = rejectedAtUtc;
        Status = ActionRecordStatus.Rejected;
    }

    public void MarkRunning() 
    {
        if (Status != ActionRecordStatus.Approved)
        {
            throw new InvalidOperationException("Only approved actions can start running.");
        }

        Status = ActionRecordStatus.Running;
    }

    public void Complete(
        string commandsJson,
        string resultsJson,
        int? exitCode,
        long durationMilliseconds,
        DateTimeOffset completedAtUtc,
        bool succeeded)
    {
        if (Status != ActionRecordStatus.Running)
        {
            throw new InvalidOperationException("Only running actions can be completed.");
        }

        CommandsJson = commandsJson;
        ResultsJson = resultsJson;
        ExitCode = exitCode;
        DurationMilliseconds = Math.Max(0, durationMilliseconds);
        CompletedAtUtc = completedAtUtc;
        Status = succeeded ? ActionRecordStatus.Succeeded : ActionRecordStatus.Failed;
    }

    private void EnsureProposed()
    {
        if (Status != ActionRecordStatus.Proposed)
        {
            throw new InvalidOperationException("Only proposed actions can be approved or rejected.");
        }
    }
}
