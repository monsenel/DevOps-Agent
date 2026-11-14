namespace DevOpsAgent.Application.Discovery;

public sealed record HostDiscoveryResult(
    string Hostname,
    IReadOnlyList<HostInterface> Interfaces,
    IReadOnlyList<HostRoute> Routes,
    IReadOnlyList<EtcHostsEntry> HostsEntries);

public sealed record HostInterface(
    int Index,
    string Name,
    string? MacAddress,
    string? State,
    int? Mtu,
    IReadOnlyList<HostAddress> Addresses);

public sealed record HostAddress(
    string Family,
    string Address,
    int PrefixLength,
    string SubnetCidr,
    string? Scope,
    string? Broadcast);

public sealed record HostRoute(
    string Family,
    string Destination,
    string? Gateway,
    string? InterfaceName,
    string? Protocol,
    string? Scope,
    string? PreferredSource,
    int? Metric,
    bool IsDefault);

public sealed record EtcHostsEntry(string Address, IReadOnlyList<string> Hostnames);
