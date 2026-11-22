using DevOpsAgent.Domain.Monitoring;

namespace DevOpsAgent.Application.Monitoring;

public sealed record AlertCondition(string Key, string Rule, AlertSeverity Severity, Guid? ServerId,
    string Target, string Title, string Details);

public sealed record AlertDto(Guid Id, string Rule, AlertSeverity Severity, AlertState State, Guid? ServerId,
    string Target, string Title, string Details, DateTimeOffset FirstSeenAtUtc, DateTimeOffset LastSeenAtUtc,
    DateTimeOffset? AcknowledgedAtUtc, DateTimeOffset? ResolvedAtUtc, int Count);

/// <summary>
/// A (server, rule) pair that could not be freshly evaluated this cycle (e.g. host unreachable, probe truncated, or no
/// fresh metric sample). Open/acknowledged alerts in this scope are held instead of auto-resolving, up to
/// <see cref="AlertService.MaxHoldDuration"/> (T7.R20, D-061).
/// </summary>
public sealed record HeldRuleScope(Guid ServerId, string Rule);

public sealed record AlertEvaluationSnapshot(
    IReadOnlyList<AlertCondition> Conditions,
    IReadOnlyCollection<HeldRuleScope> HeldScopes)
{
    public static AlertEvaluationSnapshot FromConditions(IReadOnlyList<AlertCondition> conditions) =>
        new(conditions, []);
}

public interface IAlertSignalSource
{
    Task<IReadOnlyList<AlertCondition>> GetActiveConditionsAsync(CancellationToken cancellationToken);

    async Task<AlertEvaluationSnapshot> EvaluateSignalsAsync(CancellationToken cancellationToken) =>
        AlertEvaluationSnapshot.FromConditions(await GetActiveConditionsAsync(cancellationToken));
}
