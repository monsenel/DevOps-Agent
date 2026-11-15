using System.Globalization;
using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Domain.Monitoring;
using Microsoft.Extensions.Logging;

namespace DevOpsAgent.Application.Monitoring;

/// <summary>One container as reported by the read-only status query in the metric cycle.</summary>
public sealed record ContainerStatus(string Id, string Name, string State, int RestartCount);

/// <summary>Read-only Docker container status for the restart-loop rule (T7.R3, MON-2).</summary>
public static class ContainerStatusQuery
{
    public const string NoDockerMarker = "__DVA_NO_DOCKER__";

    /// <summary>
    /// Lists every container with its state and restart count; prints a marker when Docker is not installed.
    /// T7.R18: a container removed between <c>ps</c> and <c>inspect</c> makes <c>inspect</c> exit 1 although it printed the
    /// others. That is not a failure (exit 0, the vanished container is simply missing, and no sudo retry follows). The exit
    /// code of <c>inspect</c> survives as soon as it printed anything that is neither a container line nor a complaint about
    /// a missing object (a daemon that dies half-way prints the containers it got to and then an error): a sample that is
    /// missing containers for that reason must not look complete, or the alerts of those containers would resolve.
    /// </summary>
    public const string Command =
        "if command -v docker >/dev/null 2>&1; then ids=$(docker ps -aq --no-trunc) || exit $?; "
        + "if [ -n \"$ids\" ]; then out=$(docker inspect -f '{{.Id}}|{{.Name}}|{{.State.Status}}|{{.RestartCount}}' $ids 2>&1); rc=$?; "
        + "printf '%s\\n' \"$out\"; "
        + "if [ $rc -ne 0 ] && printf '%s\\n' \"$out\" | grep -Eqv '^$|^[^|]+[|][^|]*[|][^|]*[|][0-9]+$|No such (object|container)'; then exit $rc; fi; fi; "
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
/// Container rules fed by the per-server container history that the metric cycle records.
/// <list type="bullet">
/// <item>Restart loop (T7.R3, D-034, D-035): a container caught "restarting" in two consecutive samples, or whose restart
/// count rose by 3 or more within 10 minutes, raises a medium alert. It resolves once 10 minutes pass without another
/// restart, or when the container is removed. A recreated container (new ID, counter reset) is not counted as a restart.</item>
/// <item>Container states (T7.R18, D-038): <c>paused</c> raises a low alert; <c>created</c> and <c>removing</c> raise a
/// medium alert only after the same container has been in that state for more than 10 minutes, measured from the stored
/// samples. The alert ends with the state.</item>
/// </list>
/// With no fresh sample (server unreachable or in backoff, container query failing) the picture is held as of the last
/// sample, so nothing opens, ages or resolves from stale data (T7.R18).
/// </summary>
public sealed class ContainerRestartMonitor(IAppSettingRepository settings, TimeProvider timeProvider, ILogger<ContainerRestartMonitor> logger)
{
    public const string Rule = "container-restart-loop";
    public const string StateRule = "container-state";
    public const int RestartThreshold = 3;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    /// <summary>A <c>created</c> or <c>removing</c> container alerts only after this long in that state.</summary>
    public static readonly TimeSpan StuckStateAfter = TimeSpan.FromMinutes(10);

    /// <summary>Samples come about once a minute; a latest sample older than this means the server is not being sampled.</summary>
    public static readonly TimeSpan MaxSampleAge = TimeSpan.FromMinutes(3);

    /// <summary>Stored history older than this is dropped whenever it is read or written.</summary>
    public static readonly TimeSpan HistoryLimit = TimeSpan.FromHours(24);

    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(30);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <param name="containers">This cycle's containers, or null when Docker is not installed (history is cleared).</param>
    public async Task RecordAsync(Guid serverId, IReadOnlyList<ContainerStatus>? containers, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var history = containers is null ? [] : Retain(await ReadAsync(serverId, cancellationToken), now)
            .Append(new ContainerCycle(now, containers))
            .ToList();
        await settings.SetAsync(Key(serverId), JsonSerializer.Serialize(history, JsonOptions), cancellationToken);
    }

    /// <summary>
    /// Keeps the last 30 minutes of sampled time, and never more than <see cref="HistoryLimit"/> of wall-clock time. Time
    /// in which the server was not sampled (gaps longer than <see cref="MaxSampleAge"/>) does not age the history, so the
    /// samples from before an outage are still there to be compared with the first ones after it.
    /// </summary>
    private static List<ContainerCycle> Retain(IReadOnlyList<ContainerCycle> history, DateTimeOffset now)
    {
        var kept = new List<ContainerCycle>();
        var newer = now;
        var sampled = TimeSpan.Zero;
        foreach (var cycle in history.OrderByDescending(item => item.AtUtc))
        {
            if (cycle.AtUtc < now - HistoryLimit) break;
            var step = newer - cycle.AtUtc;
            if (step <= MaxSampleAge) sampled += step;
            if (sampled > Retention) break;
            kept.Add(cycle);
            newer = cycle.AtUtc;
        }

        kept.Reverse();
        return kept;
    }

    public async Task<IReadOnlyList<AlertCondition>> GetConditionsAsync(Guid serverId, string serverName, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var history = await ReadAndPruneAsync(serverId, now, cancellationToken);
        var conditions = new List<AlertCondition>();
        foreach (var loop in Evaluate(history, now))
            conditions.Add(new AlertCondition($"{Rule}:{serverId:N}:{loop.Name}", Rule, AlertSeverity.Medium, serverId, loop.Name,
                $"Container {loop.Name} is restart-looping",
                $"{serverName}: {loop.RestartsInWindow} restart(s) in the last 10 minutes; current state {loop.State}; restart count {loop.RestartCount}."));
        foreach (var issue in EvaluateStates(history))
        {
            var severity = issue.State == PausedState ? AlertSeverity.Low : AlertSeverity.Medium;
            var details = issue.State == PausedState
                ? $"{serverName}: Docker reports this container as paused."
                : $"{serverName}: Docker has reported this container as {issue.State} for {(int)issue.Duration.TotalMinutes} minutes.";
            conditions.Add(new AlertCondition($"{StateRule}:{serverId:N}:{issue.Name}:{issue.State}", StateRule, severity, serverId,
                issue.Name, $"Container {issue.Name} is {issue.State}", details));
        }

        return conditions;
    }

    public static IReadOnlyList<RestartLoop> Evaluate(IReadOnlyList<ContainerCycle> history, DateTimeOffset now)
    {
        var cycles = history.OrderBy(cycle => cycle.AtUtc).ToArray();
        if (cycles.Length == 0) return [];
        var latest = cycles[^1];
        // No fresh sample (the server is unreachable or in backoff, or its container query keeps failing): evaluate as of the
        // last sample. An open alert stays open instead of resolving, and nothing new opens, until a fresh sample arrives.
        var asOf = now - latest.AtUtc > MaxSampleAge ? latest.AtUtc : now;

        var result = new List<RestartLoop>();
        foreach (var current in latest.Containers)
        {
            // Restart activity per cycle: the counter increase on the same container (a recreated container has a new ID
            // and a reset counter, which is not a restart), or the container being caught in the "restarting" state in two
            // consecutive cycles (one sample can catch the short pause after a single crash).
            var activity = new List<(DateTimeOffset At, int Restarts, bool Restarting)>();
            ContainerStatus? previous = null;
            DateTimeOffset? previousCycleAt = null;
            foreach (var cycle in cycles)
            {
                var status = cycle.Containers.FirstOrDefault(item => item.Name == current.Name);
                var afterGap = previousCycleAt is { } before && cycle.AtUtc - before > MaxSampleAge;
                var gapStart = previousCycleAt;
                previousCycleAt = cycle.AtUtc;
                if (status is null) { previous = null; continue; }
                var sameContainer = previous is not null && previous.Id == status.Id;
                var increase = sameContainer ? Math.Max(0, status.RestartCount - previous!.RestartCount) : 0;
                var restarting = sameContainer && IsRestarting(status) && IsRestarting(previous!);
                // The server was not sampled for a while (outage, app stopped). A loop that was open before the gap stays
                // open until 10 quiet minutes have been seen in fresh samples after it, not until the old restarts age out.
                var carried = afterGap && sameContainer && IsLoop(activity, gapStart!.Value);
                if (increase > 0 || restarting || carried) activity.Add((cycle.AtUtc, increase, restarting || carried));
                previous = status;
            }

            // Only activity inside the window can raise or keep the alert: an old loop must not make one new restart look like one.
            if (!IsLoop(activity, asOf)) continue;

            result.Add(new RestartLoop(current.Name, current.State, current.RestartCount,
                activity.Where(item => item.At > asOf - Window).Sum(item => item.Restarts)));
        }

        return result;
    }

    /// <summary>Containers in the latest sample whose state raises the T7.R18 alerts (paused, or created/removing for long enough).</summary>
    public static IReadOnlyList<ContainerStateIssue> EvaluateStates(IReadOnlyList<ContainerCycle> history)
    {
        var cycles = history.OrderBy(cycle => cycle.AtUtc).ToArray();
        if (cycles.Length == 0) return [];
        var latest = cycles[^1];
        var result = new List<ContainerStateIssue>();
        foreach (var container in latest.Containers)
        {
            var state = container.State.ToLowerInvariant();
            if (state == PausedState)
            {
                result.Add(new ContainerStateIssue(container.Name, state, TimeSpan.Zero));
                continue;
            }

            if (state is not (CreatedState or RemovingState)) continue;
            // First-seen time of this state: walk back through the stored samples while the same container (same ID) is in it.
            var since = latest.AtUtc;
            for (var index = cycles.Length - 2; index >= 0; index--)
            {
                var earlier = cycles[index].Containers.FirstOrDefault(item => item.Id == container.Id);
                if (earlier is null || !string.Equals(earlier.State, container.State, StringComparison.OrdinalIgnoreCase)) break;
                since = cycles[index].AtUtc;
            }

            var duration = latest.AtUtc - since;
            if (duration > StuckStateAfter) result.Add(new ContainerStateIssue(container.Name, state, duration));
        }

        return result;
    }

    private const string PausedState = "paused";
    private const string CreatedState = "created";
    private const string RemovingState = "removing";

    private static bool IsLoop(List<(DateTimeOffset At, int Restarts, bool Restarting)> activity, DateTimeOffset asOf) =>
        activity.Any(item => item.At > asOf - Window
            && (item.Restarting || activity.Where(other => other.At > item.At - Window && other.At <= item.At).Sum(other => other.Restarts) >= RestartThreshold));

    private static bool IsRestarting(ContainerStatus status) => string.Equals(status.State, "restarting", StringComparison.OrdinalIgnoreCase);

    private async Task<IReadOnlyList<ContainerCycle>> ReadAndPruneAsync(Guid serverId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var history = await ReadAsync(serverId, cancellationToken);
        var kept = history.Where(cycle => cycle.AtUtc >= now - HistoryLimit).ToList();
        // A server that is no longer sampled never rewrites its history; this keeps it from living forever.
        if (kept.Count != history.Count)
            await settings.SetAsync(Key(serverId), JsonSerializer.Serialize(kept, JsonOptions), cancellationToken);
        return kept;
    }

    private async Task<IReadOnlyList<ContainerCycle>> ReadAsync(Guid serverId, CancellationToken cancellationToken)
    {
        var json = await settings.GetAsync(Key(serverId), cancellationToken);
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            var history = JsonSerializer.Deserialize<List<ContainerCycle>>(json, JsonOptions) ?? [];
            if (history.Any(cycle => cycle is null || cycle.Containers is null || cycle.Containers.Any(container => container is null)))
                throw new JsonException("The stored history has null entries.");
            return history;
        }
        catch (JsonException exception)
        {
            // Never log the stored text. The unreadable value is replaced by an empty history, so it is reported once.
            logger.LogWarning("Stored container history of server {ServerId} cannot be parsed ({ExceptionType}, {Length} characters); using an empty history.",
                serverId, exception.GetType().Name, json.Length);
            await settings.SetAsync(Key(serverId), "[]", cancellationToken);
            return [];
        }
    }

    private static string Key(Guid serverId) => $"monitoring.containers.{serverId:N}";
}

public sealed record ContainerCycle(DateTimeOffset AtUtc, IReadOnlyList<ContainerStatus> Containers);

public sealed record RestartLoop(string Name, string State, int RestartCount, int RestartsInWindow);

public sealed record ContainerStateIssue(string Name, string State, TimeSpan Duration);
