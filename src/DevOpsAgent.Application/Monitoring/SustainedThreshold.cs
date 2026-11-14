using DevOpsAgent.Domain.Monitoring;

namespace DevOpsAgent.Application.Monitoring;

public static class SustainedThreshold
{
    /// <summary>Extra history to load beyond the required duration so a sample at or before the window start exists.</summary>
    public static readonly TimeSpan LookbackSlack = TimeSpan.FromMinutes(3);

    /// <summary>
    /// True when the latest sample breaches the threshold and, for a non-zero duration, every sample from the
    /// last one at or before (latest − duration) through the latest also breaches it.
    /// </summary>
    public static bool IsBreached(IEnumerable<MetricSample> samples, string metric, double threshold, TimeSpan duration,
        bool strict, out double latestValue)
    {
        latestValue = 0;
        var values = samples.Where(item => item.Metric == metric).OrderBy(item => item.AtUtc).ToArray();
        if (values.Length == 0) return false;
        bool Breaches(double value) => strict ? value > threshold : value >= threshold;
        latestValue = values[^1].Value;
        if (!Breaches(latestValue)) return false;
        if (duration <= TimeSpan.Zero) return true;

        var windowStart = values[^1].AtUtc - duration;
        var anchorIndex = Array.FindLastIndex(values, item => item.AtUtc <= windowStart);
        if (anchorIndex < 0) return false;
        return values.Skip(anchorIndex).All(item => Breaches(item.Value));
    }
}
