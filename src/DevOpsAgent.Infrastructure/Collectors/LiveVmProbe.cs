using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Application.Monitoring;
using DevOpsAgent.Domain.Servers;

namespace DevOpsAgent.Infrastructure.Collectors;

public sealed class LiveVmProbe(
    ISshRunner sshRunner,
    TimeProvider timeProvider) : ILiveVmProbe
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(15);

    public async Task<LiveVmProbeResult> ProbeAsync(Server server, CancellationToken cancellationToken)
    {
        var context = new ServerContext(server, sshRunner);
        var processCollector = new ProcessCollector();
        var listenerCollector = new ListenerCollector();

        ParsedMetrics? metrics = null;
        ProcessDiscoveryResult? processSnapshot = null;
        IReadOnlyList<ListenerInfo> listeners = [];
        var warnings = new List<string>();

        // Sample metrics twice cleanly if needed, or take current counters + Mem/Disk/Load/Cores immediately.
        var metricFirst = await sshRunner.RunAsync(server.Id, MetricsParser.CollectionCommand,
            new SshRunOptions(CommandTimeout), cancellationToken);
        if (metricFirst.ExitCode == 0)
        {
            var firstParsed = MetricsParser.Parse(metricFirst.StandardOutput);
            await Task.Delay(TimeSpan.FromMilliseconds(350), cancellationToken);
            var metricSecond = await sshRunner.RunAsync(server.Id, MetricsParser.CollectionCommand,
                new SshRunOptions(CommandTimeout), cancellationToken);
            metrics = metricSecond.ExitCode == 0
                ? MetricsParser.Parse(metricSecond.StandardOutput, firstParsed, TimeSpan.FromMilliseconds(350))
                : firstParsed;
        }
        else if (!string.IsNullOrWhiteSpace(metricFirst.StandardError))
        {
            warnings.Add($"Metrics: {metricFirst.StandardError.Trim()}");
        }

        if (await processCollector.IsApplicableAsync(context, cancellationToken))
        {
            var procOutput = await processCollector.CollectAsync(context, cancellationToken);
            var procParsed = processCollector.Parse(procOutput);
            if (procParsed.IsSuccess && procParsed.Data is { } element)
            {
                processSnapshot = System.Text.Json.JsonSerializer.Deserialize<ProcessDiscoveryResult>(element.GetRawText());
            }
            if (!string.IsNullOrWhiteSpace(procOutput.StandardError))
            {
                warnings.Add(procOutput.StandardError.Trim());
            }
        }

        if (await listenerCollector.IsApplicableAsync(context, cancellationToken))
        {
            var listenerOutput = await listenerCollector.CollectAsync(context, cancellationToken);
            var listenerParsed = listenerCollector.Parse(listenerOutput);
            if (listenerParsed.IsSuccess && listenerParsed.Data is { } element)
            {
                var result = System.Text.Json.JsonSerializer.Deserialize<ListenerDiscoveryResult>(element.GetRawText());
                if (result is not null)
                {
                    listeners = result.Listeners;
                }
            }
        }

        return new LiveVmProbeResult(
            metrics,
            processSnapshot,
            listeners,
            timeProvider.GetUtcNow(),
            warnings.Count > 0 ? string.Join(" · ", warnings) : null);
    }
}
