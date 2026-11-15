using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using DevOpsAgent.Application.Agent;
using DevOpsAgent.Domain.Servers;

namespace DevOpsAgent.Infrastructure.Security;

/// <summary>The app host's interface addresses plus every inventory host, resolved when it is a name (best effort).</summary>
public sealed class ProtectedAddressSource : IProtectedAddressSource
{
    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(3);

    public async Task<IReadOnlyCollection<IPAddress>> GetAsync(IReadOnlyList<Server> inventory, CancellationToken cancellationToken)
    {
        var result = new HashSet<IPAddress>();
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            foreach (var unicast in networkInterface.GetIPProperties().UnicastAddresses)
                result.Add(unicast.Address);
        }

        foreach (var host in inventory.Select(server => server.Host).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (IPAddress.TryParse(host, out var literal))
            {
                result.Add(literal);
                continue;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ResolveTimeout);
            try
            {
                foreach (var address in await Dns.GetHostAddressesAsync(host, timeout.Token))
                    result.Add(address);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                // A name the app cannot resolve (e.g. one only the jump host resolves) cannot be compared; servers are
                // still protected by their literal addresses and by the operator/host checks.
            }
        }

        return result;
    }
}

/// <summary>Bounded, process-wide record of the client addresses of authenticated requests (single operator).</summary>
public sealed class OperatorAddressRegistry : IOperatorAddressRegistry
{
    private const int Capacity = 32;
    private readonly ConcurrentDictionary<IPAddress, DateTimeOffset> _addresses = new();

    public void Record(IPAddress address)
    {
        _addresses[address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address] = DateTimeOffset.UtcNow;
        if (_addresses.Count <= Capacity) return;
        foreach (var stale in _addresses.OrderBy(item => item.Value).Take(_addresses.Count - Capacity))
            _addresses.TryRemove(stale.Key, out _);
    }

    public IReadOnlyCollection<IPAddress> Snapshot() => _addresses.Keys.ToArray();
}
