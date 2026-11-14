namespace DevOpsAgent.Domain.Monitoring;

public sealed class MetricRollup
{
    private MetricRollup() { }

    public MetricRollup(Guid serverId, string metric, DateTimeOffset bucketStartUtc, double minimum, double average, double maximum, int sampleCount)
    {
        Id = Guid.NewGuid();
        ServerId = serverId;
        Metric = metric;
        BucketStartUtc = bucketStartUtc;
        Minimum = minimum;
        Average = average;
        Maximum = maximum;
        SampleCount = sampleCount;
    }

    public Guid Id { get; private set; }
    public Guid ServerId { get; private set; }
    public string Metric { get; private set; } = string.Empty;
    public DateTimeOffset BucketStartUtc { get; private set; }
    public double Minimum { get; private set; }
    public double Average { get; private set; }
    public double Maximum { get; private set; }
    public int SampleCount { get; private set; }
}
