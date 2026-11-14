using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Domain.Monitoring;

namespace DevOpsAgent.Application.Monitoring;

public sealed class AlertService(IAlertRepository alerts, IAlertSignalSource signals, TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<AlertDto>> ListAsync(AlertState? state, CancellationToken cancellationToken) =>
        (await alerts.ListAsync(state, cancellationToken)).Select(ToDto).ToArray();

    public async Task<int> GetOpenCountAsync(CancellationToken cancellationToken) =>
        (await alerts.ListAsync(AlertState.Open, cancellationToken)).Count;

    public async Task EvaluateAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var conditions = await signals.GetActiveConditionsAsync(cancellationToken);
        var activeByKey = conditions.ToDictionary(item => item.Key, StringComparer.Ordinal);
        var existing = await alerts.ListAsync(null, cancellationToken);
        var existingByKey = existing.ToDictionary(item => item.Key, StringComparer.Ordinal);

        foreach (var condition in conditions)
        {
            if (existingByKey.TryGetValue(condition.Key, out var current))
                current.Refresh(condition.Severity, condition.Title, condition.Details, now);
            else
                await alerts.AddAsync(new Alert(condition.Key, condition.Rule, condition.Severity, condition.ServerId,
                    condition.Target, condition.Title, condition.Details, now), cancellationToken);
        }

        foreach (var item in existing.Where(item => !activeByKey.ContainsKey(item.Key))) item.Resolve(now);
        if (existing.Count > 0) await alerts.SaveAsync(cancellationToken);
    }

    public async Task<bool> AcknowledgeAsync(Guid id, CancellationToken cancellationToken)
    {
        var alert = await alerts.GetAsync(id, cancellationToken);
        if (alert is null) return false;
        alert.Acknowledge(timeProvider.GetUtcNow());
        await alerts.SaveAsync(cancellationToken);
        return true;
    }

    public async Task<bool> ResolveAsync(Guid id, CancellationToken cancellationToken)
    {
        var alert = await alerts.GetAsync(id, cancellationToken);
        if (alert is null) return false;
        alert.Resolve(timeProvider.GetUtcNow());
        await alerts.SaveAsync(cancellationToken);
        return true;
    }

    private static AlertDto ToDto(Alert item) => new(item.Id, item.Rule, item.Severity, item.State, item.ServerId,
        item.Target, item.Title, item.Details, item.FirstSeenAtUtc, item.LastSeenAtUtc, item.AcknowledgedAtUtc,
        item.ResolvedAtUtc, item.Count);
}
