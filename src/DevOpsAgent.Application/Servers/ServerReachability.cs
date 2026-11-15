using DevOpsAgent.Application.Abstractions;

namespace DevOpsAgent.Application.Servers;

/// <summary>How a server's reachability reads in the UI: its own backoff, or the backoff of a jump host on its route.</summary>
public static class ServerReachability
{
    /// <returns>Null when the server is not known to be unreachable; otherwise the "unreachable (since …, next retry …)" text.</returns>
    public static string? Describe(Guid serverId, IReadOnlyDictionary<Guid, (string Name, Guid? JumpServerId)> inventory,
        IHostReachability reachability)
    {
        if (!inventory.TryGetValue(serverId, out var server)) return null;
        if (reachability.Get(serverId) is { } own) return HostUnreachableException.Describe(server.Name, own);

        var visited = new HashSet<Guid> { serverId };
        var jumpId = server.JumpServerId;
        while (jumpId is Guid hop && visited.Add(hop) && inventory.TryGetValue(hop, out var jump))
        {
            if (reachability.Get(hop) is { } jumpState) return HostUnreachableException.Describe(server.Name, jumpState, jump.Name);
            jumpId = jump.JumpServerId;
        }

        return null;
    }
}
