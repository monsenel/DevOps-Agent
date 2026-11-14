using System.Text.Json;

namespace DevOpsAgent.Application.Discovery;

public static class GraphTraceResolver
{
    public static GraphTracePath? Resolve(GraphSnapshotDto graph, string query)
    {
        var hostname = ExtractHostname(query);
        if (hostname.Length == 0) return null;

        var domainNodes = graph.Nodes.Select(item => item.Data)
            .Where(node => string.Equals(ReadString(node, "kind"), "Domain", StringComparison.OrdinalIgnoreCase))
            .Select(node => (Id: ReadString(node, "id"), Name: ReadString(node, "label")))
            .Where(node => node.Id is not null && node.Name is not null)
            .ToArray();
        var startId = domainNodes.Where(node => string.Equals(node.Name, hostname, StringComparison.OrdinalIgnoreCase))
            .Select(node => node.Id).FirstOrDefault()
            ?? domainNodes.Where(node => node.Name!.StartsWith("*.", StringComparison.Ordinal)
                && hostname.EndsWith(node.Name[1..], StringComparison.OrdinalIgnoreCase))
                .Select(node => node.Id).FirstOrDefault();
        if (startId is null) return null;

        var outgoing = graph.Edges.Select(edge => edge.Data)
            .Where(edge => ReadString(edge, "kind") is "Declared" or "Observed" or "Inferred")
            .Select(edge => (Data: edge, Source: ReadString(edge, "source"), Target: ReadString(edge, "target")))
            .Where(edge => edge.Source is not null && edge.Target is not null)
            .GroupBy(edge => edge.Source!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        var nodeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { startId };
        var edgeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        queue.Enqueue(startId);
        while (queue.TryDequeue(out var current))
        {
            if (!outgoing.TryGetValue(current, out var edges)) continue;
            foreach (var edge in edges)
            {
                if (ReadString(edge.Data, "id") is string edgeId) edgeIds.Add(edgeId);
                if (nodeIds.Add(edge.Target!)) queue.Enqueue(edge.Target!);
            }
        }

        return new GraphTracePath(hostname, nodeIds, edgeIds);
    }

    private static string ExtractHostname(string query)
    {
        var value = query.Trim();
        if (value.Length == 0) return string.Empty;
        if (!value.Contains("://", StringComparison.Ordinal)) value = $"https://{value}";
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            ? uri.IdnHost.TrimEnd('.').ToLowerInvariant()
            : string.Empty;
    }

    private static string? ReadString(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var candidate in element.EnumerateObject())
        {
            if (string.Equals(candidate.Name, property, StringComparison.OrdinalIgnoreCase)
                && candidate.Value.ValueKind == JsonValueKind.String)
            {
                return candidate.Value.GetString();
            }
        }

        return null;
    }
}

public sealed record GraphTracePath(string Hostname, IReadOnlySet<string> NodeIds, IReadOnlySet<string> EdgeIds);
