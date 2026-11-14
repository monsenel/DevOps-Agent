using DevOpsAgent.Domain.Monitoring;

namespace DevOpsAgent.Application.Monitoring;

public sealed record AlertCondition(string Key, string Rule, AlertSeverity Severity, Guid? ServerId,
    string Target, string Title, string Details);

public sealed record AlertDto(Guid Id, string Rule, AlertSeverity Severity, AlertState State, Guid? ServerId,
    string Target, string Title, string Details, DateTimeOffset FirstSeenAtUtc, DateTimeOffset LastSeenAtUtc,
    DateTimeOffset? AcknowledgedAtUtc, DateTimeOffset? ResolvedAtUtc, int Count);

public interface IAlertSignalSource
{
    Task<IReadOnlyList<AlertCondition>> GetActiveConditionsAsync(CancellationToken cancellationToken);
}
