namespace DevOpsAgent.Application.Discovery;

public sealed record SystemdDiscoveryResult(
    IReadOnlyList<SystemdUnitInfo> Units,
    IReadOnlyList<string>? RunningServices = null);

public sealed record SystemdUnitInfo(
    string Name,
    int MainPid,
    string? ExecStart,
    string? FragmentPath,
    IReadOnlyList<int> ListenerPids,
    DateTimeOffset? ActiveSinceUtc = null);
