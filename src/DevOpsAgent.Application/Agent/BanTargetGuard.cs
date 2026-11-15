using System.Net;
using System.Net.Sockets;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Domain.Servers;

namespace DevOpsAgent.Application.Agent;

/// <summary>Addresses that must never be banned: the app host's own, every inventory (and so every jump) host, and the operator's.</summary>
public interface IProtectedAddressSource
{
    Task<IReadOnlyCollection<IPAddress>> GetAsync(IReadOnlyList<Server> inventory, CancellationToken cancellationToken);
}

/// <summary>Addresses the signed-in operator has used to reach the app (recorded per authenticated request).</summary>
public interface IOperatorAddressRegistry
{
    void Record(IPAddress address);
    IReadOnlyCollection<IPAddress> Snapshot();
}

/// <summary>
/// Refuses a <c>block_ip</c> that would lock out the app, a managed server, a jump host, or the operator (D-030). Checked when the
/// action is proposed and again when it is approved, because inventory and operator addresses can change in between.
/// </summary>
public sealed class BanTargetGuard(IProtectedAddressSource protectedAddresses, IOperatorAddressRegistry operators)
{
    public async Task<string?> GetRefusalAsync(string address, IReadOnlyList<Server> inventory, CancellationToken cancellationToken)
    {
        if (!IPAddress.TryParse(address, out var parsed)) return "The address is not a valid IP address.";
        var target = Normalize(parsed);
        if (IPAddress.IsLoopback(target) || target.Equals(IPAddress.Any) || target.Equals(IPAddress.IPv6Any)
            || target.Equals(IPAddress.Broadcast) || target.Equals(IPAddress.None) || IsMulticast(target))
            return $"{address} is a loopback, unspecified or multicast address and cannot be banned.";
        if (operators.Snapshot().Select(Normalize).Contains(target))
            return $"{address} is an address you are using to reach DevOps Agent and cannot be banned.";
        if ((await protectedAddresses.GetAsync(inventory, cancellationToken)).Select(Normalize).Contains(target))
            return $"{address} belongs to DevOps Agent's host or to a server in the inventory (including jump hosts) and cannot be banned.";
        return null;
    }

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static bool IsMulticast(IPAddress address) => address.AddressFamily == AddressFamily.InterNetworkV6
        ? address.IsIPv6Multicast
        : address.GetAddressBytes()[0] is >= 224 and <= 239;
}
