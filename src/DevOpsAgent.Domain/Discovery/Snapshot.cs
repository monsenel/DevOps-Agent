namespace DevOpsAgent.Domain.Discovery;

public enum SnapshotStatus
{
    Running,
    Completed,
    Partial,
    Failed,
    Cancelled
}

public sealed class Snapshot
{
    private Snapshot()
    {
    }

    public Snapshot(IReadOnlyCollection<Guid> serverIds, DateTimeOffset startedAtUtc)
    {
        Id = Guid.NewGuid();
        ServerIdsJson = System.Text.Json.JsonSerializer.Serialize(serverIds);
        StartedAtUtc = startedAtUtc;
        Status = SnapshotStatus.Running;
    }

    public Guid Id { get; private set; }
    public DateTimeOffset StartedAtUtc { get; private set; }
    public DateTimeOffset? FinishedAtUtc { get; private set; }
    public SnapshotStatus Status { get; private set; }
    public string ServerIdsJson { get; private set; } = "[]";
    public int SucceededServerCount { get; private set; }
    public int FailedServerCount { get; private set; }

    public void Complete(DateTimeOffset finishedAtUtc, int succeededServerCount, int failedServerCount)
    {
        FinishedAtUtc = finishedAtUtc;
        SucceededServerCount = succeededServerCount;
        FailedServerCount = failedServerCount;
        Status = succeededServerCount == 0 && failedServerCount > 0
            ? SnapshotStatus.Failed
            : failedServerCount > 0
                ? SnapshotStatus.Partial
                : SnapshotStatus.Completed;
    }

    public void Fail(DateTimeOffset finishedAtUtc, int succeededServerCount, int failedServerCount)
    {
        FinishedAtUtc = finishedAtUtc;
        SucceededServerCount = succeededServerCount;
        FailedServerCount = failedServerCount;
        Status = SnapshotStatus.Failed;
    }

    public void Cancel(DateTimeOffset finishedAtUtc, int succeededServerCount, int failedServerCount)
    {
        FinishedAtUtc = finishedAtUtc;
        SucceededServerCount = succeededServerCount;
        FailedServerCount = failedServerCount;
        Status = SnapshotStatus.Cancelled;
    }
}
