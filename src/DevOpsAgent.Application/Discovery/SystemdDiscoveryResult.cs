namespace DevOpsAgent.Application.Discovery;

public sealed record SystemdDiscoveryResult(IReadOnlyList<SystemdUnitInfo> Units);

public sealed record SystemdUnitInfo(
    string Name,
    int MainPid,
    string? ExecStart,
    string? FragmentPath,
    IReadOnlyList<int> ListenerPids);
