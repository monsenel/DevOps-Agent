using System.Globalization;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Domain.Monitoring;

namespace DevOpsAgent.Application.Monitoring;

public sealed record MetricBaseline(double Mean, double StdDev, int SampleCount);

public sealed record MetricAnomalyResult(
    string Metric,
    double LatestValue,
    double BaselineMean,
    double BaselineStdDev,
    double ZScore,
    int BaselineSampleCount);

/// <summary>
/// Computes rolling mean/standard-deviation baselines from stored <see cref="MetricRollup"/> and
/// <see cref="MetricSample"/> data and flags sustained &gt;3σ deviations as Medium <c>metric-anomaly</c> alerts.
/// </summary>
public sealed class AnomalyBaselineService(IMetricRepository metrics, TimeProvider timeProvider)
{
    public const string AlertRule = "metric-anomaly";
    public const double SigmaThreshold = 3.0;
    public const int MinimumBaselinePoints = 10;
    public const int SustainedSampleCount = 3;
    public const double MinimumStdDev = 1.0;

    public static readonly TimeSpan BaselineWindow = TimeSpan.FromDays(7);
    public static readonly TimeSpan RecentSampleWindow = TimeSpan.FromHours(24);
    public static readonly TimeSpan SustainedLookback = TimeSpan.FromMinutes(10);

    private static readonly IReadOnlyList<string> MonitoredMetrics =
    [
        "cpu_percent",
        "memory_percent",
        "load_1m",
        "disk_percent"
    ];

    public async Task<IReadOnlyList<AlertCondition>> EvaluateServerAsync(Guid serverId, string serverName, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var rollups = await metrics.GetRollupsAsync(serverId, now - BaselineWindow, cancellationToken);
        var samples = await metrics.GetRecentAsync(serverId, now - RecentSampleWindow, cancellationToken);
        if (samples.Count < SustainedSampleCount) return [];

        var conditions = new List<AlertCondition>();
        var sustainedCutoff = now - SustainedLookback;

        foreach (var metricName in MonitoredMetrics)
        {
            var metricSamples = samples
                .Where(item => string.Equals(item.Metric, metricName, StringComparison.Ordinal))
                .OrderBy(item => item.AtUtc)
                .ToArray();
            if (metricSamples.Length < SustainedSampleCount) continue;

            var recent = metricSamples
                .Where(item => item.AtUtc >= sustainedCutoff)
                .Select(item => item.Value)
                .ToArray();
            if (recent.Length < SustainedSampleCount) continue;

            // Keep the sustained window out of the baseline so an ongoing spike does not inflate its own baseline.
            var baselineValues = new List<double>();
            foreach (var rollup in rollups.Where(item => string.Equals(item.Metric, metricName, StringComparison.Ordinal)
                         && item.BucketStartUtc < sustainedCutoff))
            {
                baselineValues.Add(rollup.Average);
            }

            foreach (var sample in metricSamples.Where(item => item.AtUtc < sustainedCutoff))
            {
                baselineValues.Add(sample.Value);
            }

            var anomaly = EvaluateMetric(metricName, baselineValues, recent);
            if (anomaly is null) continue;

            conditions.Add(new AlertCondition(
                Key: $"{AlertRule}:{serverId:N}:{metricName}",
                Rule: AlertRule,
                Severity: AlertSeverity.Medium,
                ServerId: serverId,
                Target: $"{serverName}:{metricName}",
                Title: string.Create(CultureInfo.InvariantCulture,
                    $"{serverName}: anomalous {metricName} ({anomaly.LatestValue:0.0} vs baseline {anomaly.BaselineMean:0.0})"),
                Details: string.Create(CultureInfo.InvariantCulture,
                    $"Sustained {anomaly.ZScore:0.0}σ deviation above rolling baseline (mean {anomaly.BaselineMean:0.0}, σ {anomaly.BaselineStdDev:0.0}, baseline points {anomaly.BaselineSampleCount}).")));
        }

        return conditions;
    }

    public static MetricBaseline ComputeBaseline(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return new MetricBaseline(0, 0, 0);

        double sum = 0;
        for (var i = 0; i < values.Count; i++) sum += values[i];
        var mean = sum / values.Count;

        double varianceSum = 0;
        for (var i = 0; i < values.Count; i++)
        {
            var delta = values[i] - mean;
            varianceSum += delta * delta;
        }

        var stdDev = Math.Sqrt(varianceSum / values.Count);
        return new MetricBaseline(mean, stdDev, values.Count);
    }

    public static MetricAnomalyResult? EvaluateMetric(
        string metric,
        IReadOnlyList<double> baselineValues,
        IReadOnlyList<double> recentValues)
    {
        if (baselineValues.Count < MinimumBaselinePoints || recentValues.Count < SustainedSampleCount)
            return null;

        var baseline = ComputeBaseline(baselineValues);
        var effectiveStdDev = Math.Max(baseline.StdDev, MinimumStdDev);
        var upperBound = baseline.Mean + (SigmaThreshold * effectiveStdDev);

        var startIndex = recentValues.Count - SustainedSampleCount;
        for (var i = startIndex; i < recentValues.Count; i++)
        {
            if (recentValues[i] <= upperBound)
                return null;
        }

        var latest = recentValues[^1];
        var zScore = (latest - baseline.Mean) / effectiveStdDev;
        return new MetricAnomalyResult(metric, latest, baseline.Mean, effectiveStdDev, zScore, baseline.SampleCount);
    }
}
