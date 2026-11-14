using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Application.Monitoring;
using DevOpsAgent.Domain.Discovery;
using DevOpsAgent.Domain.Monitoring;

namespace DevOpsAgent.Infrastructure.Monitoring;

public sealed class AlertSignalSource(
    IServerRepository servers,
    IMetricRepository metrics,
    IHealthCheckRepository healthChecks,
    ISnapshotRepository snapshots,
    ISshRunner sshRunner,
    AuthLogScanner authLogScanner,
    DriftService drift,
    TimeProvider timeProvider) : IAlertSignalSource
{
    public async Task<IReadOnlyList<AlertCondition>> GetActiveConditionsAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var result = new List<AlertCondition>();
        var inventory = await servers.ListAsync(cancellationToken);
        foreach (var server in inventory)
        {
            result.AddRange(await authLogScanner.ScanAsync(server, cancellationToken));
            var (remoteStatus, hostKeyMismatch) = await TryGetRemoteStatusAsync(server.Id, cancellationToken);
            // The exception names the host whose key mismatched. Behind a jump chain that can be the jump host, in which
            // case this server is merely unreachable (the jump host raises its own mismatch alert).
            var mismatchIsOwn = hostKeyMismatch is not null && hostKeyMismatch.Port == server.Port
                && string.Equals(hostKeyMismatch.Host, server.Host, StringComparison.OrdinalIgnoreCase);
            if (hostKeyMismatch is not null && !mismatchIsOwn)
            {
                result.Add(Condition("server-unreachable", server.Id.ToString("N"), AlertSeverity.High, server.Id, server.Host,
                    $"{server.Name} is unreachable",
                    $"The route to this server goes through {hostKeyMismatch.Host}:{hostKeyMismatch.Port}, which presented an unexpected SSH host key and was refused."));
            }
            else if (hostKeyMismatch is not null)
            {
                // A changed host key is a possible impersonation, not an outage: report it distinctly from "unreachable".
                result.Add(Condition("ssh-host-key-mismatch", server.Id.ToString("N"), AlertSeverity.High, server.Id, server.Host,
                    $"{server.Name} presented an unexpected SSH host key",
                    $"The host key of {hostKeyMismatch.Host}:{hostKeyMismatch.Port} does not match the pinned key; the connection was refused. "
                    + $"Presented fingerprint: {hostKeyMismatch.Fingerprint ?? "unknown"}. Verify the server before trusting the new key."));
            }
            else if (remoteStatus is null)
            {
                result.Add(Condition("server-unreachable", server.Id.ToString("N"), AlertSeverity.High, server.Id, server.Host,
                    $"{server.Name} is unreachable", "The scheduled read-only SSH status check could not reach the server."));
            }
            else
            {
                foreach (var container in remoteStatus.Containers.Where(item => item.State != "running"))
                    result.Add(Condition("container-state", $"{server.Id:N}:{container.Name}", AlertSeverity.High, server.Id,
                        container.Name, $"Container {container.Name} is {container.State}", $"Latest read-only Docker status reports: {container.State}."));
                foreach (var unit in remoteStatus.FailedUnits)
                    result.Add(Condition("unit-failed", $"{server.Id:N}:{unit}", AlertSeverity.High, server.Id,
                        unit, $"Service {unit} failed", "systemctl reports this service in its failed state."));

                var latest = await metrics.GetLatestAsync(server.Id, cancellationToken);
                if (latest.Count > 0)
                {
                var recent = await metrics.GetRecentAsync(server.Id, now - TimeSpan.FromMinutes(5) - SustainedThreshold.LookbackSlack, cancellationToken);
                AddThreshold(recent, "disk_percent", "disk", 90, TimeSpan.Zero, AlertSeverity.High, "Disk usage is at least 90%", result, server.Id, server.Name);
                AddThreshold(recent, "memory_percent", "memory", 90, TimeSpan.FromMinutes(5), AlertSeverity.High, "Memory usage has stayed at least 90% for 5 minutes", result, server.Id, server.Name);
                var cpuCores = latest.FirstOrDefault(item => item.Metric == "cpu_cores")?.Value;
                if (cpuCores is > 0)
                    AddThreshold(recent, "load_1m", "load", cpuCores.Value * 2, TimeSpan.FromMinutes(5), AlertSeverity.Medium,
                        $"Load has stayed above {cpuCores.Value * 2:0} for 5 minutes", result, server.Id, server.Name, strict: true);
                }
            }
        }

        foreach (var server in inventory)
        {
            foreach (var check in await healthChecks.ListAsync(server.Id, cancellationToken))
            {
                if (check.LastStatus == HealthCheckStatus.Unhealthy)
                    result.Add(Condition("health-check-failing", check.Id.ToString("N"), AlertSeverity.High, server.Id,
                        check.Target, $"Health check {check.Name} is failing", check.LastError ?? "The latest check failed."));
                if (check.Type == HealthCheckType.Https && check.TlsDaysRemaining is int days && days < 14)
                    result.Add(Condition("tls-expiring", check.Id.ToString("N"), days < 0 ? AlertSeverity.High : AlertSeverity.Medium,
                        server.Id, check.Target, $"TLS certificate expires in {days} days", $"Certificate expiry is fewer than 14 days away."));
            }
        }

        var history = (await snapshots.ListAsync(cancellationToken)).Where(item => item.FinishedAtUtc is not null)
            .OrderByDescending(item => item.StartedAtUtc).Take(2).ToArray();
        if (history.Length == 2)
        {
            var changes = await drift.CompareAsync(history[1].Id, history[0].Id, cancellationToken);
            foreach (var change in changes.Changes.Where(item => item.EntityType == DriftEntityType.Listener && item.ChangeType == DriftChangeType.Added))
            {
                var hostId = ReadServerIdFromNodeKey(change.NodeKey);
                result.Add(Condition("new-listener", change.Key, AlertSeverity.Medium, hostId, change.NodeKey ?? change.Key,
                    "A new listener was discovered", change.Description));
            }
        }

        return result;
    }

    private async Task<(RemoteServiceStatus? Status, SshHostKeyMismatchException? HostKeyMismatch)> TryGetRemoteStatusAsync(
        Guid serverId, CancellationToken cancellationToken)
    {
        const string command = "printf '__DVA_DOCKER__\\n'; if command -v docker >/dev/null 2>&1; then docker ps -a --format '{{.Names}}\\t{{.State}}'; fi; " +
            "printf '__DVA_SYSTEMD_FAILED__\\n'; systemctl --failed --type=service --no-legend --plain 2>/dev/null || true";
        try
        {
            var response = await sshRunner.RunAsync(serverId, command, new SshRunOptions(TimeSpan.FromSeconds(20)), cancellationToken);
            return (response.ExitCode == 0 ? RemoteServiceStatusParser.Parse(response.StandardOutput) : null, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (SshHostKeyMismatchException exception) { return (null, exception); }
        catch (Exception) { return (null, null); }
    }

    private static void AddThreshold(IReadOnlyList<MetricSample> samples, string metric, string key, double threshold,
        TimeSpan requiredDuration, AlertSeverity severity, string title, ICollection<AlertCondition> result, Guid serverId, string serverName, bool strict = false)
    {
        if (!SustainedThreshold.IsBreached(samples, metric, threshold, requiredDuration, strict, out var latestValue)) return;
        result.Add(Condition($"threshold-{key}", serverId.ToString("N"), severity, serverId, serverName,
            $"{serverName}: {title}", $"Latest {metric} value is {latestValue:0.0}."));
    }

    private static AlertCondition Condition(string rule, string targetKey, AlertSeverity severity, Guid? serverId,
        string target, string title, string details) => new($"{rule}:{targetKey}", rule, severity, serverId, target, title, details);

    private static Guid? ReadServerIdFromNodeKey(string? key)
    {
        if (key is null) return null;
        var pieces = key.Split(':');
        return pieces.Length > 1 && Guid.TryParse(pieces[1], out var serverId) ? serverId : null;
    }

}
