using DevOpsAgent.Application.Discovery;

namespace DevOpsAgent.Infrastructure.Collectors;

public static class ConnectionCollectorParser
{
    public static ConnectionDiscoveryResult Parse(
        string hostOutput,
        IReadOnlyDictionary<string, string>? containerOutputs = null,
        IReadOnlyList<string>? notes = null,
        DateTimeOffset? collectedAtUtc = null)
    {
        var connections = ParseOutput(hostOutput, "host", null).ToList();
        if (containerOutputs is not null)
        {
            foreach (var (containerName, output) in containerOutputs.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                connections.AddRange(ParseOutput(output, "container", containerName));
            }
        }

        return new ConnectionDiscoveryResult(connections, notes ?? [], collectedAtUtc);
    }

    private static IEnumerable<EstablishedConnectionInfo> ParseOutput(string output, string networkNamespace, string? containerName)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var columns = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var hasStateColumn = columns.Length >= 5;
            var state = hasStateColumn ? columns[0] : "ESTAB";
            var endpointOffset = hasStateColumn ? 3 : 2;
            if (columns.Length < endpointOffset + 2 || !string.Equals(state, "ESTAB", StringComparison.OrdinalIgnoreCase)
                || !TryParseEndpoint(columns[endpointOffset], out var localAddress, out var localPort)
                || !TryParseEndpoint(columns[endpointOffset + 1], out var peerAddress, out var peerPort))
            {
                continue;
            }

            yield return new EstablishedConnectionInfo(networkNamespace, containerName, "tcp", "ESTAB",
                localAddress, localPort, peerAddress, peerPort);
        }
    }

    private static bool TryParseEndpoint(string value, out string address, out int port)
    {
        address = string.Empty;
        port = 0;
        string portText;
        if (value.StartsWith('['))
        {
            var closing = value.IndexOf(']');
            if (closing < 0 || closing + 1 >= value.Length || value[closing + 1] != ':') return false;
            address = value[1..closing];
            portText = value[(closing + 2)..];
        }
        else
        {
            var separator = value.LastIndexOf(':');
            if (separator <= 0) return false;
            address = value[..separator];
            portText = value[(separator + 1)..];
        }

        return int.TryParse(portText, out port) && port is >= 0 and <= 65535;
    }
}
