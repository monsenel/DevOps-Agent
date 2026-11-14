using System.Net;
using System.Text.Json;
using DevOpsAgent.Application.Discovery;

namespace DevOpsAgent.Infrastructure.Collectors;

public static class HostCollectorParser
{
    public static HostDiscoveryResult Parse(string addressesJson, string routesJson, string hostname, string hostsFile)
    {
        using var addressesDocument = JsonDocument.Parse(addressesJson);
        using var routesDocument = JsonDocument.Parse(routesJson);

        if (addressesDocument.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("ip -j addr output must be a JSON array.");
        }

        if (routesDocument.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("ip -j route output must be a JSON array.");
        }

        return new HostDiscoveryResult(
            hostname.Trim(),
            ParseInterfaces(addressesDocument.RootElement),
            ParseRoutes(routesDocument.RootElement),
            ParseHostsFile(hostsFile));
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
            var isDefault = destination.Equals("default", StringComparison.OrdinalIgnoreCase) ||
                destination is "0.0.0.0/0" or "::/0";

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
