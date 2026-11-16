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

    [Fact]
    public void ParseIpOnelineAddr_BusyBoxFixtureReadsAddressesPrefixesSubnetsScopesAndBroadcast()
    {
        var interfaces = HostCollectorParser.ParseIpOnelineAddr(Fixture("busybox-ip-o-addr.txt"));

        Assert.Equal(new[] { "lo", "eth0", "docker0" }, interfaces.Select(item => item.Name));
        Assert.Equal(new[] { 1, 2, 3 }, interfaces.Select(item => item.Index));
        Assert.All(interfaces, item =>
        {
            Assert.Null(item.MacAddress);
            Assert.Null(item.State);
            Assert.Null(item.Mtu);
        });

        var eth0 = interfaces.Single(item => item.Name == "eth0");
        var ipv4 = Assert.Single(eth0.Addresses, address => address.Family == "inet");
        Assert.Equal("192.168.20.5", ipv4.Address);
        Assert.Equal(24, ipv4.PrefixLength);
        Assert.Equal("192.168.20.0/24", ipv4.SubnetCidr);
        Assert.Equal("global", ipv4.Scope);
        Assert.Equal("192.168.20.255", ipv4.Broadcast);
        Assert.Contains(eth0.Addresses, address => address.Family == "inet6" && address.Address == "fd00:20::5"
            && address.PrefixLength == 64 && address.SubnetCidr == "fd00:20::/64" && address.Scope == "global");
        Assert.Contains(eth0.Addresses, address => address.Family == "inet6" && address.Scope == "link"
            && address.Address == "fe80::5054:ff:fe12:3456" && address.Broadcast is null);

        var lo = interfaces.Single(item => item.Name == "lo");
        Assert.Contains(lo.Addresses, address => address.Address == "127.0.0.1" && address.PrefixLength == 8
            && address.Scope == "host" && address.Broadcast is null);
        Assert.Contains(lo.Addresses, address => address.Address == "::1" && address.PrefixLength == 128);
        Assert.Equal("172.17.0.0/16", interfaces.Single(item => item.Name == "docker0").Addresses.Single().SubnetCidr);
    }

    [Fact]
    public void ParseIpOnelineAddr_IproutePointToPointAndLinkLinesFillWhatTheyCarry()
    {
        var interfaces = HostCollectorParser.ParseIpOnelineAddr(string.Join('\n',
            "2: eth0: <BROADCAST,MULTICAST,UP,LOWER_UP> mtu 1450 qdisc fq_codel state UP group default qlen 1000\\    link/ether 52:54:00:12:34:56 brd ff:ff:ff:ff:ff:ff",
            "2: eth0    inet 10.0.0.2/24 brd 10.0.0.255 scope global dynamic eth0\\       valid_lft 3000sec preferred_lft 3000sec",
            "4: tun0    inet 10.8.0.1 peer 10.8.0.2/32 scope global tun0\\       valid_lft forever preferred_lft forever",
            "garbage line",
            "5: bad    inet 999.1.1.1/24 scope global bad\\"));

        var eth0 = interfaces.Single(item => item.Name == "eth0");
        Assert.Equal("52:54:00:12:34:56", eth0.MacAddress);
        Assert.Equal("UP", eth0.State);
        Assert.Equal(1450, eth0.Mtu);
        Assert.Equal("10.0.0.0/24", Assert.Single(eth0.Addresses).SubnetCidr);

        var tun = Assert.Single(interfaces.Single(item => item.Name == "tun0").Addresses);
        Assert.Equal("10.8.0.1", tun.Address);
        Assert.Equal(32, tun.PrefixLength);
        Assert.Empty(interfaces.Single(item => item.Name == "bad").Addresses);
    }

    [Fact]
    public void ParseIpRouteText_BusyBoxFixtureReadsDefaultAndConnectedRoutes()
    {
        var routes = HostCollectorParser.ParseIpRouteText(Fixture("busybox-ip-route.txt"));

        Assert.Equal(3, routes.Count);
        var gateway = Assert.Single(routes, route => route.IsDefault);
        Assert.Equal("inet", gateway.Family);
        Assert.Equal("default", gateway.Destination);
        Assert.Equal("192.168.20.1", gateway.Gateway);
        Assert.Equal("eth0", gateway.InterfaceName);
        Assert.Equal(202, gateway.Metric);

        var lan = routes.Single(route => route.Destination == "192.168.20.0/24");
        Assert.False(lan.IsDefault);
        Assert.Null(lan.Gateway);
        Assert.Equal("eth0", lan.InterfaceName);
        Assert.Equal("link", lan.Scope);
        Assert.Equal("192.168.20.5", lan.PreferredSource);
        Assert.Equal(202, lan.Metric);
        Assert.Null(routes.Single(route => route.Destination == "172.17.0.0/16").Metric);
    }

    [Fact]
    public void ParseIpRouteText_HandlesIproute2ExtrasAndIpv6()
    {
        var routes = HostCollectorParser.ParseIpRouteText(string.Join('\n',
            "default via 10.0.0.1 dev eth0 proto dhcp src 10.0.0.2 metric 100",
            "blackhole 10.9.0.0/24",
            "fe80::/64 dev eth0 proto kernel metric 256 pref medium",
            "default via fe80::1 dev eth0 metric 1024"));

        Assert.Equal(4, routes.Count);
        Assert.Equal("dhcp", routes[0].Protocol);
        Assert.Equal("10.9.0.0/24", routes[1].Destination);
        Assert.Equal("inet6", routes[2].Family);
        Assert.Equal(256, routes[2].Metric);
        Assert.True(routes[3].IsDefault);
        Assert.Equal("inet6", routes[3].Family);
    }

    [Fact]
    public void Parse_BusyBoxTextEquivalentToIpJsonForTheSameHost()
    {
        var result = HostCollectorParser.Parse(Fixture("busybox-ip-o-addr.txt"), Fixture("busybox-ip-route.txt"),
            "alpine-01\n", "127.0.0.1 localhost\n");

        Assert.Equal("alpine-01", result.Hostname);
        Assert.Equal(3, result.Interfaces.Count);
        Assert.Equal(3, result.Routes.Count);
        Assert.Contains(result.HostsEntries, entry => entry.Address == "127.0.0.1");
        Assert.False(HostCollectorParser.IsJson(Fixture("busybox-ip-o-addr.txt")));
        Assert.True(HostCollectorParser.IsJson("  [\n]"));
    }

    [Fact]
    public async Task CollectAsync_FallsBackToTextIpCommandsWhenBusyBoxIpRejectsJson()
    {
        const string usage = "BusyBox v1.37.0 (2025-12-16 14:19:28 UTC) multi-call binary.\n\nUsage: ip [OPTIONS] address|route|link|tunnel|neigh|rule [ARGS]\n";
        var runner = new BusyBoxSshRunner(usage);
        var server = new Server("alpine", "alpine.example.internal", 22, "deploy", "id_ed25519", "staging");
        var collector = new HostCollector();
        var context = new ServerContext(server, runner);

        var output = await collector.CollectAsync(context, CancellationToken.None);
        var parsed = collector.Parse(output);

        Assert.Equal(0, output.ExitCode);
        Assert.Equal(string.Empty, output.StandardError);
        Assert.Equal(
            ["ip -j addr", "ip -o addr show", "ip -j route", "ip route show",
                "hostname 2>/dev/null || cat /proc/sys/kernel/hostname", "cat /etc/hosts"],
            runner.Commands);
        Assert.DoesNotContain("sudo", string.Join(' ', runner.Commands), StringComparison.OrdinalIgnoreCase);
        Assert.True(parsed.IsSuccess, parsed.Error);
        var data = parsed.Data!.Value;
        Assert.Equal("alpine-01", data.GetProperty("Hostname").GetString());
        var eth0 = data.GetProperty("Interfaces").EnumerateArray().Single(item => item.GetProperty("Name").GetString() == "eth0");
        Assert.Contains(eth0.GetProperty("Addresses").EnumerateArray(),
            address => address.GetProperty("Address").GetString() == "192.168.20.5");
        Assert.Contains(data.GetProperty("Routes").EnumerateArray(), route => route.GetProperty("IsDefault").GetBoolean());
    }

    [Fact]
    public async Task CollectAsync_FallsBackWhenJsonCommandSucceedsWithoutJsonAndReportsFailingFallback()
    {
        var runner = new BusyBoxSshRunner(usage: string.Empty, jsonExitCode: 0, jsonOutput: "not json", textFails: true);
        var server = new Server("alpine", "alpine.example.internal", 22, "deploy", "id_ed25519", "staging");

        var output = await new HostCollector().CollectAsync(new ServerContext(server, runner), CancellationToken.None);

        Assert.Equal(1, output.ExitCode);
        Assert.Contains("addresses: ip: boom", output.StandardError);
        Assert.Contains("routes: ip: boom", output.StandardError);
    }

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    /// <summary>A BusyBox host: `ip -j` prints the usage on stderr and exits 1; the text commands work.</summary>
    private sealed class BusyBoxSshRunner(string usage, int jsonExitCode = 1, string jsonOutput = "", bool textFails = false) : ISshRunner
    {
        public List<string> Commands { get; } = [];

        public Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options,
            CancellationToken cancellationToken)
        {
            Commands.Add(command);
            var result = command switch
            {
                "ip -j addr" or "ip -j route" => new RemoteCommandResult(jsonExitCode, jsonOutput, usage, TimeSpan.FromMilliseconds(1)),
                "ip -o addr show" or "ip route show" when textFails => new RemoteCommandResult(1, string.Empty, "ip: boom", TimeSpan.FromMilliseconds(1)),
                "ip -o addr show" => Ok(Fixture("busybox-ip-o-addr.txt")),
                "ip route show" => Ok(Fixture("busybox-ip-route.txt")),
                "hostname 2>/dev/null || cat /proc/sys/kernel/hostname" => Ok("alpine-01\n"),
                "cat /etc/hosts" => Ok("127.0.0.1 localhost\n"),
                _ => Ok(string.Empty)
            };
            return Task.FromResult(result);
        }

        private static RemoteCommandResult Ok(string output) => new(0, output, string.Empty, TimeSpan.FromMilliseconds(1));
    }

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
