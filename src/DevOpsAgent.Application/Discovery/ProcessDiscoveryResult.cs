namespace DevOpsAgent.Application.Discovery;

public enum ProcessCategory
{
    Os = 0,
    Service = 1,
    Docker = 2
}

public sealed record ProcessInfo(
    int Pid,
    int ParentPid,
    string User,
    string State,
    double CpuPercent,
    double MemoryPercent,
    long ElapsedSeconds,
    string Name,
    string CommandLine,
    ProcessCategory Category,
    string? UnitName = null,
    string? ContainerName = null);

public sealed record DockerInventoryContainerInfo(
    string Id,
    string Name,
    string Image,
    string State,
    string StatusText,
    string? PortsText = null);

public sealed record ProcessDiscoveryResult(
    double? SystemUptimeSeconds,
    IReadOnlyList<ProcessInfo> Processes,
    IReadOnlyList<DockerInventoryContainerInfo> DockerContainers,
    bool DockerInstalled,
    bool DockerDaemonRunning,
    int TotalProcessCount = 0);
