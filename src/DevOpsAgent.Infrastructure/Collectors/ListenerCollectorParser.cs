using System.Text.RegularExpressions;
using DevOpsAgent.Application.Discovery;

namespace DevOpsAgent.Infrastructure.Collectors;

public static class ListenerCollectorParser
{
    private static readonly Regex ProcessPattern = new("\\(\\\"(?<name>[^\\\"]+)\\\",pid=(?<pid>\\d+)", RegexOptions.CultureInvariant);

    public static IReadOnlyList<ListenerInfo> Parse(string output)
    {
        var listeners = new List<ListenerInfo>();
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var columns = trimmed.Split((char[]?)null, 7, StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length < 5 || !TryParseEndpoint(columns[4], out var address, out var port))
            {
                continue;
            }

            var processName = (string?)null;
            int? processId = null;
            if (columns.Length > 6)
            {
                var match = ProcessPattern.Match(columns[6]);
                if (match.Success && int.TryParse(match.Groups["pid"].Value, out var parsedPid))
                {
                    processName = match.Groups["name"].Value;
                    processId = parsedPid;
                }
            }

            var protocol = columns[0].ToLowerInvariant() switch
            {
                "tcp6" => "tcp",
                "udp6" => "udp",
                var value => value
            };
            var peerAddress = columns.Length > 5 && TryParseEndpoint(columns[5], out var peer, out _) ? peer : null;
            listeners.Add(new ListenerInfo(protocol, columns[1], address, port, peerAddress, processName, processId));
        }

        return listeners;
    }

    private static bool TryParseEndpoint(string endpoint, out string address, out int port)
    {
        address = string.Empty;
        port = 0;
        string portText;
        if (endpoint.Length > 0 && endpoint[0] == '[')
        {
            var closingBracket = endpoint.IndexOf(']');
            if (closingBracket < 0 || closingBracket + 1 >= endpoint.Length || endpoint[closingBracket + 1] != ':')
            {
                return false;
            }

            address = endpoint[1..closingBracket];
            portText = endpoint[(closingBracket + 2)..];
        }
        else
        {
            var separator = endpoint.LastIndexOf(':');
            if (separator < 0)
            {
                return false;
            }

            address = endpoint[..separator];
            portText = endpoint[(separator + 1)..];
        }

        return address.Length > 0 && int.TryParse(portText, out port) && port is >= 0 and <= 65535;
    }
}
