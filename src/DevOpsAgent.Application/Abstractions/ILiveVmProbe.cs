using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Application.Monitoring;
using DevOpsAgent.Domain.Servers;

namespace DevOpsAgent.Application.Abstractions;

public sealed record LiveVmProbeResult(
    ParsedMetrics? Metrics,
    ProcessDiscoveryResult? ProcessSnapshot,
    IReadOnlyList<ListenerInfo> Listeners,
    DateTimeOffset CapturedAtUtc,
    string? Warning = null);

public interface ILiveVmProbe
{
    Task<LiveVmProbeResult> ProbeAsync(Server server, CancellationToken cancellationToken);
}
