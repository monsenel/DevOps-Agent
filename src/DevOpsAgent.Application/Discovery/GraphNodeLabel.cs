using System.Text.Json;

namespace DevOpsAgent.Application.Discovery;

public static class GraphNodeLabel
{
    /// <summary>
    /// The text that identifies a graph node to keyboard and screen-reader users. Unknown endpoints are all labelled
    /// "Unknown", so their address and port are appended to tell them apart.
    /// </summary>
    public static string Describe(JsonElement node)
    {
        var label = DescribeWithoutStatus(node);
        // Staleness is part of the accessible name, not only a dimmed colour (NFR-3).
        var stale = node.ValueKind == JsonValueKind.Object && node.TryGetProperty("attributes", out var attributes)
            ? StaleCarryForward.Badge(attributes) : null;
        return stale is null ? label : $"{label} ({stale})";
    }

    private static string DescribeWithoutStatus(JsonElement node)
    {
        var label = ReadString(node, "label") ?? ReadString(node, "kind") ?? "Node";
        if (!string.Equals(ReadString(node, "kind"), "Unknown", StringComparison.Ordinal)) return label;

        var address = node.TryGetProperty("attributes", out var attributes) && attributes.ValueKind == JsonValueKind.Object
            ? ReadString(attributes, "Address") : null;
        var port = node.TryGetProperty("attributes", out attributes) && attributes.ValueKind == JsonValueKind.Object
            && attributes.TryGetProperty("Port", out var portValue) && portValue.ValueKind == JsonValueKind.Number
            ? portValue.ToString() : null;
        var endpoint = address is null ? null : port is null ? address : $"{address}:{port}";
        // Fall back to the stable node key (it ends in address:port) when the attributes carry no address.
        endpoint ??= ReadString(node, "key");
        return endpoint is null ? label : $"{label} {endpoint}";
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
