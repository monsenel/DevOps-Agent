using System.Net;
using DevOpsAgent.Domain.Servers;

namespace DevOpsAgent.Tests;

public sealed class BanTargetGuardTests
{
    private static readonly Server Edge = new("srv-edge", "198.51.100.10", 22, "dva", "key", "staging");
    private static readonly Server App = new("srv-app", "10.0.0.5", 22, "dva", "key", "staging");

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("198.51.100.10")]          // inventory / jump host
    [InlineData("10.0.0.5")]               // inventory host behind the jump
    [InlineData("::ffff:10.0.0.5")]        // IPv4-mapped form of an inventory host
    [InlineData("192.0.2.200")]            // the app host's own interface
    [InlineData("203.0.113.50")]           // the operator's client address
    public async Task ProtectedAddressesAreRefused(string address)
    {
        var guard = TestBanGuard.Create([IPAddress.Parse("192.0.2.200")], [IPAddress.Parse("203.0.113.50")]);

        Assert.NotNull(await guard.GetRefusalAsync(address, [Edge, App], CancellationToken.None));
    }

    [Theory]
    [InlineData("203.0.113.9")]
    [InlineData("2001:db8::9")]
    public async Task OtherAddressesCanBeBanned(string address)
    {
        var guard = TestBanGuard.Create([IPAddress.Parse("192.0.2.200")], [IPAddress.Parse("203.0.113.50")]);

        Assert.Null(await guard.GetRefusalAsync(address, [Edge, App], CancellationToken.None));
    }
}
