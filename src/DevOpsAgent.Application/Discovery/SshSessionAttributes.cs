using System.Text.Json;

namespace DevOpsAgent.Application.Discovery;

/// <summary>Reads the <c>SshSessions</c> of a Host node's attributes (written by <see cref="GraphBuilder"/>, T8.18).</summary>
public static class SshSessionAttributes
{
    public const string PropertyName = "SshSessions";

    /// <summary>
    /// The sessions in a Host node's <paramref name="attributes"/> object. Property names are matched case-insensitively
    /// (the stored JSON is PascalCase, a page may hold camelCase). Missing or malformed data gives an empty list; an item
    /// without a source address is skipped, a missing count is 1, a missing port is 0 and a missing agent flag is false.
    /// </summary>
    public static IReadOnlyList<SshSessionInfo> Read(JsonElement attributes)
    {
        if (!TryGet(attributes, PropertyName, out var list) || list.ValueKind != JsonValueKind.Array) return [];

        var sessions = new List<SshSessionInfo>();
        foreach (var item in list.EnumerateArray())
        {
            if (!TryGet(item, "Address", out var address) || address.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(address.GetString())) continue;

            sessions.Add(new SshSessionInfo(
                address.GetString()!.Trim(),
                TryGet(item, "Port", out var port) && port.ValueKind == JsonValueKind.Number && port.TryGetInt32(out var portNumber)
                    ? portNumber
                    : 0,
                TryGet(item, "Count", out var count) && count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out var countNumber)
                    ? Math.Max(1, countNumber)
                    : 1,
                TryGet(item, "IsAgent", out var agent) && agent.ValueKind == JsonValueKind.True));
        }

        return sessions;
    }

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}
