namespace DevOpsAgent.Application.Discovery;

public sealed record ConnectionDiscoveryResult(
    IReadOnlyList<EstablishedConnectionInfo> Connections,
    IReadOnlyList<string> Notes,
    DateTimeOffset? CollectedAtUtc = null);

public sealed record EstablishedConnectionInfo(
    string Namespace,
    string? ContainerName,
    string Protocol,
    string State,
    string LocalAddress,
    int LocalPort,
    string PeerAddress,
    int PeerPort);
