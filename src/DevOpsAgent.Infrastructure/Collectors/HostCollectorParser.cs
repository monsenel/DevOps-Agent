using System.Net;
using System.Text.Json;
using DevOpsAgent.Application.Discovery;

namespace DevOpsAgent.Infrastructure.Collectors;

public static class HostCollectorParser
{
    // Routes of these types start the line with the type before the destination (iproute2: `blackhole 10.9.0.0/24`).
    private static readonly HashSet<string> RouteTypes = new(StringComparer.Ordinal)
    {
        "unicast", "local", "broadcast", "multicast", "throw", "unreachable", "prohibit", "blackhole", "nat", "anycast"
    };

    /// <summary>
    /// Parses the address and route listings. Each is either the JSON of <c>ip -j</c> (starts with <c>[</c> or <c>{</c>)
    /// or, on BusyBox hosts without JSON support, the text of <c>ip -o addr show</c> / <c>ip route show</c>.
    /// </summary>
    public static HostDiscoveryResult Parse(string addresses, string routes, string hostname, string hostsFile) =>
        new(
            hostname.Trim(),
            IsJson(addresses) ? ParseInterfacesJson(addresses) : ParseIpOnelineAddr(addresses),
            IsJson(routes) ? ParseRoutesJson(routes) : ParseIpRouteText(routes),
            ParseHostsFile(hostsFile));

    /// <summary>True when <paramref name="output"/> is what <c>ip -j</c> prints; false for text (BusyBox) or empty output.</summary>
    public static bool IsJson(string output)
    {
        var trimmed = output.AsSpan().TrimStart();
        return trimmed.Length > 0 && trimmed[0] is '[' or '{';
    }

    private static IReadOnlyList<HostInterface> ParseInterfacesJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("ip -j addr output must be a JSON array.");
        }

        return ParseInterfaces(document.RootElement);
    }

    private static IReadOnlyList<HostRoute> ParseRoutesJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("ip -j route output must be a JSON array.");
        }

        return ParseRoutes(document.RootElement);
    }

    /// <summary>
    /// Parses <c>ip -o addr show</c> (BusyBox and iproute2). One address per line, the part after <c>\</c> is the lifetime
    /// continuation: <c>2: eth0    inet 192.168.20.5/24 brd 192.168.20.255 scope global eth0\       valid_lft forever ...</c>.
    /// MAC address, state and MTU are only filled when the output has link lines (iproute2 style), otherwise null.
    /// </summary>
    public static IReadOnlyList<HostInterface> ParseIpOnelineAddr(string output)
    {
        var builders = new List<InterfaceBuilder>();
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            var continuation = line.IndexOf('\\');
            var head = (continuation >= 0 ? line[..continuation] : line)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var tail = continuation >= 0
                ? line[(continuation + 1)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                : [];
            if (head.Length < 3 || !head[0].EndsWith(':') || !int.TryParse(head[0].AsSpan(0, head[0].Length - 1), out var index))
            {
                continue;
            }

            var name = head[1].TrimEnd(':').Split('@')[0];
            if (name.Length == 0)
            {
                continue;
            }

            var builder = builders.Find(item => item.Name == name);
            if (builder is null)
            {
                builder = new InterfaceBuilder(index, name);
                builders.Add(builder);
            }

            if (head[2] is "inet" or "inet6")
            {
                if (TryParseTextAddress(head[2], head[3..], out var address))
                {
                    builder.Addresses.Add(address);
                }

                continue;
            }

            // Link line: `2: eth0    link/ether 52:.. brd ..` or `2: eth0: <FLAGS> mtu 1500 ... state UP ...\    link/ether 52:..`.
            builder.Mtu ??= ValueAfter(head, "mtu") is { } mtu && int.TryParse(mtu, out var parsedMtu) ? parsedMtu : null;
            builder.State ??= ValueAfter(head, "state");
            builder.Mac ??= LinkAddress(head) ?? LinkAddress(tail);
        }

        return builders
            .Select(item => new HostInterface(item.Index, item.Name, item.Mac, item.State, item.Mtu, item.Addresses))
            .ToArray();
    }

    /// <summary>
    /// Parses <c>ip route show</c> (BusyBox and iproute2): <c>default via 192.168.20.1 dev eth0  metric 202</c> and
    /// <c>192.168.20.0/24 dev eth0 scope link  src 192.168.20.5</c>. Unknown words are ignored.
    /// </summary>
    public static IReadOnlyList<HostRoute> ParseIpRouteText(string output)
    {
        var routes = new List<HostRoute>();
        foreach (var line in output.Split('\n'))
        {
            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0 || tokens[0] == "nexthop")
            {
                continue;
            }

            var position = RouteTypes.Contains(tokens[0]) && tokens.Length > 1 ? 1 : 0;
            var destination = tokens[position++];
            string? gateway = null, device = null, protocol = null, scope = null, source = null;
            int? metric = null;
            for (; position < tokens.Length - 1; position++)
            {
                switch (tokens[position])
                {
                    case "via":
                        position++;
                        if (tokens[position] is "inet" or "inet6" && position + 1 < tokens.Length)
                        {
                            position++;
                        }

                        gateway = tokens[position];
                        break;
                    case "dev":
                        device = tokens[++position];
                        break;
                    case "proto":
                        protocol = tokens[++position];
                        break;
                    case "scope":
                        scope = tokens[++position];
                        break;
                    case "src":
                        source = tokens[++position];
                        break;
                    case "metric":
                        metric = int.TryParse(tokens[++position], out var parsedMetric) ? parsedMetric : null;
                        break;
                }
            }

            routes.Add(new HostRoute(InferFamily(destination, gateway), destination, gateway, device, protocol, scope,
                source, metric, IsDefaultDestination(destination)));
        }

        return routes;
    }

    private static bool TryParseTextAddress(string family, string[] fields, out HostAddress address)
    {
        address = null!; // assigned on every path that returns true
        if (fields.Length == 0)
        {
            return false;
        }

        var parts = fields[0].Split('/', 2);
        if (!IPAddress.TryParse(parts[0], out var ipAddress))
        {
            return false;
        }

        var maxPrefix = ipAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        // Point-to-point addresses print without a prefix (`inet 10.0.0.1 peer 10.0.0.2/32`): the local side is a host address.
        var prefixLength = parts.Length == 2 ? int.TryParse(parts[1], out var parsed) ? parsed : -1 : maxPrefix;
        if (prefixLength < 0 || prefixLength > maxPrefix)
        {
            return false;
        }

        address = new HostAddress(family, ipAddress.ToString(), prefixLength, ToSubnetCidr(ipAddress, prefixLength),
            ValueAfter(fields, "scope"), ValueAfter(fields, "brd"));
        return true;
    }

    private static string? ValueAfter(string[] tokens, string key)
    {
        var position = Array.IndexOf(tokens, key);
        return position >= 0 && position + 1 < tokens.Length ? tokens[position + 1] : null;
    }

    private static string? LinkAddress(string[] tokens)
    {
        var position = Array.FindIndex(tokens, token => token.StartsWith("link/", StringComparison.Ordinal));
        return position >= 0 && position + 1 < tokens.Length ? tokens[position + 1] : null;
    }

    private static bool IsDefaultDestination(string destination) =>
        destination.Equals("default", StringComparison.OrdinalIgnoreCase) || destination is "0.0.0.0/0" or "::/0";

    private sealed class InterfaceBuilder(int index, string name)
    {
        public int Index { get; } = index;
        public string Name { get; } = name;
        public string? Mac { get; set; }
        public string? State { get; set; }
        public int? Mtu { get; set; }
        public List<HostAddress> Addresses { get; } = [];
    }

    private static IReadOnlyList<HostInterface> ParseInterfaces(JsonElement root)
    {
        var interfaces = new List<HostInterface>();
        foreach (var item in root.EnumerateArray())
        {
            var index = GetInt32(item, "ifindex") ?? 0;
            var name = GetString(item, "ifname");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var addresses = new List<HostAddress>();
            if (item.TryGetProperty("addr_info", out var addressInfo) && addressInfo.ValueKind == JsonValueKind.Array)
            {
                foreach (var address in addressInfo.EnumerateArray())
                {
                    var localAddress = GetString(address, "local");
                    var prefixLength = GetInt32(address, "prefixlen");
                    if (localAddress is null || prefixLength is null || !IPAddress.TryParse(localAddress, out var ipAddress))
                    {
                        continue;
                    }

                    var maxPrefix = ipAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
                    if (prefixLength < 0 || prefixLength > maxPrefix)
                    {
                        continue;
                    }

                    var family = GetString(address, "family") ??
                        (ipAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "inet" : "inet6");
                    addresses.Add(new HostAddress(
                        family,
                        ipAddress.ToString(),
                        prefixLength.Value,
                        ToSubnetCidr(ipAddress, prefixLength.Value),
                        GetString(address, "scope"),
                        GetString(address, "broadcast")));
                }
            }

            interfaces.Add(new HostInterface(
                index,
                name,
                GetString(item, "address"),
                GetString(item, "operstate"),
                GetInt32(item, "mtu"),
                addresses));
        }

        return interfaces;
    }

    private static IReadOnlyList<HostRoute> ParseRoutes(JsonElement root)
    {
        var routes = new List<HostRoute>();
        foreach (var item in root.EnumerateArray())
        {
            var destination = GetString(item, "dst") ?? "default";
            var gateway = GetString(item, "gateway");
            var family = GetString(item, "family") ?? InferFamily(destination, gateway);
            var isDefault = IsDefaultDestination(destination);

            routes.Add(new HostRoute(
                family,
                destination,
                gateway,
                GetString(item, "dev"),
                GetString(item, "protocol"),
                GetString(item, "scope"),
                GetString(item, "prefsrc"),
                GetInt32(item, "metric"),
                isDefault));
        }

        return routes;
    }

    private static IReadOnlyList<EtcHostsEntry> ParseHostsFile(string hostsFile)
    {
        var entries = new List<EtcHostsEntry>();
        foreach (var line in hostsFile.Split('\n'))
        {
            var content = line.Split('#', 2)[0].Trim();
            if (content.Length == 0)
            {
                continue;
            }

            var parts = content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !IPAddress.TryParse(parts[0], out var address))
            {
                continue;
            }

            entries.Add(new EtcHostsEntry(address.ToString(), parts[1..]));
        }

        return entries;
    }

    private static string InferFamily(string destination, string? gateway)
    {
        var probe = destination == "default" ? gateway : destination.Split('/')[0];
        return IPAddress.TryParse(probe, out var address) &&
               address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? "inet6"
            : "inet";
    }

    private static string ToSubnetCidr(IPAddress address, int prefixLength)
    {
        var bytes = address.GetAddressBytes();
        for (var index = 0; index < bytes.Length; index++)
        {
            var bitsRemaining = prefixLength - index * 8;
            var mask = bitsRemaining >= 8 ? 0xff : bitsRemaining <= 0 ? 0 : 0xff << (8 - bitsRemaining);
            bytes[index] = (byte)(bytes[index] & mask);
        }

        return $"{new IPAddress(bytes)}/{prefixLength}";
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static int? GetInt32(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var result)
            ? result
            : null;
}
