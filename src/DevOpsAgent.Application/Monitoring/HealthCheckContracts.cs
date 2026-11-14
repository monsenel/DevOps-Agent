using DevOpsAgent.Domain.Monitoring;

namespace DevOpsAgent.Application.Monitoring;

public sealed record HealthCheckInput(string Name, HealthCheckType Type, string Target, int IntervalSeconds, bool IsEnabled = true);

public sealed record HealthCheckDto(Guid Id, Guid ServerId, string Name, HealthCheckType Type, string Target,
    int IntervalSeconds, bool IsEnabled, HealthCheckStatus Status, int? StatusCode, int? TlsDaysRemaining,
    string? Error, DateTimeOffset? CheckedAtUtc);

public sealed record HealthCheckSuggestion(string Name, HealthCheckType Type, string Target, int IntervalSeconds);

public sealed record HealthProbeResult(bool IsHealthy, int? StatusCode, int? TlsDaysRemaining, string? Error);

public interface IHealthProbe
{
    Task<HealthProbeResult> ProbeAsync(HealthCheckType type, string target, CancellationToken cancellationToken);
}
