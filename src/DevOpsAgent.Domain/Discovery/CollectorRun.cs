namespace DevOpsAgent.Domain.Discovery;

public enum CollectorRunStatus
{
    Succeeded,
    Failed,
    Skipped
}

public sealed class CollectorRun
{
    private CollectorRun()
    {
    }

    public CollectorRun(
        Guid snapshotId,
        Guid serverId,
        string collectorName,
        CollectorRunStatus status,
        string rawOutput,
        string standardError,
        int? exitCode,
        string? error,
        string? resultJson,
        TimeSpan duration,
        DateTimeOffset createdAtUtc)
    {
        Id = Guid.NewGuid();
        SnapshotId = snapshotId;
        ServerId = serverId;
        CollectorName = collectorName;
        Status = status;
        RawOutput = rawOutput;
        StandardError = standardError;
        ExitCode = exitCode;
        Error = error;
        ResultJson = resultJson;
        Duration = duration;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }
    public Guid SnapshotId { get; private set; }
    public Guid ServerId { get; private set; }
    public string CollectorName { get; private set; } = string.Empty;
    public CollectorRunStatus Status { get; private set; }
    public string RawOutput { get; private set; } = string.Empty;
    public string StandardError { get; private set; } = string.Empty;
    public int? ExitCode { get; private set; }
    public string? Error { get; private set; }
    public string? ResultJson { get; private set; }
    public TimeSpan Duration { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
}
