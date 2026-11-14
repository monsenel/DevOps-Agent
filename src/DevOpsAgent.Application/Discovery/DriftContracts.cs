using DevOpsAgent.Domain.Discovery;

namespace DevOpsAgent.Application.Discovery;

public enum DriftChangeType
{
    Added,
    Removed,
    Changed
}

public enum DriftEntityType
{
    Node,
    Listener,
    Edge
}

public sealed record DriftChange(
    DriftChangeType ChangeType,
    DriftEntityType EntityType,
    string Key,
    string Description,
    string? NodeKey = null,
    string? SourceNodeKey = null,
    string? TargetNodeKey = null,
    Guid? ServerId = null);

public sealed record DriftReport(
    Guid FromSnapshotId,
    Guid ToSnapshotId,
    IReadOnlyList<DriftChange> Changes)
{
    public IReadOnlyList<DriftChange> NewListeners => Changes
        .Where(change => change.ChangeType == DriftChangeType.Added && change.EntityType == DriftEntityType.Listener)
        .ToArray();
}

public sealed record SnapshotSummary(
    Guid Id,
    DateTimeOffset StartedAtUtc,
    SnapshotStatus Status,
    int SucceededServerCount,
    int FailedServerCount);
