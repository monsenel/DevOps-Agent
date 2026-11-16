using System.Text.RegularExpressions;
using DevOpsAgent.Application.Discovery;

namespace DevOpsAgent.Infrastructure.Collectors;

public static class ListenerCollectorParser
{
    private static readonly Regex ProcessPattern = new("\\(\\\"(?<name>[^\\\"]+)\\\",pid=(?<pid>\\d+)", RegexOptions.CultureInvariant);

    private static readonly Regex NetstatProgramPattern = new(@"^(?<pid>\d+)/(?<name>.*)$", RegexOptions.CultureInvariant);

    /// <summary>Parses <c>ss -tulpnH</c> or <c>netstat -tulpn</c> output (detected per line).</summary>
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
            if (columns.Length < 5)
            {
                continue;
            }

            // `netstat -tulpn` rows (BusyBox, net-tools) have numeric Recv-Q/Send-Q right after the protocol; `ss` has the state there.
            if (IsDigits(columns[1]) && IsDigits(columns[2]))
            {
                if (ParseNetstatRow(columns) is { } netstatListener)
                {
                    listeners.Add(netstatListener);
                }

                continue;
            }

            if (!TryParseEndpoint(columns[4], out var address, out var port))
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

    // Proto Recv-Q Send-Q Local-Address Foreign-Address [State] [PID/Program name]
    // tcp 0 0 0.0.0.0:80 0.0.0.0:* LISTEN 12/nginx: master pr
    // udp 0 0 0.0.0.0:68 0.0.0.0:*        1/init            (UDP has no state column)
    private static ListenerInfo? ParseNetstatRow(string[] columns)
    {
        var protocol = columns[0].ToLowerInvariant() switch
        {
            "tcp" or "tcp6" => "tcp",
            "udp" or "udp6" or "udplite" or "udplite6" => "udp",
            _ => null
        };
        if (protocol is null || !TryParseEndpoint(columns[3], out var address, out var port))
        {
            return null;
        }

        string? state = null;
        var programIndex = 5;
        if (columns.Length > 5 && IsStateWord(columns[5]))
        {
            state = columns[5];
            programIndex = 6;
        }

        string? processName = null;
        int? processId = null;
        if (columns.Length > programIndex)
        {
            var match = NetstatProgramPattern.Match(string.Join(' ', columns[programIndex..]));
            if (match.Success && int.TryParse(match.Groups["pid"].Value, out var pid))
            {
                processId = pid;
                processName = ProgramName(match.Groups["name"].Value);
            }
        }

        var peerAddress = TryParseEndpoint(columns[4], out var peer, out _) ? peer : null;
        return new ListenerInfo(protocol, state ?? (protocol == "udp" ? "UNCONN" : "LISTEN"), address, port, peerAddress,
            processName, processId);
    }

    // BusyBox shows the command line cut to the column width: "nginx: master pr", "/usr/sbin/sshd -D". Keep the executable's name.
    private static string? ProgramName(string value)
    {
        var end = value.IndexOfAny([':', ' ', '\t']);
        var name = end >= 0 ? value[..end] : value;
        name = name[(name.LastIndexOf('/') + 1)..];
        return name.Length == 0 ? null : name;
    }

    private static bool IsStateWord(string value) =>
        value.Length > 0 && value.All(character => character is >= 'A' and <= 'Z' or '_');

    private static bool IsDigits(string value) => value.Length > 0 && value.All(char.IsAsciiDigit);

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
