using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Domain.Agent;
using DevOpsAgent.Domain.Graph;

namespace DevOpsAgent.Application.Agent;

/// <param name="Name">The dependent node's name ("server block · api.test.local", "Unknown · 10.88.20.20").</param>
/// <param name="Server">The server it runs on; null for nodes outside the inventory.</param>
/// <param name="Level">1 = connects to the target directly, 2 = connects to something that does.</param>
public sealed record ImpactDependent(string Name, string Kind, string? Server, int Level);

/// <summary>
/// What a proposed action may affect (T8.7), from the latest discovery graph: the target's dependents (inbound declared and
/// observed connections, two levels), the other servers they run on, and the domains nginx routes to the target.
/// </summary>
/// <param name="Target">A short description of what the action touches.</param>
/// <param name="Warnings">"The target is stale …", "The graph is older than 24 h …", "No discovery yet …".</param>
public sealed record ImpactPreview(string Target, IReadOnlyList<ImpactDependent> Dependents, IReadOnlyList<string> Servers,
    IReadOnlyList<string> Domains, IReadOnlyList<string> Warnings, DateTimeOffset? GraphAtUtc, bool TargetFound,
    IReadOnlyList<PackageImpact>? Packages = null)
{
    public bool IsEmpty => Dependents.Count == 0 && Domains.Count == 0;
}

/// <summary>What a package update on a server touches (T8.9): the services it can restart and a warning for Docker or nginx.</summary>
public sealed record PackageImpact(string Package, string Effect, IReadOnlyList<string> Services, bool RestartsDockerOrNginx);

public sealed class ImpactService(IServerRepository servers, ISnapshotRepository snapshots, IGraphRepository graphs, TimeProvider timeProvider)
{
    /// <summary>A graph older than this is called out in the card.</summary>
    public static readonly TimeSpan StaleGraphAge = TimeSpan.FromHours(24);

    private const int DependentLevels = 2;
    private const int DomainSearchDepth = 6;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<ImpactPreview>> ForActionAsync(ActionRecord action, CancellationToken cancellationToken)
    {
        var serverIds = JsonSerializer.Deserialize<Guid[]>(action.ServerIdsJson) ?? [];
        var results = new List<ImpactPreview>();
        foreach (var serverId in serverIds.Distinct())
            results.Add(await ForAsync(action.ToolName, action.ArgumentsJson, serverId, cancellationToken));
        return results;
    }

    public async Task<ImpactPreview> ForAsync(string toolName, string argumentsJson, Guid serverId, CancellationToken cancellationToken)
    {
        var inventory = await servers.ListAsync(cancellationToken);
        var serverNames = inventory.ToDictionary(server => server.Id, server => server.Name);
        var serverName = serverNames.GetValueOrDefault(serverId, "unknown server");
        using var arguments = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        var args = arguments.RootElement;
        var latest = await snapshots.GetLatestAsync(cancellationToken);
        var graph = latest is null ? null : await graphs.GetEntitiesAsync(latest.Id, cancellationToken);
        var (description, selector) = Describe(toolName, args, serverName);
        if (latest is null || graph is null)
            return new ImpactPreview(description, [], [], [], ["No discovery has run yet, so the impact is unknown. Run discovery first."], null, false);

        var warnings = new List<string>();
        var age = timeProvider.GetUtcNow() - (latest.FinishedAtUtc ?? latest.StartedAtUtc);
        if (age > StaleGraphAge)
            warnings.Add($"The graph is {FormatAge(age)} old (older than 24 h); dependencies may have changed. Run discovery for a current view.");

        var nodes = graph.Nodes.ToDictionary(node => node.Id);
        var children = graph.Nodes.Where(node => node.ParentNodeId is not null).ToLookup(node => node.ParentNodeId!.Value);
        var targets = selector(graph.Nodes.Where(node => node.ServerId == serverId).ToArray());
        // A container or a host takes everything inside it along: its services and their listeners.
        var targetSet = new HashSet<Guid>();
        var pending = new Stack<GraphNode>(targets);
        while (pending.TryPop(out var node))
            if (targetSet.Add(node.Id))
                foreach (var child in children[node.Id]) pending.Push(child);
        if (targetSet.Count == 0)
            return new ImpactPreview(description, [], [], [], [.. warnings, $"The target is not in the latest discovery graph of {serverName}; its dependents are unknown."],
                latest.StartedAtUtc, false);

        foreach (var stale in targets.Where(IsStale))
            warnings.Add($"{Label(stale)} is stale in the graph (last seen {ReadString(stale.AttributesJson, StaleCarryForward.LastSeenAttribute) ?? "unknown"}): the server was unreachable at the last discovery.");

        // Edges point from the consumer to the provider (domain → server block → upstream → container), so dependents are the
        // sources of edges into the target. Network attachments are topology, not use.
        var inbound = graph.Edges.Where(edge => edge.Kind != GraphEdgeKind.NetworkAttachment && edge.SourceNodeId != edge.TargetNodeId)
            .ToLookup(edge => edge.TargetNodeId, edge => edge.SourceNodeId);
        var dependents = new List<ImpactDependent>();
        var seen = new HashSet<Guid>(targetSet);
        var frontier = targetSet.ToList();
        for (var level = 1; level <= DependentLevels && frontier.Count > 0; level++)
        {
            var next = new List<Guid>();
            foreach (var id in frontier)
            foreach (var source in inbound[id])
            {
                if (!seen.Add(source) || !nodes.TryGetValue(source, out var node)) continue;
                next.Add(source);
                if (node.Kind is GraphNodeKind.Domain or GraphNodeKind.Subnet) continue;
                dependents.Add(new ImpactDependent(Label(node), node.Kind.ToString(),
                    node.ServerId is { } owner ? serverNames.GetValueOrDefault(owner) : null, level));
            }

            frontier = next;
        }

        var domains = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<Guid>(targetSet);
        var walk = targetSet.ToList();
        for (var depth = 0; depth < DomainSearchDepth && walk.Count > 0; depth++)
        {
            var next = new List<Guid>();
            foreach (var source in walk.SelectMany(id => inbound[id]).Where(visited.Add))
            {
                if (nodes.TryGetValue(source, out var node) && node.Kind == GraphNodeKind.Domain) domains.Add(node.Name);
                next.Add(source);
            }

            walk = next;
        }

        var otherServers = dependents.Select(item => item.Server).OfType<string>().Where(name => name != serverName)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        return new ImpactPreview(description, dependents, otherServers, [.. domains], warnings, latest.StartedAtUtc, true);
    }

    // Which nodes of the server the action touches, and how to say it.
    private static (string Description, Func<IReadOnlyList<GraphNode>, IReadOnlyList<GraphNode>> Select) Describe(string tool, JsonElement args, string serverName)
    {
        IReadOnlyList<GraphNode> WholeHost(IReadOnlyList<GraphNode> nodes) => nodes.Where(node => node.Kind == GraphNodeKind.Host).ToArray();
        switch (tool)
        {
            case "restart_service" when ReadString(args, "unit") is { } unit:
                var name = unit.EndsWith(".service", StringComparison.Ordinal) ? unit[..^".service".Length] : unit;
                return ($"service {unit} on {serverName}", nodes => nodes.Where(node => node.Kind == GraphNodeKind.Service
                        && (string.Equals(node.Name, name, StringComparison.Ordinal) || string.Equals(node.Name, unit, StringComparison.Ordinal)
                            || (name == "nginx" && IsNginxConfig(node))))
                    .ToArray());
            case "restart_container" when ReadString(args, "container") is { } container:
                return ($"container {container} on {serverName}", nodes => nodes.Where(node => node.Kind == GraphNodeKind.Container
                    && string.Equals(node.Name, container, StringComparison.Ordinal)).ToArray());
            case "edit_config" when ReadString(args, "path") is { } path:
                return ($"{path} on {serverName}", nodes =>
                {
                    var inFile = nodes.Where(node => string.Equals(ReadString(node.AttributesJson, "FilePath"), path, StringComparison.Ordinal)).ToList();
                    // nginx is reloaded after an edit under /etc/nginx: everything it serves is affected.
                    if (path.StartsWith("/etc/nginx/", StringComparison.Ordinal))
                        inFile.AddRange(nodes.Where(node => IsNginxConfig(node) || (node.Kind == GraphNodeKind.Service && node.Name == "nginx")));
                    return inFile.Count > 0 ? inFile.Distinct().ToArray() : WholeHost(nodes);
                });
            case "apply_updates":
                return ($"package updates on {serverName}", WholeHost);
            case "block_ip" when ReadString(args, "address") is { } address:
                return ($"firewall of {serverName} (blocking {address})", WholeHost);
            case "unblock_ip" when ReadString(args, "address") is { } address:
                return ($"firewall of {serverName} (unblocking {address})", WholeHost);
            default:
                return ($"{serverName} ({tool})", WholeHost);
        }
    }

    public static bool IsNginxConfig(GraphNode node) =>
        node.Kind == GraphNodeKind.Service && (node.Key.Contains(":nginx-server:", StringComparison.Ordinal) || node.Key.Contains(":nginx-upstream:", StringComparison.Ordinal));

    private static bool IsStale(GraphNode node) =>
        TryRead(node.AttributesJson, StaleCarryForward.StaleAttribute, out var value) && value.ValueKind == JsonValueKind.True;

    private static string Label(GraphNode node) => node.Kind == GraphNodeKind.Unknown && ReadString(node.AttributesJson, "Address") is { } address
        ? $"Unknown · {address}"
        : node.Name;

    private static string FormatAge(TimeSpan age) => age.TotalDays >= 2 ? $"{(int)age.TotalDays} days" : $"{(int)age.TotalHours} h";

    private static string? ReadString(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var item in element.EnumerateObject())
            if (item.Name.Equals(property, StringComparison.OrdinalIgnoreCase))
                return item.Value.ValueKind == JsonValueKind.String ? item.Value.GetString() : item.Value.ToString();
        return null;
    }

    private static string? ReadString(string json, string property) => TryRead(json, property, out var value)
        ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString()
        : null;

    private static bool TryRead(string json, string property, out JsonElement value)
    {
        value = default;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            foreach (var item in document.RootElement.EnumerateObject())
            {
                if (!item.Name.Equals(property, StringComparison.OrdinalIgnoreCase)) continue;
                value = item.Value.Clone();
                return true;
            }
        }
        catch (JsonException) { }
        return false;
    }
}
