using System.Globalization;
using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;

namespace DevOpsAgent.Infrastructure.Collectors;

public sealed class SystemdCollector : ICollector
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    public string Name => "systemd";

    public static string BuildCgroupCommand(IEnumerable<int> pids)
    {
        var pidList = string.Join(' ', pids.Where(pid => pid > 0).Distinct().Order()
            .Select(pid => pid.ToString(CultureInfo.InvariantCulture)));
        return $"for p in {pidList}; do printf '{SystemdCollectorParser.PidMarkerPrefix}%s\\n' \"$p\"; cat \"/proc/$p/cgroup\" 2>/dev/null || printf '{SystemdCollectorParser.ErrorMarker}\\n'; done";
    }

    public async Task<bool> IsApplicableAsync(ServerContext context, CancellationToken cancellationToken)
    {
        var result = await context.SshRunner.RunAsync(context.Server.Id, "command -v systemctl >/dev/null 2>&1",
            new SshRunOptions(CommandTimeout), cancellationToken);
        return result.ExitCode == 0;
    }

    public async Task<CollectorOutput> CollectAsync(ServerContext context, CancellationToken cancellationToken)
    {
        var listeners = context.TryGetPreviousResult<ListenerDiscoveryResult>("listeners", out var listenerResult)
            ? listenerResult!.Listeners
            : [];
        var listenerPids = listeners.Where(listener => listener.ProcessId is > 0)
            .Select(listener => listener.ProcessId!.Value).ToHashSet();

        var listResult = await context.SshRunner.RunAsync(context.Server.Id,
            "systemctl list-units --type=service --state=running --no-legend --plain",
            new SshRunOptions(CommandTimeout), cancellationToken);
        var errors = new List<string>();
        if (listResult.ExitCode != 0)
        {
            errors.Add(string.IsNullOrWhiteSpace(listResult.StandardError)
                ? $"systemctl list-units exited with code {listResult.ExitCode?.ToString() ?? "unknown"}."
                : listResult.StandardError.Trim());
        }

        var showOutputs = new Dictionary<string, string>(StringComparer.Ordinal);
        string? cgroupOutput = null;
        var duration = listResult.Duration;
        if (listResult.ExitCode == 0 && listenerPids.Count > 0)
        {
            var runningUnits = SystemdCollectorParser.ParseRunningServiceNames(listResult.StandardOutput);
            var runningSet = runningUnits.ToHashSet(StringComparer.Ordinal);

            // T7.R9: map listener PIDs to units via read-only /proc/<pid>/cgroup first so `systemctl show` runs only for
            // listener-owning units; fall back to scanning running services only for PIDs whose /proc entry is restricted.
            var cgroupResult = await context.SshRunner.RunAsync(context.Server.Id,
                BuildCgroupCommand(listenerPids),
                new SshRunOptions(CommandTimeout), cancellationToken);
            duration += cgroupResult.Duration;

            var cgroupMapping = cgroupResult.ExitCode == 0
                ? SystemdCollectorParser.ParseCgroupOutput(cgroupResult.StandardOutput)
                : new CgroupPidMapping(new Dictionary<int, string>(), listenerPids, new HashSet<int>());
            if (cgroupResult.ExitCode == 0 && !string.IsNullOrWhiteSpace(cgroupResult.StandardOutput))
            {
                cgroupOutput = cgroupResult.StandardOutput;
            }

            var pidsByUnit = cgroupMapping.PidToUnit
                .Where(pair => listenerPids.Contains(pair.Key))
                .GroupBy(pair => pair.Value, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => (IReadOnlyCollection<int>)group.Select(pair => pair.Key).ToArray(), StringComparer.Ordinal);

            var queriedUnits = new HashSet<string>(StringComparer.Ordinal);
            var matchedPids = new HashSet<int>();

            // Query only listener-owning units discovered through /proc/<pid>/cgroup (in running-list order first).
            var mappedUnits = runningUnits.Where(pidsByUnit.ContainsKey)
                .Concat(pidsByUnit.Keys.Where(unit => !runningSet.Contains(unit)))
                .Distinct(StringComparer.Ordinal);

            foreach (var unitName in mappedUnits)
            {
                queriedUnits.Add(unitName);
                var showResult = await QueryUnitAsync(context, unitName, cancellationToken);
                duration += showResult.Duration;
                if (showResult.ExitCode == 0)
                {
                    if (SystemdCollectorParser.ParseUnit(unitName, showResult.StandardOutput, listenerPids,
                            pidsByUnit.GetValueOrDefault(unitName)) is { } parsed)
                    {
                        showOutputs[unitName] = showResult.StandardOutput;
                        foreach (var pid in parsed.ListenerPids) matchedPids.Add(pid);
                    }
                }
                else
                {
                    errors.Add(FormatShowError(unitName, showResult));
                }
            }

            // Fallback when /proc/<pid>/cgroup is restricted (e.g. hidepid=2) or did not return an entry for some listener PIDs.
            var unresolvedRestrictedPids = listenerPids
                .Where(pid => !matchedPids.Contains(pid)
                    && (cgroupMapping.RestrictedPids.Contains(pid) || !cgroupMapping.ObservedPids.Contains(pid)))
                .ToHashSet();

            if (unresolvedRestrictedPids.Count > 0)
            {
                foreach (var unitName in runningUnits)
                {
                    if (unresolvedRestrictedPids.Count == 0) break;
                    if (!queriedUnits.Add(unitName)) continue;

                    var showResult = await QueryUnitAsync(context, unitName, cancellationToken);
                    duration += showResult.Duration;
                    if (showResult.ExitCode == 0)
                    {
                        if (SystemdCollectorParser.ParseUnit(unitName, showResult.StandardOutput, listenerPids) is { } parsed)
                        {
                            showOutputs[unitName] = showResult.StandardOutput;
                            foreach (var pid in parsed.ListenerPids) unresolvedRestrictedPids.Remove(pid);
                        }
                    }
                    else
                    {
                        errors.Add(FormatShowError(unitName, showResult));
                    }
                }
            }
        }

        var raw = JsonSerializer.Serialize(new SystemdCollectorRawOutput(
            listResult.StandardOutput, showOutputs, listenerPids.Order().ToArray(), cgroupOutput));
        return new CollectorOutput(raw, string.Join(Environment.NewLine, errors), errors.Count == 0 ? 0 : 1, duration);
    }

    public CollectorResult Parse(CollectorOutput output)
    {
        try
        {
            var raw = JsonSerializer.Deserialize<SystemdCollectorRawOutput>(output.StandardOutput);
            return raw is null
                ? new CollectorResult(Error: "systemd collector output is empty.")
                : new CollectorResult(JsonSerializer.SerializeToElement(SystemdCollectorParser.Parse(
                    raw.RunningUnitsOutput, raw.UnitShowOutputs, raw.ListenerPids.ToHashSet(), raw.CgroupOutput)));
        }
        catch (JsonException exception)
        {
            return new CollectorResult(Error: $"Invalid systemd collector output: {exception.Message}");
        }
    }

    private static Task<RemoteCommandResult> QueryUnitAsync(ServerContext context, string unitName, CancellationToken cancellationToken) =>
        context.SshRunner.RunAsync(context.Server.Id,
            $"systemctl show -p MainPID,ExecStart,FragmentPath {ShellQuote(unitName)}",
            new SshRunOptions(CommandTimeout), cancellationToken);

    private static string FormatShowError(string unitName, RemoteCommandResult showResult) =>
        string.IsNullOrWhiteSpace(showResult.StandardError)
            ? $"systemctl show {unitName} exited with code {showResult.ExitCode?.ToString() ?? "unknown"}."
            : showResult.StandardError.Trim();

    private static string ShellQuote(string value) => $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";

    private sealed record SystemdCollectorRawOutput(
        string RunningUnitsOutput,
        IReadOnlyDictionary<string, string> UnitShowOutputs,
        int[] ListenerPids,
        string? CgroupOutput = null);
}
