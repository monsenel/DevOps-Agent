namespace DevOpsAgent.Application.Monitoring;

public sealed record ParsedMetrics(
    IReadOnlyDictionary<string, double> Values,
    IReadOnlyDictionary<string, long> CpuCounters,
    IReadOnlyDictionary<string, long> NetworkCounters);

public sealed record MetricValue(string Name, double Value, DateTimeOffset AtUtc);

public sealed record ServerMetrics(Guid ServerId, string ServerName, IReadOnlyList<MetricValue> Latest, IReadOnlyList<MetricValue> Recent);
