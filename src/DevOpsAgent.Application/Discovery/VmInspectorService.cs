using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Monitoring;
using DevOpsAgent.Domain.Discovery;
using DevOpsAgent.Domain.Monitoring;
using DevOpsAgent.Domain.Servers;

namespace DevOpsAgent.Application.Discovery;

public enum PortExposure
{
    External = 0,
    Private = 1,
    Internal = 2
}

public sealed record VmResourceMetrics(
    double? CpuPercent,
    int? CpuCores,
    double? MemoryPercent,
    double? MemoryUsedBytes,
    double? MemoryTotalBytes,
    double? DiskPercent,
    double? DiskUsedBytes,
    double? DiskTotalBytes,
    double? Load1m,
    double? Load5m,
    double? Load15m,
    double? NetworkRxBytesPerSec,
    double? NetworkTxBytesPerSec,
    double? SystemUptimeSeconds,
    DateTimeOffset? SampledAtUtc);

public sealed record VmDockerContainerView(
    string Id,
    string Name,
    string Image,
    string State,
    string? HealthStatus,
    string? ComposeProject,
    string? ComposeService,
    string UptimeText,
    string? PortsText);

public sealed record VmSystemdServiceView(
    string Name,
    int MainPid,
    string UptimeText,
    bool HasListeners,
    bool IsOsService);

public sealed record VmAppStackView(
    string Kind,
    string Name,
    string Detail);

public sealed record VmPortBindingView(
    string Address,
    int Port,
    string Protocol,
    PortExposure Exposure,
    string ExposureLabel,
    string? OwnerName,
    string? FirewallNote);

public sealed record VmInspectorSnapshot(
    Guid ServerId,
    string ServerName,
    string Host,
    string Group,
    string? OsInfo,
    bool DockerInstalled,
    bool DockerDaemonRunning,
    VmResourceMetrics Resources,
    IReadOnlyList<VmDockerContainerView> Containers,
    IReadOnlyList<VmSystemdServiceView> SystemdServices,
    IReadOnlyList<VmAppStackView> AppStacks,
    IReadOnlyList<VmPortBindingView> Ports,
    IReadOnlyList<ProcessInfo> Processes,
    int TotalProcessCount,
    int OpenAlertCount,
    DateTimeOffset? SnapshotAtUtc,
    bool IsLiveRefresh = false,
    string? Warning = null);

public sealed class VmInspectorService(
    IServerRepository servers,
    ISnapshotRepository snapshots,
    IMetricRepository metrics,
    IAlertRepository alerts,
    ILiveVmProbe liveProbe,
    TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<VmInspectorSnapshot?> LoadAsync(Guid serverId, CancellationToken cancellationToken)
    {
        var server = await servers.GetAsync(serverId, cancellationToken);
        if (server is null)
        {
            return null;
        }

        var runs = await snapshots.ListLatestServerCollectorRunsAsync(serverId, cancellationToken);
        var metricSamples = await metrics.GetLatestAsync(serverId, cancellationToken);
        var openAlerts = await alerts.ListAsync(AlertState.Open, cancellationToken);
        var serverAlertCount = openAlerts.Count(item => item.ServerId == serverId);

        return BuildSnapshot(server, runs, metricSamples, serverAlertCount, live: null);
    }

    public async Task<VmInspectorSnapshot?> RefreshLiveAsync(Guid serverId, CancellationToken cancellationToken)
    {
        var server = await servers.GetAsync(serverId, cancellationToken);
        if (server is null)
        {
            return null;
        }

        var runs = await snapshots.ListLatestServerCollectorRunsAsync(serverId, cancellationToken);
        var metricSamples = await metrics.GetLatestAsync(serverId, cancellationToken);
        var openAlerts = await alerts.ListAsync(AlertState.Open, cancellationToken);
        var serverAlertCount = openAlerts.Count(item => item.ServerId == serverId);

        var live = await liveProbe.ProbeAsync(server, cancellationToken);
        return BuildSnapshot(server, runs, metricSamples, serverAlertCount, live);
    }

    private VmInspectorSnapshot BuildSnapshot(
        Server server,
        IReadOnlyList<CollectorRun> runs,
        IReadOnlyList<MetricSample> metricSamples,
        int openAlertCount,
        LiveVmProbeResult? live)
    {
        var byName = runs.ToDictionary(run => run.CollectorName, StringComparer.OrdinalIgnoreCase);

        var docker = DeserializeRun<DockerDiscoveryResult>(byName.GetValueOrDefault("docker"));
        var systemd = DeserializeRun<SystemdDiscoveryResult>(byName.GetValueOrDefault("systemd"));
        var listeners = live?.Listeners is { Count: > 0 }
            ? new ListenerDiscoveryResult(live.Listeners)
            : DeserializeRun<ListenerDiscoveryResult>(byName.GetValueOrDefault("listeners"));
        var firewall = DeserializeRun<FirewallDiscoveryResult>(byName.GetValueOrDefault("firewall"));
        var nginx = DeserializeRun<NginxDiscoveryResult>(byName.GetValueOrDefault("nginx"));
        var reverseProxy = DeserializeRun<ReverseProxyDiscoveryResult>(byName.GetValueOrDefault("reverse-proxy"));
        var appConfig = DeserializeRun<AppConfigDiscoveryResult>(byName.GetValueOrDefault("app-config"));
        var processes = live?.ProcessSnapshot ?? DeserializeRun<ProcessDiscoveryResult>(byName.GetValueOrDefault("processes"));

        var now = live?.CapturedAtUtc ?? timeProvider.GetUtcNow();
        var latestRunAt = runs.Count > 0 ? runs.Max(run => run.CreatedAtUtc) : (DateTimeOffset?)null;
        var snapshotAt = live?.CapturedAtUtc ?? latestRunAt;

        var resources = BuildResources(metricSamples, live?.Metrics, processes?.SystemUptimeSeconds, live?.CapturedAtUtc);
        var containerViews = BuildContainers(docker, processes, now);
        var systemdViews = BuildSystemdServices(systemd, processes, now);
        var appStacks = BuildAppStacks(nginx, reverseProxy, appConfig);
        var portViews = BuildPorts(listeners, docker, firewall);

        return new VmInspectorSnapshot(
            server.Id,
            server.Name,
            server.Host,
            server.Group,
            server.OsInfo,
            processes?.DockerInstalled ?? docker is not null,
            processes?.DockerDaemonRunning ?? (docker is not null && docker.Containers.Count >= 0),
            resources,
            containerViews,
            systemdViews,
            appStacks,
            portViews,
            processes?.Processes ?? [],
            processes?.TotalProcessCount ?? processes?.Processes.Count ?? 0,
            openAlertCount,
            snapshotAt,
            live is not null,
            live?.Warning);
    }

    private static VmResourceMetrics BuildResources(
        IReadOnlyList<MetricSample> stored,
        ParsedMetrics? liveMetrics,
        double? systemUptimeSeconds,
        DateTimeOffset? liveAtUtc)
    {
        var map = stored.ToDictionary(item => item.Metric, item => item.Value, StringComparer.OrdinalIgnoreCase);
        DateTimeOffset? sampledAt = liveAtUtc ?? (stored.Count > 0 ? stored.Max(item => item.AtUtc) : null);

        if (liveMetrics is not null)
        {
            foreach (var pair in liveMetrics.Values)
            {
                map[pair.Key] = pair.Value;
            }
        }

        double? Read(string key) => map.TryGetValue(key, out var val) ? val : null;

        return new VmResourceMetrics(
            Read("cpu_percent"),
            Read("cpu_cores") is { } cores ? (int)Math.Round(cores) : null,
            Read("memory_percent"),
            Read("memory_used_bytes"),
            Read("memory_total_bytes"),
            Read("disk_percent"),
            Read("disk_used_bytes"),
            Read("disk_total_bytes"),
            Read("load_1m"),
            Read("load_5m"),
            Read("load_15m"),
            Read("network_receive_bytes_per_second"),
            Read("network_transmit_bytes_per_second"),
            systemUptimeSeconds,
            sampledAt);
    }

    private static IReadOnlyList<VmDockerContainerView> BuildContainers(
        DockerDiscoveryResult? docker,
        ProcessDiscoveryResult? processes,
        DateTimeOffset now)
    {
        var byName = new Dictionary<string, VmDockerContainerView>(StringComparer.OrdinalIgnoreCase);

        if (docker is not null)
        {
            foreach (var container in docker.Containers)
            {
                var ports = container.PublishedPorts.Count == 0
                    ? null
                    : string.Join(", ", container.PublishedPorts.Select(port =>
                        $"{(string.IsNullOrWhiteSpace(port.HostIp) ? "*" : port.HostIp)}:{port.HostPort}->{port.ContainerPort}/{port.Protocol}"));
                var uptime = container.StartedAtUtc is { } started
                    ? (string.Equals(container.State, "running", StringComparison.OrdinalIgnoreCase)
                        ? $"Up {FormatDuration(now - started)}"
                        : container.FinishedAtUtc is { } finished
                            ? $"Stopped {FormatDuration(now - finished)} ago"
                            : container.State)
                    : container.State;

                byName[container.Name] = new VmDockerContainerView(
                    container.Id,
                    container.Name,
                    container.Image,
                    container.State,
                    container.HealthStatus,
                    container.ComposeProject,
                    container.ComposeService,
                    uptime,
                    ports);
            }
        }

        if (processes is not null)
        {
            foreach (var container in processes.DockerContainers)
            {
                if (byName.TryGetValue(container.Name, out var existing))
                {
                    byName[container.Name] = existing with
                    {
                        State = container.State,
                        UptimeText = !string.IsNullOrWhiteSpace(container.StatusText) ? container.StatusText : existing.UptimeText,
                        PortsText = existing.PortsText ?? container.PortsText
                    };
                }
                else
                {
                    byName[container.Name] = new VmDockerContainerView(
                        container.Id,
                        container.Name,
                        container.Image,
                        container.State,
                        null,
                        null,
                        null,
                        container.StatusText,
                        container.PortsText);
                }
            }
        }

        return byName.Values
            .OrderBy(item => string.Equals(item.State, "running", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<VmSystemdServiceView> BuildSystemdServices(
        SystemdDiscoveryResult? systemd,
        ProcessDiscoveryResult? processes,
        DateTimeOffset now)
    {
        var list = new Dictionary<string, VmSystemdServiceView>(StringComparer.OrdinalIgnoreCase);
        var processElapsedByUnit = (processes?.Processes ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item.UnitName))
            .GroupBy(item => item.UnitName!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Max(item => item.ElapsedSeconds), StringComparer.OrdinalIgnoreCase);

        if (systemd is not null)
        {
            foreach (var unit in systemd.Units)
            {
                var uptime = unit.ActiveSinceUtc is { } since
                    ? $"Up {FormatDuration(now - since)}"
                    : processElapsedByUnit.TryGetValue(unit.Name, out var elapsed) && elapsed > 0
                        ? $"Up {FormatDuration(TimeSpan.FromSeconds(elapsed))}"
                        : "Running";

                list[unit.Name] = new VmSystemdServiceView(
                    unit.Name,
                    unit.MainPid,
                    uptime,
                    unit.ListenerPids.Count > 0,
                    IsOsSystemdUnit(unit.Name));
            }

            if (systemd.RunningServices is not null)
            {
                foreach (var unitName in systemd.RunningServices)
                {
                    if (list.ContainsKey(unitName))
                    {
                        continue;
                    }

                    var uptime = processElapsedByUnit.TryGetValue(unitName, out var elapsed) && elapsed > 0
                        ? $"Up {FormatDuration(TimeSpan.FromSeconds(elapsed))}"
                        : "Running";
                    list[unitName] = new VmSystemdServiceView(
                        unitName,
                        0,
                        uptime,
                        false,
                        IsOsSystemdUnit(unitName));
                }
            }
        }

        return list.Values
            .OrderBy(item => item.IsOsService ? 1 : 0)
            .ThenByDescending(item => item.HasListeners)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<VmAppStackView> BuildAppStacks(
        NginxDiscoveryResult? nginx,
        ReverseProxyDiscoveryResult? reverseProxy,
        AppConfigDiscoveryResult? appConfig)
    {
        var list = new List<VmAppStackView>();
        if (nginx is not null)
        {
            foreach (var block in nginx.Servers.Take(12))
            {
                var domains = block.ServerNames.Count > 0
                    ? string.Join(", ", block.ServerNames.Select(name => name.Value))
                    : "default";
                var ports = block.Listens.Count > 0
                    ? string.Join(", ", block.Listens.Select(listen => listen.Value).Distinct())
                    : "80";
                list.Add(new VmAppStackView("nginx", domains, $"Ports: {ports}"));
            }
        }

        if (reverseProxy is not null)
        {
            foreach (var route in reverseProxy.Routes.Take(12))
            {
                var target = route.Domains.Count > 0 ? string.Join(", ", route.Domains) : (route.FrontendName ?? route.BackendName ?? "route");
                var port = route.ListenPort is int listen ? $" · :{listen}" : string.Empty;
                list.Add(new VmAppStackView(route.ProxyType, target, $"{route.Targets.Count} target(s){port}"));
            }
        }

        if (appConfig is not null)
        {
            foreach (var reference in appConfig.References.Take(15))
            {
                var portText = reference.Port is int port ? $":{port}" : string.Empty;
                var owner = !string.IsNullOrWhiteSpace(reference.ContainerName) ? $" ({reference.ContainerName})" : string.Empty;
                list.Add(new VmAppStackView(reference.DependencyType, $"{reference.Host}{portText}{owner}",
                    $"{reference.SourceKind} · {reference.SourcePath}"));
            }
        }

        return list;
    }

    private static IReadOnlyList<VmPortBindingView> BuildPorts(
        ListenerDiscoveryResult? listeners,
        DockerDiscoveryResult? docker,
        FirewallDiscoveryResult? firewall)
    {
        var items = new Dictionary<string, VmPortBindingView>(StringComparer.OrdinalIgnoreCase);

        if (listeners is not null)
        {
            foreach (var listener in listeners.Listeners)
            {
                var exposure = ClassifyExposure(listener.Address);
                var label = ExposureLabel(exposure);
                var key = $"{listener.Protocol}|{listener.Address}|{listener.Port}";
                items[key] = new VmPortBindingView(
                    listener.Address,
                    listener.Port,
                    listener.Protocol,
                    exposure,
                    label,
                    listener.ProcessName,
                    EvaluateFirewallNote(firewall, listener.Port, listener.Protocol, exposure));
            }
        }

        if (docker is not null)
        {
            foreach (var container in docker.Containers)
            {
                foreach (var port in container.PublishedPorts)
                {
                    var hostIp = string.IsNullOrWhiteSpace(port.HostIp) ? "0.0.0.0" : port.HostIp;
                    var exposure = ClassifyExposure(hostIp);
                    var key = $"{port.Protocol}|{hostIp}|{port.HostPort}";
                    if (!items.ContainsKey(key))
                    {
                        items[key] = new VmPortBindingView(
                            hostIp,
                            port.HostPort,
                            port.Protocol,
                            exposure,
                            ExposureLabel(exposure),
                            $"docker:{container.Name} -> :{port.ContainerPort}",
                            EvaluateFirewallNote(firewall, port.HostPort, port.Protocol, exposure));
                    }
                }
            }
        }

        return items.Values
            .OrderBy(item => (int)item.Exposure)
            .ThenBy(item => item.Port)
            .ThenBy(item => item.Address, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static PortExposure ClassifyExposure(string address)
    {
        var clean = address.Trim().Trim('[', ']');
        var scopeIndex = clean.IndexOf('%');
        if (scopeIndex > 0)
        {
            clean = clean[..scopeIndex];
        }

        if (clean is "" or "*" or "0.0.0.0" or "::" or "::0")
        {
            return PortExposure.External;
        }

        if (clean.Equals("localhost", StringComparison.OrdinalIgnoreCase) || clean.StartsWith("unix:", StringComparison.OrdinalIgnoreCase))
        {
            return PortExposure.Internal;
        }

        if (!IPAddress.TryParse(clean, out var ip))
        {
            return PortExposure.Private;
        }

        if (IPAddress.IsLoopback(ip))
        {
            return PortExposure.Internal;
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            // RFC1918, CGNAT 100.64.0.0/10, Link-local 169.254.0.0/16
            if (bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127)
                || (bytes[0] == 169 && bytes[1] == 254))
            {
                return PortExposure.Private;
            }

            return PortExposure.External;
        }

        if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal)
        {
            return PortExposure.Private;
        }

        return PortExposure.External;
    }

    private static string ExposureLabel(PortExposure exposure) => exposure switch
    {
        PortExposure.External => "External (All/Public)",
        PortExposure.Private => "Private / LAN",
        _ => "Internal (Loopback)"
    };

    private static string? EvaluateFirewallNote(
        FirewallDiscoveryResult? firewall,
        int port,
        string protocol,
        PortExposure exposure)
    {
        if (exposure == PortExposure.Internal)
        {
            return "Loopback only";
        }

        if (firewall is null)
        {
            return null;
        }

        if (!firewall.Enabled)
        {
            return $"{firewall.Backend}: inactive (open)";
        }

        foreach (var rule in firewall.Rules)
        {
            if (!string.Equals(rule.Direction, "in", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(rule.Direction, "inbound", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(rule.Direction))
            {
                continue;
            }

            if (!string.Equals(rule.Protocol, "any", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(rule.Protocol, protocol, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (PortMatchesRule(port, rule.Port))
            {
                return $"{firewall.Backend}: {rule.Action}";
            }
        }

        return !string.IsNullOrWhiteSpace(firewall.DefaultInbound)
            ? $"{firewall.Backend}: default {firewall.DefaultInbound}"
            : $"{firewall.Backend}: enabled";
    }

    private static bool PortMatchesRule(int port, string? rulePort)
    {
        if (string.IsNullOrWhiteSpace(rulePort))
        {
            return false;
        }

        foreach (var segment in rulePort.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var slash = segment.IndexOf('/');
            var value = slash > 0 ? segment[..slash] : segment;
            if (int.TryParse(value, out var single) && single == port)
            {
                return true;
            }

            var colonOrDash = value.IndexOfAny([':', '-']);
            if (colonOrDash > 0
                && int.TryParse(value[..colonOrDash], out var start)
                && int.TryParse(value[(colonOrDash + 1)..], out var end)
                && port >= start && port <= end)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsOsSystemdUnit(string unitName) =>
        unitName.StartsWith("systemd-", StringComparison.OrdinalIgnoreCase)
        || unitName.StartsWith("getty@", StringComparison.OrdinalIgnoreCase)
        || unitName.StartsWith("user@", StringComparison.OrdinalIgnoreCase)
        || unitName.Equals("dbus.service", StringComparison.OrdinalIgnoreCase)
        || unitName.Equals("cron.service", StringComparison.OrdinalIgnoreCase)
        || unitName.Equals("crond.service", StringComparison.OrdinalIgnoreCase)
        || unitName.Equals("rsyslog.service", StringComparison.OrdinalIgnoreCase)
        || unitName.Equals("ssh.service", StringComparison.OrdinalIgnoreCase)
        || unitName.Equals("sshd.service", StringComparison.OrdinalIgnoreCase);

    public static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalSeconds < 60)
        {
            return $"{Math.Max(0, (int)duration.TotalSeconds)}s";
        }

        if (duration.TotalHours < 1)
        {
            return $"{(int)duration.TotalMinutes}m";
        }

        if (duration.TotalDays < 1)
        {
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        }

        return $"{(int)duration.TotalDays}d {duration.Hours}h";
    }

    private static T? DeserializeRun<T>(CollectorRun? run) where T : class
    {
        if (run?.ResultJson is not { Length: > 0 } json)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
