using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Domain.Monitoring;

namespace DevOpsAgent.Application.Monitoring;

public sealed class MetricService(
    IServerRepository servers,
    IMetricRepository metrics,
    ISshRunner sshRunner,
    TimeProvider timeProvider)
{
    public async Task PollAsync(CancellationToken cancellationToken)
    {
        var inventory = await servers.ListAsync(cancellationToken);
        using var semaphore = new SemaphoreSlim(8);
        await Task.WhenAll(inventory.Select(async server =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var result = await sshRunner.RunAsync(server.Id, MetricsParser.CollectionCommand,
                    new SshRunOptions(TimeSpan.FromSeconds(30)), cancellationToken);
                if (result.ExitCode != 0) return;

                var now = timeProvider.GetUtcNow();
                var latest = await metrics.GetLatestAsync(server.Id, cancellationToken);
                var previous = ToPrevious(latest);
                TimeSpan? elapsed = latest.Count == 0 ? null : now - latest.Max(item => item.AtUtc);
                var parsed = MetricsParser.Parse(result.StandardOutput, previous, elapsed);
                var samples = parsed.Values.Select(item => new MetricSample(server.Id, item.Key, item.Value, now)).ToList();
                samples.AddRange(parsed.CpuCounters.Select(item => new MetricSample(server.Id, $"_cpu_{item.Key}", item.Value, now)));
                samples.AddRange(parsed.NetworkCounters.Select(item => new MetricSample(server.Id, $"_net_{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(item.Key))}", item.Value, now)));
                if (samples.Count > 0) await metrics.AddSamplesAsync(samples, cancellationToken);
            }
            finally { semaphore.Release(); }
        }));
    }

    public async Task<ServerMetrics?> GetServerMetricsAsync(Guid serverId, CancellationToken cancellationToken)
    {
        var server = await servers.GetAsync(serverId, cancellationToken);
        if (server is null) return null;
        var since = timeProvider.GetUtcNow().AddHours(-24);
        var recent = await metrics.GetRecentAsync(serverId, since, cancellationToken);
        var publicMetrics = recent.Where(item => !item.Metric.StartsWith('_'))
            .OrderBy(item => item.AtUtc).Select(item => new MetricValue(item.Metric, item.Value, item.AtUtc)).ToArray();
        var latest = publicMetrics.GroupBy(item => item.Name, StringComparer.Ordinal)
            .Select(group => group.Last()).OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
        return new ServerMetrics(serverId, server.Name, latest, publicMetrics);
    }

    private static ParsedMetrics? ToPrevious(IReadOnlyList<MetricSample> samples)
    {
        if (samples.Count == 0) return null;
        var at = samples.Max(item => item.AtUtc);
        var counters = samples.Where(item => item.AtUtc == at).ToArray();
        var cpu = counters.Where(item => item.Metric.StartsWith("_cpu_", StringComparison.Ordinal))
            .ToDictionary(item => item.Metric[5..], item => Convert.ToInt64(item.Value), StringComparer.Ordinal);
        var network = counters.Where(item => item.Metric.StartsWith("_net_", StringComparison.Ordinal))
            .Select(item => (Key: System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(item.Metric[5..])), item.Value))
            .ToDictionary(item => item.Key, item => Convert.ToInt64(item.Value), StringComparer.Ordinal);
        return new ParsedMetrics(new Dictionary<string, double>(), cpu, network);
    }
}
