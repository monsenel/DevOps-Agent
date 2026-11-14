using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Infrastructure.Collectors;

namespace DevOpsAgent.Tests;

public sealed class HostCollectorTests
{
    [Fact]
    public void Parse_FixturesIncludesInterfacesAddressFamiliesSubnetsAndDefaultRoutes()
    {
        var result = HostCollectorParser.Parse(
            Fixture("host-ip-addr.json"),
            Fixture("host-ip-route.json"),
            Fixture("host-hostname.txt"),
            Fixture("host-etc-hosts.txt"));

        Assert.Equal("edge-01", result.Hostname);
        Assert.Equal(new[] { "lo", "eth0" }, result.Interfaces.Select(item => item.Name));

        var eth0 = result.Interfaces.Single(item => item.Name == "eth0");
        Assert.Equal(1500, eth0.Mtu);
        Assert.Contains(eth0.Addresses, address =>
            address.Family == "inet" && address.Address == "192.0.2.10" && address.SubnetCidr == "192.0.2.0/24");
        Assert.Contains(eth0.Addresses, address =>
            address.Family == "inet6" && address.Address == "2001:db8:1::10" && address.SubnetCidr == "2001:db8:1::/64");
        Assert.Contains(eth0.Addresses, address => address.Address == "fe80::5054:ff:feab:cdef" && address.Scope == "link");

        var defaultRoutes = result.Routes.Where(route => route.IsDefault).ToArray();
        Assert.Equal(2, defaultRoutes.Length);
        Assert.Contains(defaultRoutes, route => route.Family == "inet" && route.Gateway == "192.0.2.1");
        Assert.Contains(defaultRoutes, route => route.Family == "inet6" && route.Gateway == "2001:db8:1::1");
        Assert.Contains(result.HostsEntries, entry => entry.Address == "192.0.2.20" && entry.Hostnames.Contains("api.internal"));
        Assert.DoesNotContain(result.HostsEntries, entry => entry.Address == "invalid");
    }

    [Fact]
    public async Task CollectAsync_RunsOnlyReadOnlyHostCommandsAndProducesParseableOutput()
    {
        var runner = new FixtureSshRunner();
        var server = new Server("edge", "edge.example.internal", 22, "deploy", "id_ed25519", "prod");
        var collector = new HostCollector();

        Assert.True(await collector.IsApplicableAsync(new ServerContext(server, runner), CancellationToken.None));
        var output = await collector.CollectAsync(new ServerContext(server, runner), CancellationToken.None);
        var parsed = collector.Parse(output);

        Assert.Equal(0, output.ExitCode);
        Assert.True(parsed.IsSuccess, parsed.Error);
        Assert.Equal(
            ["command -v ip >/dev/null 2>&1", "ip -j addr", "ip -j route",
                "hostname 2>/dev/null || cat /proc/sys/kernel/hostname", "cat /etc/hosts"],
            runner.Commands);
        Assert.DoesNotContain("sudo", string.Join(' ', runner.Commands), StringComparison.OrdinalIgnoreCase);
        Assert.True(parsed.Data.HasValue);
        Assert.Equal("edge-01", parsed.Data.Value.GetProperty("Hostname").GetString());
    }

    [Fact]
    public void Parse_InvalidJsonReturnsFailure()
    {
        var output = new CollectorOutput("{not json}", string.Empty, 0, TimeSpan.Zero);
        var result = new HostCollector().Parse(output);

        Assert.False(result.IsSuccess);
        Assert.Contains("Invalid host collector output", result.Error);
    }

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private sealed class FixtureSshRunner : ISshRunner
    {
        public List<string> Commands { get; } = [];

        public Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options,
            CancellationToken cancellationToken)
        {
            Commands.Add(command);
            var output = command switch
            {
                "ip -j addr" => Fixture("host-ip-addr.json"),
                "ip -j route" => Fixture("host-ip-route.json"),
                "hostname 2>/dev/null || cat /proc/sys/kernel/hostname" => Fixture("host-hostname.txt"),
                "cat /etc/hosts" => Fixture("host-etc-hosts.txt"),
                _ => string.Empty
            };
            return Task.FromResult(new RemoteCommandResult(0, output, string.Empty, TimeSpan.FromMilliseconds(1)));
        }
    }
}
