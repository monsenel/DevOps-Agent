using System.Net;
using DevOpsAgent.Application.Discovery;

namespace DevOpsAgent.Infrastructure.Collectors;

public static class ConnectionCollectorParser
{
    public static ConnectionDiscoveryResult Parse(
        string hostOutput,
        IReadOnlyDictionary<string, string>? containerOutputs = null,
        IReadOnlyList<string>? notes = null,
        DateTimeOffset? collectedAtUtc = null,
        string? agentSshClient = null)
    {
        var connections = ParseOutput(hostOutput, "host", null).ToList();
        var resultNotes = notes is null ? new List<string>() : [.. notes];
        if (TryParseSshClient(agentSshClient, out var clientAddress, out var serverPort))
        {
            // Only host-namespace connections can be the agent's session. The remote port is deliberately not compared:
            // another SSH connection of the agent (pool, a second run) comes from the same address to the same port.
            var removed = connections.RemoveAll(connection => connection.LocalPort == serverPort
                && IPAddress.TryParse(connection.PeerAddress, out var peer) && Normalize(peer).Equals(clientAddress));
            if (removed > 0)
            {
                resultNotes.Add($"SSH sessions of DevOps Agent from {clientAddress} to port {serverPort} are not drawn.");
            }
        }

        if (containerOutputs is not null)
        {
            foreach (var (containerName, output) in containerOutputs.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                connections.AddRange(ParseOutput(output, "container", containerName));
            }
        }

        return new ConnectionDiscoveryResult(connections, resultNotes, collectedAtUtc);
    }

    /// <summary>Parses <c>$SSH_CLIENT</c> ("client-ip client-port server-port"); the server port defaults to 22.</summary>
    private static bool TryParseSshClient(string? value, out IPAddress address, out int serverPort)
    {
        address = IPAddress.None;
        serverPort = 22;
        var fields = value?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields is not { Length: > 0 } || !IPAddress.TryParse(fields[0], out var parsed)) return false;

        address = Normalize(parsed);
        if (fields.Length >= 3 && int.TryParse(fields[2], out var port) && port is > 0 and <= 65535)
        {
            serverPort = port;
        }

        return true;
    }

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static IEnumerable<EstablishedConnectionInfo> ParseOutput(string output, string networkNamespace, string? containerName)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var columns = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            // `netstat -tn` (BusyBox): Proto Recv-Q Send-Q Local-Address Foreign-Address State
            if (columns.Length >= 6 && columns[0].StartsWith("tcp", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(columns[5], "ESTABLISHED", StringComparison.OrdinalIgnoreCase)
                    && TryParseEndpoint(columns[3], out var netstatLocal, out var netstatLocalPort)
                    && TryParseEndpoint(columns[4], out var netstatPeer, out var netstatPeerPort))
                {
                    yield return new EstablishedConnectionInfo(networkNamespace, containerName, "tcp", "ESTAB",
                        netstatLocal, netstatLocalPort, netstatPeer, netstatPeerPort);
                }

                continue;
            }

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

        if (IPAddress.TryParse(address, out var parsedAddress) && parsedAddress.IsIPv4MappedToIPv6)
        {
            address = parsedAddress.MapToIPv4().ToString();
        }

        return int.TryParse(portText, out port) && port is >= 0 and <= 65535;
    }
}
