using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Domain.Graph;
using DevOpsAgent.Domain.Monitoring;

namespace DevOpsAgent.Application.Search;

public enum SearchResultKind
{
    Server,
    Node,
    Listener,
    Alert
}

/// <param name="Url">Where Enter goes: the server page, Topology focused on the node, or the alert.</param>
/// <param name="IsStale">True when the result comes from carried-forward stale discovery data.</param>
public sealed record SearchResult(SearchResultKind Kind, string Title, string Detail, string Url, bool IsStale = false);

public sealed record SearchResultGroup(SearchResultKind Kind, string Label, IReadOnlyList<SearchResult> Items);

/// <summary>
/// The top bar's search (T8.2, hardened in T7.R30): servers (name, host, IP addresses from the latest discovery), graph nodes
/// (containers, services, domains, unknown endpoints), listeners (by port) and open alerts. Best matches first (exact, then
/// prefix, then contains), at most <see cref="MaxResults"/> in total, grouped by type.
/// </summary>
public sealed class GlobalSearchService(
    IServerRepository servers,
    ISnapshotRepository snapshots,
    IGraphRepository graphs,
    IAlertRepository alerts)
{
    public const int MaxResults = 20;
    public const int MaxQueryLength = 128;

    private static readonly (SearchResultKind Kind, string Label)[] Groups =
    [
        (SearchResultKind.Server, "Servers"),
        (SearchResultKind.Node, "Containers, services and domains"),
        (SearchResultKind.Listener, "Listeners"),
        (SearchResultKind.Alert, "Open alerts")
    ];

    public async Task<IReadOnlyList<SearchResultGroup>> SearchAsync(string? query, CancellationToken cancellationToken)
    {
        var text = query?.Trim() ?? string.Empty;
        if (text.Length == 0) return [];
        if (text.Length > MaxQueryLength) text = text[..MaxQueryLength];

        var candidates = new List<(int Score, SearchResult Result)>();
        var inventory = await servers.ListAsync(cancellationToken);
        var serverNames = inventory.ToDictionary(server => server.Id, server => server.Name);
        var latest = await snapshots.GetLatestAsync(cancellationToken);
        IReadOnlyList<GraphNode> nodes = [];
        IReadOnlyList<GraphListener> listeners = [];
        if (latest is not null)
        {
            if (graphs is IGraphSearchRepository searchGraphs)
            {
                var searchData = await searchGraphs.GetSearchDataAsync(latest.Id, cancellationToken);
                nodes = searchData?.Nodes ?? [];
                listeners = searchData?.Listeners ?? [];
            }
            else
            {
                var graph = await graphs.GetEntitiesAsync(latest.Id, cancellationToken);
                nodes = graph?.Nodes ?? [];
                listeners = graph?.Listeners ?? [];
            }
        }

        var staleHosts = nodes
            .Where(node => node.Kind == GraphNodeKind.Host && node.ServerId is not null && IsStaleNode(node))
            .GroupBy(node => node.ServerId!.Value)
            .ToDictionary(group => group.Key, group => ReadString(group.First().AttributesJson, StaleCarryForward.LastSeenAttribute));

        bool IsNodeStale(GraphNode? node) =>
            node is not null && (IsStaleNode(node) || (node.ServerId is { } sid && staleHosts.ContainsKey(sid)));

        string StaleSuffix(GraphNode? node)
        {
            if (!IsNodeStale(node)) return string.Empty;
            var lastSeen = ReadString(node!.AttributesJson, StaleCarryForward.LastSeenAttribute)
                ?? (node.ServerId is { } sid && staleHosts.TryGetValue(sid, out var hostLastSeen) ? hostLastSeen : null);
            return string.IsNullOrWhiteSpace(lastSeen) ? " · stale" : $" · stale (last seen {lastSeen})";
        }

        var hostAddresses = nodes.Where(node => node.Kind == GraphNodeKind.Host && node.ServerId is not null)
            .GroupBy(node => node.ServerId!.Value)
            .ToDictionary(group => group.Key, group => group.SelectMany(node => ReadStrings(node.AttributesJson, "Addresses")).Distinct(StringComparer.Ordinal).ToArray());
        foreach (var server in inventory)
        {
            var addresses = hostAddresses.GetValueOrDefault(server.Id, []);
            var score = Best(text, [server.Name, server.Host, .. addresses]);
            if (score is null) continue;
            var detail = $"{server.User}@{server.Host}:{server.Port} · {server.Group}";
            if (addresses.FirstOrDefault(address => Match(text, address) is not null) is { } matchedAddress && matchedAddress != server.Host)
                detail += $" · {matchedAddress}";
            var serverStale = staleHosts.TryGetValue(server.Id, out var hostLastSeen);
            if (serverStale)
                detail += string.IsNullOrWhiteSpace(hostLastSeen) ? " · stale" : $" · stale (last seen {hostLastSeen})";
            candidates.Add((score.Value, new SearchResult(SearchResultKind.Server, server.Name, detail, $"/servers/{server.Id}", serverStale)));
        }

        // The latest graph still holds the nodes of a server removed since: they are no longer part of the inventory.
        bool InInventory(GraphNode? node) => node?.ServerId is not { } owner || serverNames.ContainsKey(owner);
        foreach (var node in nodes.Where(node => node.Kind is GraphNodeKind.Container or GraphNodeKind.Service or GraphNodeKind.Domain or GraphNodeKind.Unknown)
                     .Where(InInventory))
        {
            var address = node.Kind == GraphNodeKind.Unknown ? ReadString(node.AttributesJson, "Address") : null;
            var title = address is null ? node.Name : $"Unknown · {address}";
            var score = Best(text, node.Kind == GraphNodeKind.Unknown ? [address] : [node.Name]);
            if (score is null) continue;
            var on = node.ServerId is { } serverId && serverNames.TryGetValue(serverId, out var name) ? $" on {name}" : string.Empty;
            var nodeStale = IsNodeStale(node);
            candidates.Add((score.Value, new SearchResult(SearchResultKind.Node, title, $"{KindLabel(node.Kind)}{on}{StaleSuffix(node)}", TopologyUrl(node.Id), nodeStale)));
        }

        // A port is matched as a number (":5432" and "5432" both work), never as a substring of another port.
        var portText = text.TrimStart(':');
        if (int.TryParse(portText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port))
        {
            var byNode = nodes.ToDictionary(node => node.Id);
            foreach (var listener in listeners.Where(listener => listener.Port == port)
                         .DistinctBy(listener => (listener.NodeId, listener.Port, listener.Protocol)))
            {
                byNode.TryGetValue(listener.NodeId, out var owner);
                if (!InInventory(owner)) continue;
                var server = owner?.ServerId is { } id && serverNames.TryGetValue(id, out var name) ? name : "unknown server";
                // A listener without a process name hangs off its host node: then only the server is named.
                var process = listener.ProcessName ?? (owner is { Kind: not GraphNodeKind.Host } ? owner.Name : null);
                var listenerStale = IsNodeStale(owner);
                candidates.Add((0, new SearchResult(SearchResultKind.Listener, $":{listener.Port} {listener.Protocol}",
                    $"{(process is null ? string.Empty : process + " ")}on {server} · {listener.Address}{StaleSuffix(owner)}", TopologyUrl(listener.NodeId), listenerStale)));
            }
        }

        foreach (var alert in await alerts.ListAsync(AlertState.Open, cancellationToken))
        {
            var score = Best(text, [alert.Title, alert.Target, alert.Rule]);
            if (score is null) continue;
            candidates.Add((score.Value, new SearchResult(SearchResultKind.Alert, alert.Title, $"{alert.Severity} · {alert.Target}", $"/alerts?alert={alert.Id}")));
        }

        var kept = candidates
            .OrderBy(item => item.Score)
            .ThenBy(item => (int)item.Result.Kind)
            .ThenBy(item => item.Result.Title, StringComparer.OrdinalIgnoreCase)
            .Take(MaxResults)
            .Select(item => item.Result)
            .ToArray();
        return Groups.Select(group => new SearchResultGroup(group.Kind, group.Label, kept.Where(item => item.Kind == group.Kind).ToArray()))
            .Where(group => group.Items.Count > 0)
            .ToArray();
    }

    private static bool IsStaleNode(GraphNode node) =>
        TryRead(node.AttributesJson, StaleCarryForward.StaleAttribute, out var value) && value.ValueKind == JsonValueKind.True;

    public static string TopologyUrl(Guid nodeId) => $"/topology?node={nodeId:N}";

    private static string KindLabel(GraphNodeKind kind) => kind switch
    {
        GraphNodeKind.Container => "Container",
        GraphNodeKind.Service => "Service",
        GraphNodeKind.Domain => "Domain",
        _ => "Unknown endpoint"
    };

    // 0 = exact, 1 = starts with, 2 = contains; null = no match. Case-insensitive.
    private static int? Match(string query, string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (value.Equals(query, StringComparison.OrdinalIgnoreCase)) return 0;
        if (value.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 1;
        return value.Contains(query, StringComparison.OrdinalIgnoreCase) ? 2 : null;
    }

    private static int? Best(string query, IEnumerable<string?> values) =>
        values.Select(value => Match(query, value)).Where(score => score is not null).Min();

    private static IEnumerable<string> ReadStrings(string json, string property)
    {
        if (!TryRead(json, property, out var value) || value.ValueKind != JsonValueKind.Array) return [];
        return value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray();
    }

    private static string? ReadString(string json, string property) =>
        TryRead(json, property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

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
