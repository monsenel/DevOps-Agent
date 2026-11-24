using DevOpsAgent.Application.Abstractions;

namespace DevOpsAgent.Application.Servers;

/// <summary>What the UI shows for a server that is in backoff: its kind (for the badge) and the full text.</summary>
public sealed record ServerReachabilityStatus(
    HostFailureKind Kind,
    string Text,
    bool AutomaticRetriesPaused = false,
    bool IsHostKeyMismatch = false)
{
    public const string AuthFailedPausedBadge = "Auth failed — automatic retries paused";
    public const string CommandTimeoutBadge = "Not responding";
    public const string HostKeyMismatchBadge = "Host key mismatch";

    /// <summary>
    /// The short badge: a failed login is "Auth failed", a host whose commands time out is "Not responding" (T7.R21),
    /// a host-key mismatch (including on a jump host) is "Host key mismatch" (T7.R29), and a real outage is "Unreachable".
    /// </summary>
    public string Badge => IsHostKeyMismatch ? HostKeyMismatchBadge
        : Kind switch
        {
            HostFailureKind.AuthFailed => AutomaticRetriesPaused ? AuthFailedPausedBadge : "Auth failed",
            HostFailureKind.CommandTimeout => CommandTimeoutBadge,
            _ => "Unreachable"
        };
}

/// <summary>How a server's reachability reads in the UI: its own backoff, or the backoff of a jump host on its route.</summary>
public static class ServerReachability
{
    /// <returns>Null when the server is not known to be unreachable; otherwise the "unreachable (since …, next retry …)" or "authentication failed (…)" text.</returns>
    public static string? Describe(Guid serverId, IReadOnlyDictionary<Guid, (string Name, Guid? JumpServerId)> inventory,
        IHostReachability reachability) => Status(serverId, inventory, reachability)?.Text;

    /// <returns>Null when the server is not known to be unreachable; otherwise the kind and text of the state that explains it.</returns>
    public static ServerReachabilityStatus? Status(Guid serverId, IReadOnlyDictionary<Guid, (string Name, Guid? JumpServerId)> inventory,
        IHostReachability reachability)
    {
        if (!inventory.TryGetValue(serverId, out var server)) return null;
        if (reachability.Get(serverId) is { } own)
            return new ServerReachabilityStatus(own.Kind, HostUnreachableException.Describe(server.Name, own),
                own.AutomaticRetriesPaused, SshFailures.HostKeyMismatch(own.LastFailure) is not null);

        var visited = new HashSet<Guid> { serverId };
        var jumpId = server.JumpServerId;
        while (jumpId is Guid hop && visited.Add(hop) && inventory.TryGetValue(hop, out var jump))
        {
            if (reachability.Get(hop) is { } jumpState)
                return new ServerReachabilityStatus(jumpState.Kind, HostUnreachableException.Describe(server.Name, jumpState, jump.Name),
                    jumpState.AutomaticRetriesPaused, SshFailures.HostKeyMismatch(jumpState.LastFailure) is not null);
            jumpId = jump.JumpServerId;
        }

        return null;
    }
}
