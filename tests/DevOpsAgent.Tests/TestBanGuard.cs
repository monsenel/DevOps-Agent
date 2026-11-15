using System.Net;
using DevOpsAgent.Application.Agent;
using DevOpsAgent.Domain.Servers;

namespace DevOpsAgent.Tests;

internal static class TestBanGuard
{
    public static BanTargetGuard Create(IEnumerable<IPAddress>? hostAddresses = null, IEnumerable<IPAddress>? operatorAddresses = null)
    {
        var operators = new Operators();
        foreach (var address in operatorAddresses ?? []) operators.Record(address);
        return new BanTargetGuard(new HostAddresses(hostAddresses?.ToArray() ?? []), operators);
    }

    // Like the real source: the app host's own addresses plus literal inventory hosts (no DNS in tests).
    private sealed class HostAddresses(IReadOnlyCollection<IPAddress> host) : IProtectedAddressSource
    {
        public Task<IReadOnlyCollection<IPAddress>> GetAsync(IReadOnlyList<Server> inventory, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyCollection<IPAddress>>(host.Concat(inventory
                .Select(server => IPAddress.TryParse(server.Host, out var address) ? address : null)
                .OfType<IPAddress>()).ToArray());
    }

    private sealed class Operators : IOperatorAddressRegistry
    {
        private readonly HashSet<IPAddress> _addresses = [];
        public void Record(IPAddress address) => _addresses.Add(address);
        public IReadOnlyCollection<IPAddress> Snapshot() => _addresses.ToArray();
    }
}
