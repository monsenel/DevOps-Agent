using System.Globalization;
using System.Text.RegularExpressions;
using DevOpsAgent.Application.Chat;
using DevOpsAgent.Application.Discovery;

namespace DevOpsAgent.Infrastructure.Collectors;

public static partial class ProcessCollectorParser
{
    public const int MaxStoredProcesses = 60;

    private static readonly HashSet<string> OsServiceUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        "systemd-journald.service", "systemd-udevd.service", "systemd-logind.service",
        "systemd-resolved.service", "systemd-timesyncd.service", "systemd-networkd.service",
        "systemd-machined.service", "dbus.service", "dbus-broker.service",
        "cron.service", "crond.service", "rsyslog.service", "syslog.service",
        "ssh.service", "sshd.service", "polkit.service", "accounts-daemon.service",
        "irqbalance.service", "thermald.service", "snapd.service", "unattended-upgrades.service",
        "multipathd.service", "qemu-guest-agent.service", "walinuxagent.service",
        "cloud-init.service", "chrony.service", "chronyd.service", "ntpd.service",
        "NetworkManager.service", "ModemManager.service", "wpa_supplicant.service",
        "udisks2.service", "packagekit.service", "firewalld.service", "ufw.service",
        "fail2ban.service", "auditd.service", "tuned.service", "atd.service"
    };

    private static readonly HashSet<string> OsProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "systemd", "init", "kthreadd", "sshd", "ssh", "cron", "crond", "atd",
        "systemd-journal", "systemd-journald", "systemd-udevd", "systemd-logind",
        "systemd-resolve", "systemd-resolved", "systemd-timesyn", "systemd-timesyncd",
        "systemd-network", "dbus-daemon", "dbus-broker", "rsyslogd", "syslogd",
        "agetty", "getty", "login", "bash", "sh", "zsh", "dash", "ash", "fish",
        "su", "sudo", "ps", "top", "htop", "polkitd", "irqbalance", "chronyd",
        "ntpd", "udisksd", "packagekitd", "qemu-ga", "snapd"
    };

    private static readonly HashSet<string> KnownServiceProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "nginx", "haproxy", "traefik", "caddy", "envoy", "httpd", "apache2",
        "postgres", "postgresql", "mysqld", "mariadbd", "redis-server", "redis-sentinel",
        "mongod", "mongos", "elasticsearch", "opensearch", "clickhouse-server",
        "rabbitmq-server", "beam.smp", "kafka", "minio", "etcd", "consul", "vault",
        "dockerd", "containerd", "containerd-shim", "containerd-shim-runc-v2", "docker-proxy",
        "nexus", "java", "node", "nodejs", "dotnet", "gunicorn", "uvicorn", "celery",
        "php-fpm", "php-fpm8.2", "php-fpm8.3", "puma", "ruby", "go", "gitea", "gitlab-runner",
        "prometheus", "alertmanager", "grafana", "grafana-server", "victorialogs", "loki", "promtail"
    };

    [GeneratedRegex(@"(?:docker-|cri-containerd-|libpod-|/docker/|/containers/)([a-fA-F0-9]{12,64})",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ContainerIdPattern();

    public static double? ParseUptimeSeconds(string? uptimeOutput)
    {
        if (string.IsNullOrWhiteSpace(uptimeOutput))
        {
            return null;
        }

        var firstToken = uptimeOutput.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return double.TryParse(firstToken, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0
            ? seconds
            : null;
    }

    public static IReadOnlyList<DockerInventoryContainerInfo> ParseDockerContainers(string? dockerPsAllOutput)
    {
        if (string.IsNullOrWhiteSpace(dockerPsAllOutput))
        {
            return [];
        }

        var containers = new List<DockerInventoryContainerInfo>();
        foreach (var rawLine in dockerPsAllOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = rawLine.Split('|', 6);
            if (parts.Length < 5 || string.IsNullOrWhiteSpace(parts[0]))
            {
                continue;
            }

            var id = parts[0].Trim();
            var name = parts[1].Trim().TrimStart('/');
            var image = parts[2].Trim();
            var state = parts[3].Trim().ToLowerInvariant();
            var statusText = parts[4].Trim();
            var ports = parts.Length > 5 && !string.IsNullOrWhiteSpace(parts[5]) ? parts[5].Trim() : null;
            containers.Add(new DockerInventoryContainerInfo(id, name, image, state, statusText, ports));
        }

        return containers
            .OrderBy(item => StateSortOrder(item.State))
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static ProcessDiscoveryResult Parse(
        string? uptimeOutput,
        string? psOutput,
        string? cgroupOutput,
        string? dockerPsAllOutput,
        bool dockerInstalled,
        bool dockerDaemonRunning,
        bool hasExtendedPsColumns = true)
    {
        var uptimeSeconds = ParseUptimeSeconds(uptimeOutput);
        var dockerContainers = ParseDockerContainers(dockerPsAllOutput);
        var containerById = dockerContainers
            .Where(item => item.Id.Length >= 12)
            .ToDictionary(item => item.Id, item => item.Name, StringComparer.OrdinalIgnoreCase);

        var (pidToUnit, pidToContainerId) = ParseCgroupAssociations(cgroupOutput);
        var parsedProcesses = ParsePsLines(psOutput, hasExtendedPsColumns, pidToUnit, pidToContainerId, containerById);
        var capped = SelectTopProcesses(parsedProcesses, MaxStoredProcesses);

        return new ProcessDiscoveryResult(
            uptimeSeconds,
            capped,
            dockerContainers,
            dockerInstalled,
            dockerDaemonRunning,
            parsedProcesses.Count);
    }

    public static IReadOnlyList<int> ExtractTopPids(string? psOutput, bool hasExtendedPsColumns = true, int limit = MaxStoredProcesses)
    {
        if (string.IsNullOrWhiteSpace(psOutput))
        {
            return [];
        }

        var pids = new List<int>();
        foreach (var rawLine in psOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("PID", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var first = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (int.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && pid > 0)
            {
                pids.Add(pid);
                if (pids.Count >= limit)
                {
                    break;
                }
            }
        }

        return pids;
    }

    private static IReadOnlyList<ProcessInfo> ParsePsLines(
        string? psOutput,
        bool hasExtendedPsColumns,
        IReadOnlyDictionary<int, string> pidToUnit,
        IReadOnlyDictionary<int, string> pidToContainerId,
        IReadOnlyDictionary<string, string> containerById)
    {
        if (string.IsNullOrWhiteSpace(psOutput))
        {
            return [];
        }

        var list = new List<ProcessInfo>();
        foreach (var rawLine in psOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("PID", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parsed = hasExtendedPsColumns ? TryParseExtendedLine(line) : TryParseCompactLine(line);
            if (parsed is null)
            {
                continue;
            }

            var redactedArgs = ChatContentRedactor.Redact(parsed.Value.Args);
            var (category, unitName, containerName) = ClassifyProcess(
                parsed.Value.Pid,
                parsed.Value.Name,
                redactedArgs,
                pidToUnit,
                pidToContainerId,
                containerById);

            list.Add(new ProcessInfo(
                parsed.Value.Pid,
                parsed.Value.Ppid,
                parsed.Value.User,
                parsed.Value.State,
                parsed.Value.Cpu,
                parsed.Value.Memory,
                parsed.Value.ElapsedSeconds,
                parsed.Value.Name,
                redactedArgs,
                category,
                unitName,
                containerName));
        }

        return list;
    }

    private static IReadOnlyList<ProcessInfo> SelectTopProcesses(IReadOnlyList<ProcessInfo> processes, int maxCount)
    {
        if (processes.Count <= maxCount)
        {
            return processes
                .OrderByDescending(item => item.Category != ProcessCategory.Os)
                .ThenByDescending(item => item.CpuPercent)
                .ThenByDescending(item => item.MemoryPercent)
                .ThenBy(item => item.Pid)
                .ToArray();
        }

        var prioritized = processes
            .Where(item => item.Category != ProcessCategory.Os)
            .OrderByDescending(item => item.CpuPercent)
            .ThenByDescending(item => item.MemoryPercent)
            .ThenBy(item => item.Pid)
            .Take(maxCount)
            .ToList();

        if (prioritized.Count < maxCount)
        {
            var taken = prioritized.Select(item => item.Pid).ToHashSet();
            prioritized.AddRange(processes
                .Where(item => !taken.Contains(item.Pid))
                .OrderByDescending(item => item.CpuPercent)
                .ThenByDescending(item => item.MemoryPercent)
                .ThenBy(item => item.Pid)
                .Take(maxCount - prioritized.Count));
        }

        return prioritized
            .OrderByDescending(item => item.CpuPercent)
            .ThenByDescending(item => item.MemoryPercent)
            .ThenBy(item => item.Pid)
            .ToArray();
    }

    private static (ProcessCategory Category, string? UnitName, string? ContainerName) ClassifyProcess(
        int pid,
        string name,
        string commandLine,
        IReadOnlyDictionary<int, string> pidToUnit,
        IReadOnlyDictionary<int, string> pidToContainerId,
        IReadOnlyDictionary<string, string> containerById)
    {
        if (pidToContainerId.TryGetValue(pid, out var containerId))
        {
            var matchedName = containerById.TryGetValue(containerId, out var fullMatch)
                ? fullMatch
                : containerById.FirstOrDefault(pair =>
                    pair.Key.StartsWith(containerId, StringComparison.OrdinalIgnoreCase)
                    || containerId.StartsWith(pair.Key, StringComparison.OrdinalIgnoreCase)).Value;
            return (ProcessCategory.Docker, null, matchedName ?? containerId[..Math.Min(12, containerId.Length)]);
        }

        if (pidToUnit.TryGetValue(pid, out var unitName))
        {
            var isOsUnit = OsServiceUnits.Contains(unitName)
                || unitName.StartsWith("getty@", StringComparison.OrdinalIgnoreCase)
                || unitName.StartsWith("serial-getty@", StringComparison.OrdinalIgnoreCase)
                || unitName.StartsWith("user@", StringComparison.OrdinalIgnoreCase)
                || unitName.StartsWith("systemd-", StringComparison.OrdinalIgnoreCase);
            return (isOsUnit ? ProcessCategory.Os : ProcessCategory.Service, unitName, null);
        }

        if (pid <= 2 || (name.StartsWith('[') && name.EndsWith(']')) || OsProcessNames.Contains(name))
        {
            return (ProcessCategory.Os, null, null);
        }

        if (KnownServiceProcessNames.Contains(name)
            || commandLine.Contains("nexus", StringComparison.OrdinalIgnoreCase)
            || commandLine.Contains("nginx", StringComparison.OrdinalIgnoreCase)
            || commandLine.Contains("dockerd", StringComparison.OrdinalIgnoreCase)
            || commandLine.Contains("postgres", StringComparison.OrdinalIgnoreCase)
            || commandLine.Contains("redis", StringComparison.OrdinalIgnoreCase))
        {
            return (ProcessCategory.Service, null, null);
        }

        return (ProcessCategory.Os, null, null);
    }

    private static (IReadOnlyDictionary<int, string> PidToUnit, IReadOnlyDictionary<int, string> PidToContainerId) ParseCgroupAssociations(string? cgroupOutput)
    {
        var systemdMapping = SystemdCollectorParser.ParseCgroupOutput(cgroupOutput);
        var pidToContainerId = new Dictionary<int, string>();
        if (string.IsNullOrWhiteSpace(cgroupOutput))
        {
            return (systemdMapping.PidToUnit, pidToContainerId);
        }

        int? currentPid = null;
        foreach (var rawLine in cgroupOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.StartsWith(SystemdCollectorParser.PidMarkerPrefix, StringComparison.Ordinal))
            {
                var pidText = line[SystemdCollectorParser.PidMarkerPrefix.Length..].Trim();
                currentPid = int.TryParse(pidText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
                    ? parsed
                    : null;
                continue;
            }

            if (currentPid is int pid && !pidToContainerId.ContainsKey(pid))
            {
                var match = ContainerIdPattern().Match(line);
                if (match.Success)
                {
                    pidToContainerId[pid] = match.Groups[1].Value;
                }
            }
        }

        return (systemdMapping.PidToUnit, pidToContainerId);
    }

    private readonly record struct RawPsRow(
        int Pid,
        int Ppid,
        string User,
        string State,
        double Cpu,
        double Memory,
        long ElapsedSeconds,
        string Name,
        string Args);

    private static RawPsRow? TryParseExtendedLine(string line)
    {
        // Format: PID PPID USER STAT %CPU %MEM ELAPSED COMM ARGS...
        var tokens = line.Split((char[]?)null, 9, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 8)
        {
            return null;
        }

        if (!int.TryParse(tokens[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0
            || !int.TryParse(tokens[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ppid))
        {
            return null;
        }

        _ = double.TryParse(tokens[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var cpu);
        _ = double.TryParse(tokens[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var mem);
        _ = long.TryParse(tokens[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var elapsed);
        var name = tokens[7];
        var args = tokens.Length >= 9 ? tokens[8] : name;
        return new RawPsRow(pid, ppid, tokens[2], tokens[3], cpu, mem, Math.Max(0, elapsed), name, args);
    }

    private static RawPsRow? TryParseCompactLine(string line)
    {
        // Fallback BusyBox formats:
        // 1) ps -o pid,ppid,user,stat,comm,args -> PID PPID USER STAT COMM ARGS...
        // 2) default BusyBox ps -> PID USER VSZ STAT COMMAND...
        var tokens = line.Split((char[]?)null, 6, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 4)
        {
            return null;
        }

        if (!int.TryParse(tokens[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0)
        {
            return null;
        }

        if (tokens.Length >= 5 && int.TryParse(tokens[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ppid))
        {
            var name = tokens[4];
            var args = tokens.Length >= 6 ? tokens[5] : name;
            return new RawPsRow(pid, ppid, tokens[2], tokens[3], 0, 0, 0, name, args);
        }

        // Default BusyBox `ps`: PID USER VSZ STAT COMMAND...
        var busyCmd = tokens.Length >= 5 ? tokens[4] + (tokens.Length >= 6 ? " " + tokens[5] : "") : tokens[3];
        var shortName = busyCmd.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? busyCmd;
        shortName = shortName.Contains('/') ? shortName[(shortName.LastIndexOf('/') + 1)..] : shortName;
        return new RawPsRow(pid, 0, tokens[1], tokens.Length >= 4 ? tokens[3] : "S", 0, 0, 0, shortName, busyCmd);
    }

    private static int StateSortOrder(string state) => state switch
    {
        "restarting" => 0,
        "running" => 1,
        "paused" => 2,
        "created" => 3,
        "removing" => 4,
        "exited" => 5,
        "dead" => 6,
        _ => 7
    };
}
