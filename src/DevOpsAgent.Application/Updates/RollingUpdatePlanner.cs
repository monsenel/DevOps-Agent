using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Agent;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Domain.Agent;
using DevOpsAgent.Domain.Graph;

namespace DevOpsAgent.Application.Updates;

public sealed record RollingServerInput(
    Guid ServerId,
    string ServerName,
    string Group,
    int PendingUpdateCount,
    int SecurityUpdateCount,
    bool ReadOnly,
    IReadOnlyList<string> HealthCheckNames);

public sealed record RollingWaveServer(
    Guid ServerId,
    string ServerName,
    string Group,
    int PendingUpdateCount,
    int SecurityUpdateCount,
    bool ReadOnly,
    string TierRole,
    IReadOnlyList<string> HealthCheckNames);

public sealed record RollingVerificationGate(string Summary, IReadOnlyList<string> Checks);

public sealed record RollingUpdateWave(
    int WaveNumber,
    string Label,
    IReadOnlyList<RollingWaveServer> Servers,
    RollingVerificationGate VerificationGate);

public sealed record RollingUpdatePlan(
    Guid? SnapshotId,
    bool BasedOnDiscoveryGraph,
    bool HasCycleWarning,
    IReadOnlyList<RollingUpdateWave> Waves)
{
    public int TotalServers => Waves.Sum(wave => wave.Servers.Count);
}

/// <summary>
/// Computes safe topological update waves from the latest discovery graph:
/// leaf / non-proxied backend servers first, then intermediate application tiers, and reverse proxies / edge servers last,
/// grouping independent servers in the same wave with health-check verification gates between waves.
/// </summary>
public sealed class RollingUpdatePlanner(
    IServerRepository servers,
    ISnapshotRepository snapshots,
    IGraphRepository graphs,
    IUpdatePackageRepository updatePackages,
    IHealthCheckRepository healthChecks,
    ExecutionModeService executionModes)
{
    public async Task<RollingUpdatePlan> BuildPlanAsync(
        CancellationToken cancellationToken,
        bool onlyServersWithPendingUpdates = false)
    {
        var serverList = await servers.ListAsync(cancellationToken);
        if (serverList.Count == 0)
            return new RollingUpdatePlan(null, false, false, []);

        var packages = await updatePackages.ListAsync(cancellationToken);
        var packagesByServer = packages.ToLookup(pkg => pkg.ServerId);

        var inputs = new List<RollingServerInput>(serverList.Count);
        foreach (var server in serverList.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            var serverPackages = packagesByServer[server.Id].ToArray();
            if (onlyServersWithPendingUpdates && serverPackages.Length == 0) continue;

            var policy = await executionModes.GetForGroupAsync(server.Group, cancellationToken);
            var checks = await healthChecks.ListAsync(server.Id, cancellationToken);
            inputs.Add(new RollingServerInput(
                server.Id,
                server.Name,
                server.Group,
                serverPackages.Length,
                serverPackages.Count(pkg => pkg.IsSecurity),
                policy.Mode == ExecutionMode.ReadOnly,
                checks.Select(c => $"{c.Name} ({c.Type})").OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray()));
        }

        if (inputs.Count == 0)
            return new RollingUpdatePlan(null, false, false, []);

        var latestSnapshot = await snapshots.GetLatestAsync(cancellationToken);
        if (latestSnapshot is null)
            return Plan(inputs, [], new HashSet<Guid>(), snapshotId: null, basedOnGraph: false);

        var entities = await graphs.GetEntitiesAsync(latestSnapshot.Id, cancellationToken);
        if (entities is null)
            return Plan(inputs, [], new HashSet<Guid>(), snapshotId: latestSnapshot.Id, basedOnGraph: false);

        var (serverEdges, edgeProxyServerIds) = ExtractServerTopology(entities);
        return Plan(inputs, serverEdges, edgeProxyServerIds, latestSnapshot.Id, basedOnGraph: true);
    }

    /// <summary>
    /// Extracts cross-server dependency edges (caller/proxy -&gt; backend/target) and identifies edge/proxy servers
    /// receiving traffic from <see cref="GraphNodeKind.Domain"/> nodes or running reverse proxies.
    /// </summary>
    public static (IReadOnlyList<(Guid FromServerId, Guid ToServerId)> ServerEdges, HashSet<Guid> EdgeProxyServerIds)
        ExtractServerTopology(GraphBuildResult graph)
    {
        var nodeById = graph.Nodes.ToDictionary(n => n.Id);
        var edgeSet = new HashSet<(Guid From, Guid To)>();
        var edgeProxyServers = new HashSet<Guid>();

        Guid? ResolveServerId(GraphNode node)
        {
            if (node.ServerId is Guid sid) return sid;
            var current = node;
            var guard = 0;
            while (current.ParentNodeId is Guid pid && nodeById.TryGetValue(pid, out var parent) && guard++ < 8)
            {
                if (parent.ServerId is Guid parentSid) return parentSid;
                current = parent;
            }

            return null;
        }

        foreach (var edge in graph.Edges)
        {
            if (edge.Kind == GraphEdgeKind.NetworkAttachment) continue;
            if (!nodeById.TryGetValue(edge.SourceNodeId, out var source)) continue;
            if (!nodeById.TryGetValue(edge.TargetNodeId, out var target)) continue;

            var targetServer = ResolveServerId(target);
            if (source.Kind == GraphNodeKind.Domain && targetServer is Guid ingressServer)
            {
                edgeProxyServers.Add(ingressServer);
                continue;
            }

            var sourceServer = ResolveServerId(source);
            if (sourceServer is Guid from && targetServer is Guid to && from != to)
            {
                edgeSet.Add((from, to));
                if (edge.Kind == GraphEdgeKind.Declared)
                {
                    edgeProxyServers.Add(from);
                }
            }
        }

        return (edgeSet.ToArray(), edgeProxyServers);
    }

    /// <summary>
    /// Pure deterministic topological wave planner.
    /// An edge <c>(FromServerId -&gt; ToServerId)</c> means <c>FromServerId</c> sends traffic to or depends on <c>ToServerId</c>,
    /// so <c>ToServerId</c> (leaf/backend) is patched in an earlier wave than <c>FromServerId</c> (app/reverse proxy).
    /// </summary>
    public static RollingUpdatePlan Plan(
        IReadOnlyList<RollingServerInput> servers,
        IReadOnlyList<(Guid FromServerId, Guid ToServerId)> serverEdges,
        IReadOnlySet<Guid> edgeProxyServerIds,
        Guid? snapshotId = null,
        bool basedOnGraph = true)
    {
        if (servers.Count == 0)
            return new RollingUpdatePlan(snapshotId, basedOnGraph, false, []);

        var byId = servers.ToDictionary(s => s.ServerId);
        var serverIds = byId.Keys.ToHashSet();

        // dependencyMap[caller] = set of servers that caller depends on (must be patched BEFORE caller).
        var dependencies = serverIds.ToDictionary(id => id, _ => new HashSet<Guid>());
        // callers[backend] = set of servers that depend on backend.
        var callers = serverIds.ToDictionary(id => id, _ => new HashSet<Guid>());

        foreach (var (from, to) in serverEdges)
        {
            if (from == to || !serverIds.Contains(from) || !serverIds.Contains(to)) continue;
            dependencies[from].Add(to);
            callers[to].Add(from);
        }

        var remaining = new HashSet<Guid>(serverIds);
        var remainingDeps = dependencies.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Count);
        var rawWaves = new List<List<Guid>>();
        var hasCycle = false;

        // Topological wave layering (Kahn's algorithm): servers with 0 unpatched dependencies run together in the current wave.
        // If there are both non-edge leaves and edge-proxy leaves with 0 dependencies, put non-edge leaves before edge proxies
        // so reverse proxies / ingress hosts are always patched last.
        while (remaining.Count > 0)
        {
            var ready = remaining
                .Where(id => remainingDeps[id] == 0)
                .OrderBy(id => byId[id].ServerName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (ready.Count == 0)
            {
                // Cycle fallback: pick remaining server(s) with minimal remaining dependency count, preferring non-edge servers.
                hasCycle = true;
                var minDeps = remaining.Min(id => remainingDeps[id]);
                var candidates = remaining
                    .Where(id => remainingDeps[id] == minDeps)
                    .OrderBy(id => edgeProxyServerIds.Contains(id) ? 1 : 0)
                    .ThenBy(id => byId[id].ServerName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                ready = [candidates[0]];
            }
            else
            {
                var nonEdgeReady = ready.Where(id => !edgeProxyServerIds.Contains(id)).ToList();
                if (nonEdgeReady.Count > 0 && nonEdgeReady.Count < ready.Count)
                {
                    ready = nonEdgeReady;
                }
            }

            rawWaves.Add(ready);
            foreach (var completed in ready)
            {
                remaining.Remove(completed);
                foreach (var dependent in callers[completed])
                {
                    if (remaining.Contains(dependent) && remainingDeps[dependent] > 0)
                    {
                        remainingDeps[dependent]--;
                    }
                }
            }
        }

        var waves = new List<RollingUpdateWave>(rawWaves.Count);
        for (var i = 0; i < rawWaves.Count; i++)
        {
            var waveNumber = i + 1;
            var ids = rawWaves[i];
            var waveServers = ids.Select(id =>
            {
                var s = byId[id];
                var role = ClassifyRole(id, dependencies[id].Count, callers[id].Count, edgeProxyServerIds.Contains(id));
                return new RollingWaveServer(
                    s.ServerId,
                    s.ServerName,
                    s.Group,
                    s.PendingUpdateCount,
                    s.SecurityUpdateCount,
                    s.ReadOnly,
                    role,
                    s.HealthCheckNames);
            }).ToArray();

            var label = BuildWaveLabel(waveNumber, rawWaves.Count, waveServers);
            var gate = BuildVerificationGate(waveNumber, rawWaves.Count, waveServers);
            waves.Add(new RollingUpdateWave(waveNumber, label, waveServers, gate));
        }

        return new RollingUpdatePlan(snapshotId, basedOnGraph, hasCycle, waves);
    }

    private static string ClassifyRole(Guid serverId, int outgoingDeps, int incomingCallers, bool isEdgeProxy)
    {
        if (isEdgeProxy) return "Reverse proxy / edge";
        if (outgoingDeps == 0 && incomingCallers > 0) return "Backend / data leaf";
        if (outgoingDeps > 0) return "Application tier";
        return "Standalone / leaf";
    }

    private static string BuildWaveLabel(int waveNumber, int totalWaves, IReadOnlyList<RollingWaveServer> servers)
    {
        if (servers.All(s => s.TierRole == "Reverse proxy / edge"))
            return $"Wave {waveNumber} · Reverse proxy / edge";
        if (waveNumber == 1)
            return $"Wave {waveNumber} · Leaves & non-proxied backends";
        if (waveNumber == totalWaves)
            return $"Wave {waveNumber} · Edge & top-tier callers";
        return $"Wave {waveNumber} · Application tier";
    }

    private static RollingVerificationGate BuildVerificationGate(int waveNumber, int totalWaves, IReadOnlyList<RollingWaveServer> servers)
    {
        var checks = new List<string>();
        foreach (var server in servers)
        {
            if (server.HealthCheckNames.Count > 0)
            {
                checks.Add($"{server.ServerName}: verify health checks ({string.Join(", ", server.HealthCheckNames)})");
            }
            else
            {
                checks.Add($"{server.ServerName}: verify SSH reachability and zero failed systemd/container services");
            }
        }

        var summary = waveNumber < totalWaves
            ? $"Pause after Wave {waveNumber}: confirm all health checks pass before starting Wave {waveNumber + 1}."
            : $"Final verification after Wave {waveNumber}: confirm end-to-end health checks and alerts remain green.";

        return new RollingVerificationGate(summary, checks);
    }
}
