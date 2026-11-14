using System.Globalization;
using System.Net;
using DevOpsAgent.Domain.Servers;

namespace DevOpsAgent.Application.Discovery;

public sealed class EdgeResolver
{
    public EdgeResolution Resolve(
        NginxEndpoint endpoint,
        ServerDiscoveryData source,
        IReadOnlyCollection<ServerDiscoveryData> servers,
        IpIndex ipIndex)
    {
        if (endpoint.IsUnresolved)
        {
            return Unknown("nginx target contains a variable and could not be resolved.");
        }

        if (IsUnixSocket(endpoint.Address))
        {
            return Unknown("nginx target is a Unix socket, which is not mapped to an inventory listener.");
        }

        var dockerDns = FindDockerDnsTarget(endpoint.Address, source, servers);
        if (dockerDns is not null)
        {
            return new EdgeResolution(dockerDns, endpoint.Address, false, "Docker DNS on a shared network.");
        }

        var candidateAddresses = new List<string>();
        var directAddress = endpoint.Address.Trim('[', ']');
        if (IPAddress.TryParse(directAddress, out _))
        {
            candidateAddresses.Add(directAddress);
        }
        else
        {
            if (source.Host is not null)
            {
                candidateAddresses.AddRange(source.Host.HostsEntries
                    .Where(entry => entry.Hostnames.Any(name => NameEquals(name, endpoint.Address)))
                    .Select(entry => entry.Address));
            }

            if (source.Nginx?.NameResolutions is { } nameResolutions)
            {
                var resolution = nameResolutions.FirstOrDefault(item => NameEquals(item.Key, endpoint.Address));
                if (resolution.Value is not null) candidateAddresses.AddRange(resolution.Value);
            }
        }

        foreach (var address in candidateAddresses.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var resolution = ResolveIndexedAddress(address, endpoint.Port, endpoint.Protocol, servers, ipIndex);
            if (resolution is not null)
            {
                var nameSource = source.Host?.HostsEntries.Any(entry =>
                    entry.Hostnames.Any(name => NameEquals(name, endpoint.Address))) == true
                    ? "/etc/hosts"
                    : "getent hosts";
                return resolution with { Reason = $"{nameSource} → {resolution.Reason}" };
            }
        }

        return Unknown("no inventory host, container, or listener matched the nginx target.");

        EdgeResolution Unknown(string reason)
        {
            var normalizedAddress = endpoint.Address.Trim().ToLowerInvariant();
            var portPart = endpoint.Port?.ToString(CultureInfo.InvariantCulture) ?? "none";
            return new EdgeResolution($"unknown:{source.Server.Id:N}:{normalizedAddress}:{portPart}",
                endpoint.Address, true, reason);
        }
    }

    private static EdgeResolution? ResolveIndexedAddress(
        string address,
        int? port,
        string protocol,
        IReadOnlyCollection<ServerDiscoveryData> servers,
        IpIndex ipIndex)
    {
        var ownerKeys = ipIndex.Find(address);
        if (ownerKeys.Count == 0) return null;

        var hosts = ownerKeys.Where(key => key.StartsWith("host:", StringComparison.Ordinal))
            .Select(key => Guid.TryParseExact(key[5..], "N", out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty).Distinct().ToArray();
        var containers = ownerKeys.Where(key => key.StartsWith("container:", StringComparison.Ordinal))
            .Select(key => key[10..]).ToHashSet(StringComparer.Ordinal);

        if (containers.Count == 1 && hosts.Length == 0)
        {
            var parts = containers.Single().Split(':', 2);
            if (parts.Length == 2 && Guid.TryParseExact(parts[0], "N", out var containerServerId))
            {
                var containerData = servers.FirstOrDefault(data => data.Server.Id == containerServerId);
                var container = containerData?.Docker?.Containers.FirstOrDefault(item => item.Name == parts[1]);
                if (containerData is not null && container is not null)
                {
                    return new EdgeResolution(GetContainerNodeKey(containerData, container),
                        container.ComposeService ?? container.Name, false, "global IP index → container endpoint.");
                }
            }
        }

        var candidateServerData = servers.Where(data => hosts.Contains(data.Server.Id)
            || data.Docker?.Containers.Any(container =>
                containers.Contains($"{data.Server.Id:N}:{container.Name}")) == true).ToArray();

        if (port is int targetPort)
        {
            var publishedMatches = candidateServerData
                .SelectMany(data => data.Docker?.Containers
                    .Where(container => container.PublishedPorts.Any(published => published.HostPort == targetPort
                        && ProtocolMatches(protocol, published.Protocol)))
                    .Select(container => (Data: data, Container: container)) ?? [])
                .ToArray();
            if (publishedMatches.Length == 1)
            {
                var match = publishedMatches[0];
                return new EdgeResolution(GetContainerNodeKey(match.Data, match.Container),
                    match.Container.ComposeService ?? match.Container.Name, false,
                    "global IP index → published host port → container/service.");
            }

            var listeners = candidateServerData.SelectMany(data => data.Listeners?.Listeners
                    .Where(listener => listener.Port == targetPort && ProtocolMatches(protocol, listener.Protocol))
                    .Select(listener => (Data: data, Listener: listener)) ?? [])
                .ToArray();
            if (listeners.Length == 1)
            {
                var match = listeners[0];
                return new EdgeResolution(GetListenerNodeKey(match.Data, match.Listener),
                    match.Listener.ProcessName ?? match.Data.Server.Name, false,
                    "global IP index → matching listener.");
            }
        }

        var nonSubnetOwners = ownerKeys.Where(key => !key.StartsWith("subnet:", StringComparison.Ordinal)).ToArray();
        if (nonSubnetOwners.Length == 1)
        {
            return new EdgeResolution(nonSubnetOwners[0], nonSubnetOwners[0], false, "global IP index.");
        }

        return null;
    }

    private static string? FindDockerDnsTarget(
        string target,
        ServerDiscoveryData source,
        IReadOnlyCollection<ServerDiscoveryData> servers)
    {
        var sourceContainers = source.Docker?.Containers.Where(IsProxyContainer).ToArray() ?? [];
        var sharedNetworks = sourceContainers.SelectMany(container => container.Networks)
            .Select(network => network.NetworkName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (sharedNetworks.Count == 0) return null;

        var matches = servers.Where(data => data.Server.Id == source.Server.Id)
            .SelectMany(data => data.Docker?.Containers.Select(container => (Data: data, Container: container)) ?? [])
            .Where(item => item.Container.Networks.Any(network => sharedNetworks.Contains(network.NetworkName)))
            .Where(item => ContainerNames(item.Container).Any(name => NameEquals(name, target)))
            .ToArray();
        return matches.Length == 1 ? GetContainerNodeKey(matches[0].Data, matches[0].Container) : null;
    }

    private static IEnumerable<string> ContainerNames(DockerContainerInfo container)
    {
        yield return container.Name;
        if (!string.IsNullOrWhiteSpace(container.ComposeService)) yield return container.ComposeService;
        foreach (var alias in container.Networks.SelectMany(network => network.Aliases)) yield return alias;
    }

    private static bool IsProxyContainer(DockerContainerInfo container) =>
        container.Image.Contains("nginx", StringComparison.OrdinalIgnoreCase)
        || container.Name.Contains("nginx", StringComparison.OrdinalIgnoreCase)
        || container.ComposeService?.Contains("proxy", StringComparison.OrdinalIgnoreCase) == true;

    private static string GetContainerNodeKey(ServerDiscoveryData data, DockerContainerInfo container) =>
        string.IsNullOrWhiteSpace(container.ComposeService)
            ? $"container:{data.Server.Id:N}:{container.Name}"
            : $"service:{data.Server.Id:N}:compose:{(string.IsNullOrWhiteSpace(container.ComposeProject) ? "default" : container.ComposeProject)}/{container.ComposeService}";

    private static string GetListenerNodeKey(ServerDiscoveryData data, ListenerInfo listener)
    {
        if (listener.ProcessId is > 0)
        {
            var container = data.Docker?.Containers.FirstOrDefault(item => item.MainPid == listener.ProcessId.Value);
            if (container is not null) return GetContainerNodeKey(data, container);
            var unit = data.Systemd?.Units.FirstOrDefault(item => item.MainPid == listener.ProcessId.Value);
            if (unit is not null) return $"service:{data.Server.Id:N}:systemd:{unit.Name}";
        }

        return string.IsNullOrWhiteSpace(listener.ProcessName)
            ? $"host:{data.Server.Id:N}"
            : $"service:{data.Server.Id:N}:process:{listener.ProcessName}";
    }

    private static bool NameEquals(string left, string right) =>
        string.Equals(left.Trim().TrimEnd('.'), right.Trim().TrimEnd('.'), StringComparison.OrdinalIgnoreCase);

    private static bool IsUnixSocket(string address) => address.StartsWith("unix:", StringComparison.OrdinalIgnoreCase)
        || address.Contains("://unix:", StringComparison.OrdinalIgnoreCase);

    private static bool ProtocolMatches(string requested, string candidate) =>
        string.IsNullOrWhiteSpace(requested) || string.Equals(requested, candidate, StringComparison.OrdinalIgnoreCase)
        || (requested is "http" or "https" or "grpc" or "grpcs" && candidate == "tcp");
}

public sealed record NginxEndpoint(string Address, int? Port, string Protocol, bool IsUnresolved);

public sealed record EdgeResolution(string NodeKey, string TargetName, bool IsUnknown, string Reason);

public static class NginxEndpointParser
{
    public static NginxEndpoint Parse(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return new NginxEndpoint(target, null, "tcp", true);
        var value = target.Trim();
        if (value.Contains('$')) return new NginxEndpoint(value, null, "tcp", true);
        if (value.StartsWith("unix:", StringComparison.OrdinalIgnoreCase)
            || value.Contains("://unix:", StringComparison.OrdinalIgnoreCase))
        {
            return new NginxEndpoint(value, null, "unix", false);
        }

        var hasScheme = value.Contains("://", StringComparison.Ordinal);
        var parseValue = hasScheme ? value : $"tcp://{value}";
        if (!Uri.TryCreate(parseValue, UriKind.Absolute, out var uri))
        {
            return new NginxEndpoint(value, null, "tcp", false);
        }

        var protocol = hasScheme ? uri.Scheme.ToLowerInvariant() : "tcp";
        var port = uri.IsDefaultPort ? DefaultPort(protocol) : uri.Port;
        return new NginxEndpoint(uri.Host.Trim('[', ']'), port, protocol, uri.Host.Contains('$'));
    }

    private static int? DefaultPort(string protocol) => protocol switch
    {
        "http" or "grpc" => 80,
        "https" or "grpcs" => 443,
        _ => null
    };
}
