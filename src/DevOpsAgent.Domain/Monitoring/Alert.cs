namespace DevOpsAgent.Domain.Monitoring;

public enum AlertSeverity { Low, Medium, High }
public enum AlertState { Open, Acknowledged, Resolved }

public sealed class Alert
{
    private Alert() { }

    public Alert(string key, string rule, AlertSeverity severity, Guid? serverId, string target, string title,
        string details, DateTimeOffset now)
    {
        Id = Guid.NewGuid();
        Key = key;
        Rule = rule;
        Severity = severity;
        State = AlertState.Open;
        ServerId = serverId;
        Target = target;
        Title = title;
        Details = details;
        FirstSeenAtUtc = now;
        LastSeenAtUtc = now;
        Count = 1;
    }

    public Guid Id { get; private set; }
    public string Key { get; private set; } = string.Empty;
    public string Rule { get; private set; } = string.Empty;
    public AlertSeverity Severity { get; private set; }
    public AlertState State { get; private set; }
    public Guid? ServerId { get; private set; }
    public string Target { get; private set; } = string.Empty;
    public string Title { get; private set; } = string.Empty;
    public string Details { get; private set; } = string.Empty;
    public DateTimeOffset FirstSeenAtUtc { get; private set; }
    public DateTimeOffset LastSeenAtUtc { get; private set; }
    public DateTimeOffset? AcknowledgedAtUtc { get; private set; }
    public DateTimeOffset? ResolvedAtUtc { get; private set; }
    public int Count { get; private set; }

    public void Refresh(AlertSeverity severity, string title, string details, DateTimeOffset now)
    {
        if (State == AlertState.Resolved)
        {
            State = AlertState.Open;
            FirstSeenAtUtc = now;
            AcknowledgedAtUtc = null;
        }
        Severity = severity;
        Title = title;
        Details = details;
        LastSeenAtUtc = now;
        ResolvedAtUtc = null;
        Count++;
    }

    public void Acknowledge(DateTimeOffset now)
    {
        if (State == AlertState.Open)
        {
            State = AlertState.Acknowledged;
            AcknowledgedAtUtc = now;
        }
    }

    public void Resolve(DateTimeOffset now)
    {
        if (State != AlertState.Resolved)
        {
            State = AlertState.Resolved;
            ResolvedAtUtc = now;
        }
    }
}
