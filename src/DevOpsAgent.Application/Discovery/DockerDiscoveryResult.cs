namespace DevOpsAgent.Application.Discovery;

public sealed record DockerDiscoveryResult(
    IReadOnlyList<DockerContainerInfo> Containers,
    IReadOnlyList<DockerNetworkInfo> Networks);

public sealed record DockerContainerInfo(
    string Id,
    string Name,
    string Image,
    string State,
    string? ComposeProject,
    string? ComposeService,
    int MainPid,
    IReadOnlyList<DockerNetworkEndpoint> Networks,
    IReadOnlyList<DockerPublishedPort> PublishedPorts,
    IReadOnlyList<string> EnvironmentVariableNames);

public sealed record DockerNetworkEndpoint(
    string NetworkName,
    string? NetworkId,
    string? IPv4Address,
    string? IPv6Address,
    IReadOnlyList<string> Aliases);

public sealed record DockerPublishedPort(
    int ContainerPort,
    string Protocol,
    string? HostIp,
    int HostPort);

public sealed record DockerNetworkInfo(
    string Id,
    string Name,
    string? Driver,
    string? Scope,
    IReadOnlyList<string> Subnets,
    IReadOnlyList<DockerNetworkContainer> Containers);

public sealed record DockerNetworkContainer(
    string Id,
    string Name,
    string? IPv4Address,
    string? IPv6Address,
    IReadOnlyList<string> Aliases);
