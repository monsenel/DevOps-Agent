using System.Net;
using DevOpsAgent.Application.Discovery;

namespace DevOpsAgent.Infrastructure.Collectors;

public static class ConnectionCollectorParser
{
    private const int DefaultSshPort = 22;

    /// <summary>Distinct (source address, server port) groups kept per server; the rest is summarised in a note.</summary>
    public const int MaxSshSources = 50;

    public static ConnectionDiscoveryResult Parse(
        string hostOutput,
        IReadOnlyDictionary<string, string>? containerOutputs = null,
        IReadOnlyList<string>? notes = null,
        DateTimeOffset? collectedAtUtc = null,
        string? agentSshClient = null,
        IReadOnlyCollection<int>? sshdPorts = null)
    {
        var hostConnections = ParseOutput(hostOutput, "host", null).ToList();
        var resultNotes = notes is null ? new List<string>() : [.. notes];

        // SSH ports: 22, the port of the agent's own session and the ports sshd listens on. Only inbound connections
        // (local port is an SSH port) of the host namespace are sessions; outbound connections to :22 stay ordinary.
        var hasAgentClient = TryParseSshClient(agentSshClient, out var agentAddress, out var agentPort);
        var sshPorts = new HashSet<int> { DefaultSshPort };
        if (hasAgentClient) sshPorts.Add(agentPort);
        if (sshdPorts is not null) sshPorts.UnionWith(sshdPorts);

        var sessionGroups = new Dictionary<(string Address, int Port), int>();
        var connections = new List<EstablishedConnectionInfo>(hostConnections.Count);
        foreach (var connection in hostConnections)
        {
            if (!sshPorts.Contains(connection.LocalPort))
            {
                connections.Add(connection);
                continue;
            }

            var key = (NormalizeAddress(connection.PeerAddress), connection.LocalPort);
            sessionGroups[key] = sessionGroups.GetValueOrDefault(key) + 1;
        }

        // The remote port is deliberately not compared with $SSH_CLIENT: another SSH connection of the agent (pool, a second
        // run) comes from the same address to the same server port.
        var sessions = sessionGroups
            .Select(group => new SshSessionInfo(group.Key.Address, group.Key.Port, group.Value,
                hasAgentClient && group.Key.Port == agentPort && group.Key.Address == agentAddress))
            .OrderBy(session => session.IsAgent ? 0 : 1)
            .ThenByDescending(session => session.Count)
            .ThenBy(session => session.SourceAddress, StringComparer.Ordinal)
            .ThenBy(session => session.ServerPort)
            .ToList();
        if (sessions.Count > MaxSshSources)
        {
            var dropped = sessions.Skip(MaxSshSources).ToArray();
            sessions = sessions.Take(MaxSshSources).ToList();
            resultNotes.Add($"{dropped.Length} more SSH sources with {dropped.Sum(session => session.Count)} sessions are not listed.");
        }

        sessions = [.. sessions.OrderBy(session => session.SourceAddress, StringComparer.Ordinal).ThenBy(session => session.ServerPort)];

        if (containerOutputs is not null)
        {
            foreach (var (containerName, output) in containerOutputs.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                connections.AddRange(ParseOutput(output, "container", containerName));
            }
        }

        return new ConnectionDiscoveryResult(connections, resultNotes, collectedAtUtc, sessions);
    }

    /// <summary>Mapped IPv6 turned into IPv4 and lowercase; text that is not an address is only lowercased.</summary>
    private static string NormalizeAddress(string address) =>
        IPAddress.TryParse(address, out var parsed) ? Normalize(parsed).ToString().ToLowerInvariant() : address.ToLowerInvariant();

    /// <summary>Parses <c>$SSH_CLIENT</c> ("client-ip client-port server-port"); the server port defaults to 22.</summary>
    private static bool TryParseSshClient(string? value, out string address, out int serverPort)
    {
        address = string.Empty;
        serverPort = DefaultSshPort;
        var fields = value?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields is not { Length: > 0 } || !IPAddress.TryParse(fields[0], out var parsed)) return false;

        address = Normalize(parsed).ToString().ToLowerInvariant();
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
