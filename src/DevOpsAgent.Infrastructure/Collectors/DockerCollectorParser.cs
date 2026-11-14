using System.Text.Json;
using DevOpsAgent.Application.Discovery;

namespace DevOpsAgent.Infrastructure.Collectors;

public static class DockerCollectorParser
{
    private const string ComposeProjectLabel = "com.docker.compose.project";
    private const string ComposeServiceLabel = "com.docker.compose.service";

    public static DockerDiscoveryResult Parse(string containersJson, string networksJson)
    {
        using var containerDocument = JsonDocument.Parse(containersJson);
        using var networkDocument = JsonDocument.Parse(networksJson);
        if (containerDocument.RootElement.ValueKind != JsonValueKind.Array || networkDocument.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("Docker inspect output must be a JSON array.");
        }

        var containers = containerDocument.RootElement.EnumerateArray().Select(ParseContainer).ToArray();
        var networks = networkDocument.RootElement.EnumerateArray().Select(ParseNetwork).ToArray();
        return new DockerDiscoveryResult(containers, networks);
    }

    private static DockerContainerInfo ParseContainer(JsonElement item)
    {
        var config = GetObject(item, "Config");
        var state = GetObject(item, "State");
        var labels = GetObject(config, "Labels");
        var networkSettings = GetObject(item, "NetworkSettings");
        var networkEndpoints = ParseContainerNetworks(GetObject(networkSettings, "Networks"));
        var ports = ParsePorts(GetObject(networkSettings, "Ports"));
        var environmentNames = ParseEnvironmentNames(config);
        var containerName = GetString(item, "Name") ?? string.Empty;

        return new DockerContainerInfo(
            GetString(item, "Id") ?? string.Empty,
            containerName.TrimStart('/'),
            GetString(config, "Image") ?? GetString(item, "Image") ?? string.Empty,
            GetString(state, "Status") ?? "unknown",
            GetString(labels, ComposeProjectLabel),
            GetString(labels, ComposeServiceLabel),
            GetInt32(state, "Pid") ?? 0,
            networkEndpoints,
            ports,
            environmentNames);
    }

    private static IReadOnlyList<DockerNetworkEndpoint> ParseContainerNetworks(JsonElement networks)
    {
        if (networks.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var endpoints = new List<DockerNetworkEndpoint>();
        foreach (var network in networks.EnumerateObject())
        {
            var aliases = GetStringArray(network.Value, "Aliases");
            endpoints.Add(new DockerNetworkEndpoint(
                network.Name,
                GetString(network.Value, "NetworkID"),
                GetString(network.Value, "IPAddress"),
                GetString(network.Value, "GlobalIPv6Address"),
                aliases));
        }

        return endpoints;
    }

    private static IReadOnlyList<DockerPublishedPort> ParsePorts(JsonElement ports)
    {
        if (ports.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var result = new List<DockerPublishedPort>();
        foreach (var port in ports.EnumerateObject())
        {
            var parts = port.Name.Split('/', 2);
            if (!int.TryParse(parts[0], out var containerPort))
            {
                continue;
            }

            var protocol = parts.Length > 1 ? parts[1] : "tcp";
            if (port.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var mapping in port.Value.EnumerateArray())
            {
                if (!int.TryParse(GetString(mapping, "HostPort"), out var hostPort))
                {
                    continue;
                }

                result.Add(new DockerPublishedPort(containerPort, protocol,
                    GetString(mapping, "HostIp"), hostPort));
            }
        }

        return result;
    }

    private static IReadOnlyList<string> ParseEnvironmentNames(JsonElement config)
    {
        if (!config.TryGetProperty("Env", out var environment) || environment.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return environment.EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString() ?? string.Empty)
            .Select(value => value.Split('=', 2)[0])
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
    }

    private static DockerNetworkInfo ParseNetwork(JsonElement item)
    {
        var containers = new List<DockerNetworkContainer>();
        var containerMap = GetObject(item, "Containers");
        if (containerMap.ValueKind == JsonValueKind.Object)
        {
            foreach (var container in containerMap.EnumerateObject())
            {
                var ipv4 = GetString(container.Value, "IPv4Address");
                var ipv6 = GetString(container.Value, "IPv6Address");
                containers.Add(new DockerNetworkContainer(
                    container.Name,
                    GetString(container.Value, "Name") ?? string.Empty,
                    RemovePrefixLength(ipv4),
                    RemovePrefixLength(ipv6),
                    GetStringArray(container.Value, "Aliases")));
            }
        }

        return new DockerNetworkInfo(
            GetString(item, "Id") ?? string.Empty,
            GetString(item, "Name") ?? string.Empty,
            GetString(item, "Driver"),
            GetString(item, "Scope"),
            containers);
    }

    private static string? RemovePrefixLength(string? address)
    {
        if (address is null)
        {
            return null;
        }

        var slash = address.IndexOf('/');
        return slash < 0 ? address : address[..slash];
    }

    private static JsonElement GetObject(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.Object
            ? value
            : default;

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var value) ||
            value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static int? GetInt32(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var value) &&
        value.TryGetInt32(out var result)
            ? result
            : null;

    private static IReadOnlyList<string> GetStringArray(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? string.Empty).ToArray()
            : [];
}
