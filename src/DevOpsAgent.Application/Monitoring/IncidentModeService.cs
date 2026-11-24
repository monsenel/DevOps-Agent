using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Domain.Monitoring;
using Microsoft.Extensions.Logging;

namespace DevOpsAgent.Application.Monitoring;

public sealed record IncidentModeState(
    bool Active,
    DateTimeOffset? ActivatedAtUtc = null,
    string? Reason = null,
    string? ActivatedBy = null)
{
    public static IncidentModeState Inactive { get; } = new(false);
}

/// <summary>
/// Persists the owner's Incident Mode toggle in AppSettings. While active, alerts prioritize High/Critical conditions
/// and Low-severity noise notifications are suppressed.
/// </summary>
public sealed class IncidentModeService(
    IAppSettingRepository settings,
    TimeProvider timeProvider,
    ILogger<IncidentModeService> logger)
{
    public const string SettingKey = "monitoring.incident-mode.v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IncidentModeState> GetAsync(CancellationToken cancellationToken)
    {
        var raw = await settings.GetAsync(SettingKey, cancellationToken);
        if (string.IsNullOrWhiteSpace(raw)) return IncidentModeState.Inactive;
        try
        {
            return JsonSerializer.Deserialize<IncidentModeState>(raw, JsonOptions) ?? IncidentModeState.Inactive;
        }
        catch (JsonException exception)
        {
            logger.LogWarning("Stored incident mode state could not be parsed ({ErrorType}); treating as inactive.",
                exception.GetType().Name);
            return IncidentModeState.Inactive;
        }
    }

    public Task<IncidentModeState> ActivateAsync(string? reason, string? activatedBy, CancellationToken cancellationToken) =>
        SetAsync(active: true, reason, activatedBy, cancellationToken);

    public Task<IncidentModeState> DeactivateAsync(CancellationToken cancellationToken) =>
        SetAsync(active: false, reason: null, activatedBy: null, cancellationToken);

    public async Task<IncidentModeState> SetAsync(bool active, string? reason, string? activatedBy, CancellationToken cancellationToken)
    {
        var normalizedReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (normalizedReason is { Length: > 240 }) normalizedReason = normalizedReason[..240];
        var normalizedActor = string.IsNullOrWhiteSpace(activatedBy) ? null : activatedBy.Trim();

        var state = active
            ? new IncidentModeState(true, timeProvider.GetUtcNow(), normalizedReason, normalizedActor)
            : IncidentModeState.Inactive;

        await settings.SetAsync(SettingKey, JsonSerializer.Serialize(state, JsonOptions), cancellationToken);
        return state;
    }

    public static bool ShouldSuppressNotification(AlertSeverity severity, bool incidentModeActive) =>
        incidentModeActive && severity == AlertSeverity.Low;

    public static IReadOnlyList<AlertDto> PrioritizeAlerts(IReadOnlyList<AlertDto> alerts, bool incidentModeActive)
    {
        if (!incidentModeActive || alerts.Count <= 1) return alerts;
        return alerts
            .OrderByDescending(alert => alert.Severity)
            .ThenByDescending(alert => alert.LastSeenAtUtc)
            .ToArray();
    }
}
