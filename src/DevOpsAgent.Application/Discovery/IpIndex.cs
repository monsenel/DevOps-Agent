using System.Net;

namespace DevOpsAgent.Application.Discovery;

public sealed class IpIndex
{
    private readonly Dictionary<string, HashSet<string>> ownersByAddress = new(StringComparer.OrdinalIgnoreCase);

    public void Add(string address, string nodeKey)
    {
        if (!TryNormalize(address, out var normalized))
        {
            return;
        }

        if (!ownersByAddress.TryGetValue(normalized, out var owners))
        {
            owners = new HashSet<string>(StringComparer.Ordinal);
            ownersByAddress.Add(normalized, owners);
        }

        owners.Add(nodeKey);
    }

    public IReadOnlyList<string> Find(string address) => TryNormalize(address, out var normalized) &&
        ownersByAddress.TryGetValue(normalized, out var owners)
            ? owners.OrderBy(key => key, StringComparer.Ordinal).ToArray()
            : [];

    private static bool TryNormalize(string address, out string normalized)
    {
        var candidate = address.Trim();
        var slash = candidate.IndexOf('/');
        if (slash >= 0)
        {
            candidate = candidate[..slash];
        }

        if (IPAddress.TryParse(candidate, out var parsed))
        {
            normalized = parsed.ToString();
            return true;
        }

        normalized = string.Empty;
        return false;
    }
}
