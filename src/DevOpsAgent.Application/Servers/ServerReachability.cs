using DevOpsAgent.Application.Abstractions;

namespace DevOpsAgent.Application.Servers;

/// <summary>What the UI shows for a server that is in backoff: its kind (for the badge) and the full text.</summary>
public sealed record ServerReachabilityStatus(HostFailureKind Kind, string Text)
{
    /// <summary>The short badge: a failed login must not be called "Unreachable".</summary>
    public string Badge => Kind == HostFailureKind.AuthFailed ? "Auth failed" : "Unreachable";
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
            return new ServerReachabilityStatus(own.Kind, HostUnreachableException.Describe(server.Name, own));

        var visited = new HashSet<Guid> { serverId };
        var jumpId = server.JumpServerId;
        while (jumpId is Guid hop && visited.Add(hop) && inventory.TryGetValue(hop, out var jump))
        {
            if (reachability.Get(hop) is { } jumpState)
                return new ServerReachabilityStatus(jumpState.Kind, HostUnreachableException.Describe(server.Name, jumpState, jump.Name));
            jumpId = jump.JumpServerId;
        }

        return null;
    }
}
