using System.Globalization;
using DevOpsAgent.Application.Discovery;

namespace DevOpsAgent.Infrastructure.Collectors;

public sealed record CgroupPidMapping(
    IReadOnlyDictionary<int, string> PidToUnit,
    IReadOnlySet<int> RestrictedPids,
    IReadOnlySet<int> ObservedPids);

public static class SystemdCollectorParser
{
    public const string PidMarkerPrefix = "__PID__:";
    public const string ErrorMarker = "__ERR__";

    public static IReadOnlyList<string> ParseRunningServiceNames(string output)
    {
        var services = new List<string>();
        foreach (var line in output.Split('\n'))
        {
            var columns = line.Split((char[]?)null, 6, StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length >= 5 && columns[0].EndsWith(".service", StringComparison.Ordinal))
            {
                services.Add(columns[0]);
            }
        }

        return services.Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Parses batched <c>/proc/&lt;pid&gt;/cgroup</c> probe output into listener-PID → systemd service unit mappings,
    /// recording PIDs whose <c>/proc</c> entry was unreadable so the collector can fall back when <c>hidepid</c> is active.
    /// </summary>
    public static CgroupPidMapping ParseCgroupOutput(string? output)
    {
        var pidToUnit = new Dictionary<int, string>();
        var restricted = new HashSet<int>();
        var observed = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return new CgroupPidMapping(pidToUnit, restricted, observed);
        }

        int? currentPid = null;
        var currentLines = new List<string>();

        void Flush()
        {
            if (currentPid is not int pid) return;
            observed.Add(pid);
            if (currentLines.Count == 0 || currentLines.Any(line => line == ErrorMarker))
            {
                restricted.Add(pid);
            }
            else if (ExtractServiceUnitFromCgroupLines(currentLines) is { } unitName)
            {
                pidToUnit[pid] = unitName;
            }

            currentLines.Clear();
        }

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith(PidMarkerPrefix, StringComparison.Ordinal))
            {
                Flush();
                var pidText = line[PidMarkerPrefix.Length..].Trim();
                currentPid = int.TryParse(pidText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedPid) && parsedPid > 0
                    ? parsedPid
                    : null;
                continue;
            }

            if (currentPid is not null)
            {
                currentLines.Add(line);
            }
        }

        Flush();
        return new CgroupPidMapping(pidToUnit, restricted, observed);
    }

    public static string? ExtractServiceUnitFromCgroupLines(IReadOnlyList<string> lines)
    {
        string? fallbackMatch = null;
        foreach (var line in lines)
        {
            var firstColon = line.IndexOf(':');
            if (firstColon < 0) continue;
            var secondColon = line.IndexOf(':', firstColon + 1);
            if (secondColon < 0) continue;

            var hierarchy = line[..firstColon];
            var controllers = line[(firstColon + 1)..secondColon];
            var path = line[(secondColon + 1)..];
            var unit = ExtractServiceUnitFromPath(path);
            if (unit is null) continue;

            // Prefer cgroup v2 unified hierarchy ("0::/...") or cgroup v1 systemd hierarchy ("N:name=systemd:/...").
            if ((hierarchy == "0" && controllers.Length == 0)
                || controllers.Contains("name=systemd", StringComparison.Ordinal)
                || string.Equals(controllers, "systemd", StringComparison.Ordinal))
            {
                return unit;
            }

            fallbackMatch ??= unit;
        }

        return fallbackMatch;
    }

    private static string? ExtractServiceUnitFromPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return null;

        // Processes running inside containers (Docker/containerd/Podman/Kubernetes/LXC) belong to the container, not a host unit.
        foreach (var segment in segments)
        {
            if (segment.StartsWith("docker-", StringComparison.OrdinalIgnoreCase)
                || segment.StartsWith("cri-containerd-", StringComparison.OrdinalIgnoreCase)
                || segment.StartsWith("libpod-", StringComparison.OrdinalIgnoreCase)
                || segment.StartsWith("kubepods", StringComparison.OrdinalIgnoreCase)
                || segment.StartsWith("lxc.payload", StringComparison.OrdinalIgnoreCase)
                || string.Equals(segment, "docker", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        for (var index = segments.Length - 1; index >= 0; index--)
        {
            if (segments[index].EndsWith(".service", StringComparison.Ordinal))
            {
                return segments[index];
            }
        }

        return null;
    }

    public static SystemdUnitInfo? ParseUnit(
        string name,
        string output,
        IReadOnlySet<int> listenerPids,
        IReadOnlyCollection<int>? cgroupListenerPids = null)
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n'))
        {
            var separator = line.IndexOf('=');
            if (separator > 0)
            {
                properties[line[..separator]] = line[(separator + 1)..].TrimEnd('\r');
            }
        }

        var mainPid = properties.TryGetValue("MainPID", out var mainPidText)
            && int.TryParse(mainPidText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedMainPid)
            && parsedMainPid > 0
                ? parsedMainPid
                : 0;

        var owningPids = new HashSet<int>();
        if (cgroupListenerPids is not null)
        {
            foreach (var pid in cgroupListenerPids)
            {
                if (pid > 0 && listenerPids.Contains(pid))
                {
                    owningPids.Add(pid);
                }
            }
        }

        if (mainPid > 0 && listenerPids.Contains(mainPid))
        {
            owningPids.Add(mainPid);
        }

        if (owningPids.Count == 0)
        {
            return null;
        }

        return new SystemdUnitInfo(
            name,
            mainPid,
            properties.GetValueOrDefault("ExecStart"),
            properties.GetValueOrDefault("FragmentPath"),
            owningPids.Order().ToArray());
    }

    public static SystemdDiscoveryResult Parse(
        string runningUnitsOutput,
        IReadOnlyDictionary<string, string> unitShowOutputs,
        IReadOnlySet<int> listenerPids,
        string? cgroupOutput = null)
    {
        var mapping = ParseCgroupOutput(cgroupOutput);
        var pidsByUnit = mapping.PidToUnit
            .GroupBy(pair => pair.Value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlyCollection<int>)group.Select(pair => pair.Key).ToArray(), StringComparer.Ordinal);

        var orderedNames = ParseRunningServiceNames(runningUnitsOutput)
            .Concat(unitShowOutputs.Keys)
            .Distinct(StringComparer.Ordinal);

        var units = orderedNames
            .Where(unitShowOutputs.ContainsKey)
            .Select(name => ParseUnit(name, unitShowOutputs[name], listenerPids, pidsByUnit.GetValueOrDefault(name)))
            .Where(unit => unit is not null)
            .Select(unit => unit!)
            .ToArray();
        return new SystemdDiscoveryResult(units);
    }
}
