namespace DevOpsAgent.Domain.Monitoring;

public sealed class MetricSample
{
    private MetricSample() { }

    public MetricSample(Guid serverId, string metric, double value, DateTimeOffset atUtc)
    {
        Id = Guid.NewGuid();
        ServerId = serverId;
        Metric = metric;
        Value = value;
        AtUtc = atUtc;
    }

    public Guid Id { get; private set; }
    public Guid ServerId { get; private set; }
    public string Metric { get; private set; } = string.Empty;
    public double Value { get; private set; }
    public DateTimeOffset AtUtc { get; private set; }
}
