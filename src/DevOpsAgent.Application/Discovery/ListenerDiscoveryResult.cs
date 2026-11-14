namespace DevOpsAgent.Application.Discovery;

public sealed record ListenerDiscoveryResult(IReadOnlyList<ListenerInfo> Listeners);

public sealed record ListenerInfo(
    string Protocol,
    string State,
    string Address,
    int Port,
    string? PeerAddress,
    string? ProcessName,
    int? ProcessId);
