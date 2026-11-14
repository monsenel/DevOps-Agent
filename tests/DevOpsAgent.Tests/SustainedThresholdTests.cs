using DevOpsAgent.Application.Monitoring;
using DevOpsAgent.Domain.Monitoring;

namespace DevOpsAgent.Tests;

public sealed class SustainedThresholdTests
{
    private static readonly Guid ServerId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 30, TimeSpan.Zero);

    private static MetricSample[] Series(params double[] values) =>
        values.Select((value, index) => new MetricSample(ServerId, "memory_percent", value,
            Now.AddMinutes(index - values.Length + 1))).ToArray();

    [Fact]
    public void SixMinutesOfOneMinuteSamplesAboveThresholdBreaches()
    {
        var samples = Series(95, 95, 96, 97, 95, 94, 93);

        Assert.True(SustainedThreshold.IsBreached(samples, "memory_percent", 90, TimeSpan.FromMinutes(5), false, out var latest));
        Assert.Equal(93, latest);
    }

    [Fact]
    public void LessThanRequiredHistoryDoesNotBreach()
    {
        var samples = Series(95, 95, 96, 97);

        Assert.False(SustainedThreshold.IsBreached(samples, "memory_percent", 90, TimeSpan.FromMinutes(5), false, out _));
    }

    [Fact]
    public void DipInsideWindowDoesNotBreach()
    {
        var samples = Series(95, 95, 80, 97, 95, 94, 93);

        Assert.False(SustainedThreshold.IsBreached(samples, "memory_percent", 90, TimeSpan.FromMinutes(5), false, out _));
    }

    [Fact]
    public void DipBeforeWindowIsIgnored()
    {
        var samples = Series(50, 95, 95, 96, 97, 95, 94);

        Assert.True(SustainedThreshold.IsBreached(samples, "memory_percent", 90, TimeSpan.FromMinutes(5), false, out _));
    }

    [Fact]
    public void ZeroDurationUsesLatestSampleOnly()
    {
        var samples = Series(10, 91);

        Assert.True(SustainedThreshold.IsBreached(samples, "memory_percent", 90, TimeSpan.Zero, false, out _));
        Assert.False(SustainedThreshold.IsBreached(samples, "memory_percent", 91, TimeSpan.Zero, true, out _));
    }
}
