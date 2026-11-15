namespace DevOpsAgent.Application.Monitoring;

/// <summary>
/// Time limits of the scheduled monitoring cycles (T7.R17). Each cycle must end inside its interval even when a host hangs:
/// the remote work of a cycle gets a deadline a little shorter than the interval (60 s metrics, 30 s alerts), the hung
/// host is marked failed for that cycle and the other hosts' results are stored.
/// </summary>
public sealed class MonitoringCycleOptions
{
    public const string SectionName = "Monitoring";

    /// <summary>Deadline of one metric command, connect time included (T7.R17).</summary>
    public TimeSpan MetricCommandTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Deadline of the alert engine's status probe, connect time included (T7.R17).</summary>
    public TimeSpan AlertProbeTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Deadline of the remote part of a metric cycle (interval 60 s).</summary>
    public TimeSpan MetricCycleDeadline { get; set; } = TimeSpan.FromSeconds(50);

    /// <summary>Deadline of the remote part of an alert evaluation (interval 30 s).</summary>
    public TimeSpan AlertCycleDeadline { get; set; } = TimeSpan.FromSeconds(25);

    /// <summary>
    /// Budget of the container-status query that rides on each metric cycle (both its attempts). It is a side query: a
    /// hung Docker daemon must not hold a host's slot for as long as the metric command itself may take.
    /// </summary>
    public TimeSpan ContainerQueryTimeout { get; set; } = TimeSpan.FromSeconds(10);
}
