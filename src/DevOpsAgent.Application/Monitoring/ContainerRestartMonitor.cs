using System.Globalization;
using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Domain.Monitoring;

namespace DevOpsAgent.Application.Monitoring;

/// <summary>One container as reported by the read-only status query in the metric cycle.</summary>
public sealed record ContainerStatus(string Id, string Name, string State, int RestartCount);

/// <summary>Read-only Docker container status for the restart-loop rule (T7.R3, MON-2).</summary>
public static class ContainerStatusQuery
{
    public const string NoDockerMarker = "__DVA_NO_DOCKER__";

    /// <summary>Lists every container with its state and restart count; prints a marker when Docker is not installed.</summary>
    public const string Command =
        "if command -v docker >/dev/null 2>&1; then ids=$(docker ps -aq --no-trunc) || exit $?; "
        + "if [ -n \"$ids\" ]; then docker inspect -f '{{.Id}}|{{.Name}}|{{.State.Status}}|{{.RestartCount}}' $ids; fi; "
        + "else printf '%s\\n' '" + NoDockerMarker + "'; fi";

    /// <returns>Null when Docker is not installed on the server; otherwise its containers.</returns>
    public static IReadOnlyList<ContainerStatus>? Parse(string output)
    {
        if (output.Contains(NoDockerMarker, StringComparison.Ordinal)) return null;
        var containers = new List<ContainerStatus>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('|');
            if (parts.Length != 4 || parts[0].Length == 0
                || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var restarts)) continue;
            containers.Add(new ContainerStatus(parts[0], parts[1].TrimStart('/'), parts[2], restarts));
        }

        return containers;
    }
}

/// <summary>
/// Restart-loop rule: a container that is "restarting", or whose restart count rose by 3 or more within 10 minutes, raises a
/// medium alert. It resolves once 10 minutes pass without another restart, or when the container is removed. The metric
/// cycle records each server's container list; a recreated container (new ID, counter reset) is not counted as a restart.
/// </summary>
public sealed class ContainerRestartMonitor(IAppSettingRepository settings, TimeProvider timeProvider)
{
    public const string Rule = "container-restart-loop";
    public const int RestartThreshold = 3;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(30);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <param name="containers">This cycle's containers, or null when Docker is not installed (history is cleared).</param>
    public async Task RecordAsync(Guid serverId, IReadOnlyList<ContainerStatus>? containers, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var history = containers is null ? [] : (await ReadAsync(serverId, cancellationToken))
            .Where(cycle => cycle.AtUtc >= now - Retention)
            .Append(new ContainerCycle(now, containers))
            .ToList();
        await settings.SetAsync(Key(serverId), JsonSerializer.Serialize(history, JsonOptions), cancellationToken);
    }

    public async Task<IReadOnlyList<AlertCondition>> GetConditionsAsync(Guid serverId, string serverName, CancellationToken cancellationToken) =>
        Evaluate(await ReadAsync(serverId, cancellationToken), timeProvider.GetUtcNow())
            .Select(loop => new AlertCondition($"{Rule}:{serverId:N}:{loop.Name}", Rule, AlertSeverity.Medium, serverId, loop.Name,
                $"Container {loop.Name} is restart-looping",
                $"{serverName}: {loop.RestartsInWindow} restart(s) in the last 10 minutes; current state {loop.State}; restart count {loop.RestartCount}."))
            .ToArray();

    public static IReadOnlyList<RestartLoop> Evaluate(IReadOnlyList<ContainerCycle> history, DateTimeOffset now)
    {
        var cycles = history.OrderBy(cycle => cycle.AtUtc).ToArray();
        if (cycles.Length == 0) return [];
        var latest = cycles[^1];
        // A server that has not been sampled recently (for example unreachable) cannot keep an alert open on old data.
        if (latest.AtUtc < now - Window) return [];

        var result = new List<RestartLoop>();
        foreach (var current in latest.Containers)
        {
            // Restart activity per cycle: the counter increase on the same container (a recreated container has a new ID
            // and a reset counter, which is not a restart), or the container being caught in the "restarting" state in two
            // consecutive cycles (one sample can catch the short pause after a single crash).
            var activity = new List<(DateTimeOffset At, int Restarts, bool Restarting)>();
            ContainerStatus? previous = null;
            foreach (var cycle in cycles)
            {
                var status = cycle.Containers.FirstOrDefault(item => item.Name == current.Name);
                if (status is null) { previous = null; continue; }
                var sameContainer = previous is not null && previous.Id == status.Id;
                var increase = sameContainer ? Math.Max(0, status.RestartCount - previous!.RestartCount) : 0;
                var restarting = sameContainer && IsRestarting(status) && IsRestarting(previous!);
                if (increase > 0 || restarting) activity.Add((cycle.AtUtc, increase, restarting));
                previous = status;
            }

            // Only activity inside the window can raise or keep the alert: an old loop must not make one new restart look like one.
            var triggered = activity.Any(item => item.At > now - Window
                && (item.Restarting || activity.Where(other => other.At > item.At - Window && other.At <= item.At).Sum(other => other.Restarts) >= RestartThreshold));
            if (!triggered) continue;

            result.Add(new RestartLoop(current.Name, current.State, current.RestartCount,
                activity.Where(item => item.At > now - Window).Sum(item => item.Restarts)));
        }

        return result;
    }

    private static bool IsRestarting(ContainerStatus status) => string.Equals(status.State, "restarting", StringComparison.OrdinalIgnoreCase);

    private async Task<IReadOnlyList<ContainerCycle>> ReadAsync(Guid serverId, CancellationToken cancellationToken)
    {
        var json = await settings.GetAsync(Key(serverId), cancellationToken);
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<ContainerCycle>>(json, JsonOptions) ?? []; }
        catch (JsonException) { return []; }
    }

    private static string Key(Guid serverId) => $"monitoring.containers.{serverId:N}";
}

public sealed record ContainerCycle(DateTimeOffset AtUtc, IReadOnlyList<ContainerStatus> Containers);

public sealed record RestartLoop(string Name, string State, int RestartCount, int RestartsInWindow);
