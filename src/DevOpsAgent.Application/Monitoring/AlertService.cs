using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Notifications;
using DevOpsAgent.Domain.Monitoring;
using Microsoft.Extensions.Logging;

namespace DevOpsAgent.Application.Monitoring;

public sealed class AlertService(IAlertRepository alerts, IAlertSignalSource signals, TimeProvider timeProvider,
    IAlertNotificationService notifications, ILogger<AlertService> logger, IKnownSecrets? knownSecrets = null)
{
    public async Task<IReadOnlyList<AlertDto>> ListAsync(AlertState? state, CancellationToken cancellationToken) =>
        (await alerts.ListAsync(state, cancellationToken)).Select(ToDto).ToArray();

    public async Task<int> GetOpenCountAsync(CancellationToken cancellationToken) =>
        (await alerts.ListAsync(AlertState.Open, cancellationToken)).Count;

    public async Task EvaluateAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        // Duplicate keys must never break an evaluation cycle: keep the first condition/alert per key and log the rest.
        var conditions = Distinct(await signals.GetActiveConditionsAsync(cancellationToken), item => item.Key, "condition");
        // Alert text can quote server output: no stored sudo password of any server is kept in it or notified (T7.R26).
        var secrets = knownSecrets is null ? [] : await knownSecrets.GetAsync(cancellationToken);
        if (secrets.Count > 0)
            conditions = conditions.Select(item => item with
            {
                Target = KnownSecretRedaction.Redact(item.Target, secrets),
                Title = KnownSecretRedaction.Redact(item.Title, secrets),
                Details = KnownSecretRedaction.Redact(item.Details, secrets)
            }).ToList();
        var activeByKey = conditions.ToDictionary(item => item.Key, StringComparer.Ordinal);
        var existing = await alerts.ListAsync(null, cancellationToken);
        var existingByKey = Distinct(existing, item => item.Key, "alert").ToDictionary(item => item.Key, StringComparer.Ordinal);
        var opened = new List<Alert>();
        var resolved = new List<Alert>();

        foreach (var condition in conditions)
        {
            if (existingByKey.TryGetValue(condition.Key, out var current))
            {
                var wasResolved = current.State == AlertState.Resolved;
                current.Refresh(condition.Severity, condition.Title, condition.Details, now);
                if (wasResolved) opened.Add(current);
            }
            else
            {
                var alert = new Alert(condition.Key, condition.Rule, condition.Severity, condition.ServerId,
                    condition.Target, condition.Title, condition.Details, now);
                await alerts.AddAsync(alert, cancellationToken);
                opened.Add(alert);
            }
        }

        foreach (var item in existing.Where(item => !activeByKey.ContainsKey(item.Key) && item.State != AlertState.Resolved))
        {
            item.Resolve(now);
            resolved.Add(item);
        }
        if (existing.Count > 0) await alerts.SaveAsync(cancellationToken);
        foreach (var item in opened) await notifications.NotifyOpenedAsync(ToDto(item), cancellationToken);
        foreach (var item in resolved) await notifications.NotifyResolvedAsync(ToDto(item), cancellationToken);
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
        var wasResolved = alert.State == AlertState.Resolved;
        alert.Resolve(timeProvider.GetUtcNow());
        await alerts.SaveAsync(cancellationToken);
        if (!wasResolved) await notifications.NotifyResolvedAsync(ToDto(alert), cancellationToken);
        return true;
    }

    private IReadOnlyList<T> Distinct<T>(IEnumerable<T> items, Func<T, string> keyOf, string kind)
    {
        var result = new List<T>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var key = keyOf(item);
            if (seen.Add(key))
            {
                result.Add(item);
            }
            else
            {
                logger.LogWarning("Ignoring duplicate {Kind} key {AlertKey} during alert evaluation.", kind, key);
            }
        }

        return result;
    }

    private static AlertDto ToDto(Alert item) => new(item.Id, item.Rule, item.Severity, item.State, item.ServerId,
        item.Target, item.Title, item.Details, item.FirstSeenAtUtc, item.LastSeenAtUtc, item.AcknowledgedAtUtc,
        item.ResolvedAtUtc, item.Count);
}
