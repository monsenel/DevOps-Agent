namespace DevOpsAgent.Application.Discovery;

/// <param name="SshSessions">
/// Inbound SSH sessions seen on the host (T8.18). They are not part of <paramref name="Connections"/> and never become edges.
/// Results stored before T8.18 have no such property and deserialize to an empty list.
/// </param>
public sealed record ConnectionDiscoveryResult(
    IReadOnlyList<EstablishedConnectionInfo> Connections,
    IReadOnlyList<string> Notes,
    DateTimeOffset? CollectedAtUtc = null,
    IReadOnlyList<SshSessionInfo>? SshSessions = null)
{
    public IReadOnlyList<SshSessionInfo> SshSessions { get; init; } = SshSessions ?? [];
}

public sealed record EstablishedConnectionInfo(
    string Namespace,
    string? ContainerName,
    string Protocol,
    string State,
    string LocalAddress,
    int LocalPort,
    string PeerAddress,
    int PeerPort);

/// <summary>Established inbound SSH connections from one source address to one server port.</summary>
/// <param name="SourceAddress">Normalised (IPv4-mapped IPv6 turned into IPv4, lowercase) address of the client.</param>
/// <param name="ServerPort">The port on this server the sessions arrived on.</param>
/// <param name="Count">Number of established connections.</param>
/// <param name="IsAgent">The sessions belong to DevOps Agent itself (<c>$SSH_CLIENT</c> of its own session).</param>
public sealed record SshSessionInfo(string SourceAddress, int ServerPort, int Count, bool IsAgent);
