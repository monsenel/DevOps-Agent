namespace DevOpsAgent.Domain.Monitoring;

public enum HealthCheckType
{
    Http,
    Https,
    Tcp
}

public enum HealthCheckStatus
{
    Unknown,
    Healthy,
    Unhealthy
}

public sealed class HealthCheck
{
    private HealthCheck() { }

    public HealthCheck(Guid serverId, string name, HealthCheckType type, string target, int intervalSeconds)
    {
        Id = Guid.NewGuid();
        ServerId = serverId;
        Name = name;
        Type = type;
        Target = target;
        IntervalSeconds = intervalSeconds;
        IsEnabled = true;
        LastStatus = HealthCheckStatus.Unknown;
        CreatedAtUtc = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }
    public Guid ServerId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public HealthCheckType Type { get; private set; }
    public string Target { get; private set; } = string.Empty;
    public int IntervalSeconds { get; private set; }
    public bool IsEnabled { get; private set; }
    public HealthCheckStatus LastStatus { get; private set; }
    public int? LastStatusCode { get; private set; }
    public int? TlsDaysRemaining { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset? LastCheckedAtUtc { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }

    public void Update(string name, HealthCheckType type, string target, int intervalSeconds, bool isEnabled)
    {
        Name = name;
        Type = type;
        Target = target;
        IntervalSeconds = intervalSeconds;
        IsEnabled = isEnabled;
    }

    public void RecordResult(bool healthy, int? statusCode, int? tlsDaysRemaining, string? error, DateTimeOffset checkedAtUtc)
    {
        LastStatus = healthy ? HealthCheckStatus.Healthy : HealthCheckStatus.Unhealthy;
        LastStatusCode = statusCode;
        TlsDaysRemaining = tlsDaysRemaining;
        LastError = string.IsNullOrWhiteSpace(error) ? null : error[..Math.Min(error.Length, 500)];
        LastCheckedAtUtc = checkedAtUtc;
    }
}
