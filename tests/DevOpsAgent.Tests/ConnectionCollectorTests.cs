using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Infrastructure.Collectors;

namespace DevOpsAgent.Tests;

public sealed class ConnectionCollectorTests
{
    [Fact]
    public void Parse_FixtureReadsHostAndContainerIpv4Ipv6EstablishedPairs()
    {
        var collectedAt = DateTimeOffset.Parse("2026-11-14T08:00:00Z");
        var result = ConnectionCollectorParser.Parse(Fixture(),
            new Dictionary<string, string> { ["api-svc"] = "ESTAB 0 0 172.20.0.10:8080 172.20.0.12:5432\n" },
            ["One namespace was skipped."], collectedAt);

        Assert.Equal(collectedAt, result.CollectedAtUtc);
        Assert.Equal(3, result.Connections.Count);
        Assert.Contains(result.Connections, item => item.Namespace == "host" && item.Protocol == "tcp"
            && item.LocalAddress == "198.51.100.10" && item.LocalPort == 53000
            && item.PeerAddress == "203.0.113.20" && item.PeerPort == 443);
        Assert.Contains(result.Connections, item => item.Namespace == "host" && item.LocalAddress == "2001:db8::10"
            && item.PeerAddress == "2001:db8::20" && item.PeerPort == 5432);
        Assert.Contains(result.Connections, item => item.Namespace == "container" && item.ContainerName == "api-svc"
            && item.LocalAddress == "172.20.0.10" && item.PeerPort == 5432);
        Assert.Equal("One namespace was skipped.", Assert.Single(result.Notes));
    }

    [Fact]
    public void Parse_FixtureReadsEstablishedOutputWithoutStateColumn()
    {
        var result = ConnectionCollectorParser.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ss-established-no-state.txt")));

        Assert.Equal(4, result.Connections.Count);
        Assert.Contains(result.Connections, item => item.State == "ESTAB" && item.LocalAddress == "172.26.0.4"
            && item.LocalPort == 57006 && item.PeerAddress == "172.26.0.3" && item.PeerPort == 9200);
        Assert.Contains(result.Connections, item => item.State == "ESTAB" && item.LocalAddress == "172.26.0.2"
            && item.LocalPort == 5432 && item.PeerAddress == "172.26.0.4" && item.PeerPort == 45640);
        Assert.Contains(result.Connections, item => item.LocalAddress == "2001:db8::10"
            && item.PeerAddress == "2001:db8::20" && item.PeerPort == 5432);
        Assert.Contains(result.Connections, item => item.LocalAddress == "172.17.0.2"
            && item.LocalPort == 80 && item.PeerAddress == "172.26.0.5" && item.PeerPort == 58486);
    }

    [Fact]
    public async Task CollectAsync_UsesReadOnlyEstablishedSocketCommand()
    {
        var runner = new RecordingSshRunner();
        var collector = new ConnectionCollector();
        var context = new ServerContext(new Server("edge", "edge.test", 22, "deploy", "key", "prod"), runner);

        Assert.True(await collector.IsApplicableAsync(context, CancellationToken.None));
        var output = await collector.CollectAsync(context, CancellationToken.None);
        var parsed = collector.Parse(output);

        Assert.Equal(["command -v ss >/dev/null 2>&1", "ss -tnH state established", "printf '%s' \"$SSH_CLIENT\""], runner.Commands);
        Assert.All(runner.Calls, call => Assert.False(call.Options.UseSudo));
        Assert.True(parsed.IsSuccess, parsed.Error);
        Assert.Equal(2, parsed.Data!.Value.GetProperty("Connections").GetArrayLength());
    }

    [Fact]
    public async Task CollectAsync_ListsInboundSshSessionsAndKeepsTheAgentsOwnAsAgent()
    {
        var ss = string.Join('\n',
            "ESTAB 0 0 10.88.10.5:22 10.88.10.1:57980",
            "ESTAB 0 0 10.88.10.5:22 10.88.10.1:57990", // a second agent connection from the same address
            "ESTAB 0 0 10.88.10.5:22 203.0.113.50:40000", // another client's SSH
            "ESTAB 0 0 10.88.10.5:22 203.0.113.50:40001",
            "ESTAB 0 0 10.88.10.5:22 203.0.113.51:40002",
            "ESTAB 0 0 10.88.10.5:443 10.88.10.1:41000", // same address, another port stays an edge
            "ESTAB 0 0 [::ffff:10.88.10.5]:22 [::ffff:10.88.10.1]:57995") + "\n"; // IPv4-mapped form of the agent
        var runner = new RecordingSshRunner(ss, "10.88.10.1 57980 22");
        var collector = new ConnectionCollector();
        var context = new ServerContext(new Server("edge", "edge.test", 22, "deploy", "key", "lab"), runner);

        var output = await collector.CollectAsync(context, CancellationToken.None);
        var parsed = collector.Parse(output);

        Assert.True(parsed.IsSuccess, parsed.Error);
        var result = parsed.Data!.Value.Deserialize<ConnectionDiscoveryResult>()!;
        var remaining = Assert.Single(result.Connections);
        Assert.Equal(443, remaining.LocalPort);
        Assert.Equal(
            [new SshSessionInfo("10.88.10.1", 22, 3, true), new SshSessionInfo("203.0.113.50", 22, 2, false),
                new SshSessionInfo("203.0.113.51", 22, 1, false)],
            result.SshSessions);
        Assert.Empty(result.Notes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-address 1 22")]
    public async Task CollectAsync_WithoutUsableSshClientListsSessionsButFlagsNoAgent(string sshClient)
    {
        var runner = new RecordingSshRunner("ESTAB 0 0 10.88.10.5:22 10.88.10.1:57980\n", sshClient);
        var collector = new ConnectionCollector();
        var context = new ServerContext(new Server("edge", "edge.test", 22, "deploy", "key", "lab"), runner);

        var output = await collector.CollectAsync(context, CancellationToken.None);
        var parsed = collector.Parse(output);

        Assert.True(parsed.IsSuccess, parsed.Error);
        var result = parsed.Data!.Value.Deserialize<ConnectionDiscoveryResult>()!;
        Assert.Empty(result.Connections);
        Assert.Equal([new SshSessionInfo("10.88.10.1", 22, 1, false)], result.SshSessions);
        Assert.Empty(result.Notes);
    }

    [Fact]
    public async Task CollectAsync_SshClientCommandFailureIsNotFatal()
    {
        var runner = new RecordingSshRunner("ESTAB 0 0 10.88.10.5:22 10.88.10.1:57980\n", "10.88.10.1 57980 22", failSshClient: true);
        var collector = new ConnectionCollector();
        var context = new ServerContext(new Server("edge", "edge.test", 22, "deploy", "key", "lab"), runner);

        var output = await collector.CollectAsync(context, CancellationToken.None);
        var parsed = collector.Parse(output);

        Assert.Equal(0, output.ExitCode);
        Assert.True(parsed.IsSuccess, parsed.Error);
        Assert.Equal([new SshSessionInfo("10.88.10.1", 22, 1, false)],
            parsed.Data!.Value.Deserialize<ConnectionDiscoveryResult>()!.SshSessions);
    }

    [Fact]
    public void Parse_NormalisesIpv4MappedAndUppercaseIpv6SourcesAndSplitsByServerPort()
    {
        var result = ConnectionCollectorParser.Parse(
            string.Join('\n',
                "ESTAB 0 0 [::ffff:10.88.10.5]:22 [::ffff:10.88.10.1]:57980",
                "ESTAB 0 0 10.88.10.5:22 10.88.10.1:57981",
                "ESTAB 0 0 [2001:DB8::A]:22 [2001:DB8::FF]:57982",
                "ESTAB 0 0 10.88.10.5:2222 10.88.10.1:57983") + "\n",
            agentSshClient: "::ffff:10.88.10.1 57980",
            sshdPorts: [2222]);

        Assert.Empty(result.Connections);
        Assert.Equal(
            [new SshSessionInfo("10.88.10.1", 22, 2, true), new SshSessionInfo("10.88.10.1", 2222, 1, false),
                new SshSessionInfo("2001:db8::ff", 22, 1, false)],
            result.SshSessions);
        Assert.Empty(result.Notes);
    }

    [Fact]
    public void Parse_TreatsSshdListenerPortsAndTheAgentServerPortAsSshAndLeavesContainersAlone()
    {
        var result = ConnectionCollectorParser.Parse(
            string.Join('\n',
                "ESTAB 0 0 10.88.10.5:2222 203.0.113.9:50000", // sshd listens on 2222
                "ESTAB 0 0 10.88.10.5:2200 [2001:db8::1]:57980", // the agent's server port
                "ESTAB 0 0 10.88.10.5:8080 203.0.113.9:50001") + "\n", // not an SSH port
            new Dictionary<string, string> { ["bastion"] = "ESTAB 0 0 172.20.0.2:22 203.0.113.9:40000\n" },
            agentSshClient: "2001:db8::1 57980 2200",
            sshdPorts: [2222]);

        Assert.Equal(2, result.Connections.Count);
        Assert.Contains(result.Connections, item => item.Namespace == "host" && item.LocalPort == 8080);
        Assert.Contains(result.Connections, item => item.Namespace == "container" && item.ContainerName == "bastion"
            && item.LocalPort == 22);
        Assert.Equal(
            [new SshSessionInfo("2001:db8::1", 2200, 1, true), new SshSessionInfo("203.0.113.9", 2222, 1, false)],
            result.SshSessions);
    }

    [Fact]
    public void Parse_OutboundConnectionsToPort22StayOrdinaryConnections()
    {
        var result = ConnectionCollectorParser.Parse("ESTAB 0 0 10.88.10.5:51000 10.88.10.9:22\n");

        var connection = Assert.Single(result.Connections);
        Assert.Equal(22, connection.PeerPort);
        Assert.Empty(result.SshSessions);
    }

    [Fact]
    public void Parse_CapsSshSourcesAndSumsTheRestIntoANote()
    {
        var lines = Enumerable.Range(1, 55)
            .Select(index => $"ESTAB 0 0 10.88.10.5:22 203.0.113.{index}:40000");
        var agentLine = "ESTAB 0 0 10.88.10.5:22 198.51.100.1:40000";
        var extra = "ESTAB 0 0 10.88.10.5:22 203.0.113.1:40001"; // 203.0.113.1 has two sessions and survives the cap
        var result = ConnectionCollectorParser.Parse(string.Join('\n', lines.Append(agentLine).Append(extra)) + "\n",
            agentSshClient: "198.51.100.1 40000 22");

        Assert.Equal(ConnectionCollectorParser.MaxSshSources, result.SshSessions.Count);
        Assert.Contains(result.SshSessions, session => session.IsAgent && session.SourceAddress == "198.51.100.1");
        Assert.Contains(result.SshSessions, session => session.SourceAddress == "203.0.113.1" && session.Count == 2);
        // 56 groups, 6 dropped (all with one session)
        Assert.Equal("6 more SSH sources with 6 sessions are not listed.", Assert.Single(result.Notes));
    }

    [Fact]
    public async Task CollectAsync_NetstatFallbackListsSshSessionsOfTheAgent()
    {
        var runner = new NoSsRunner(sshClient: "192.168.20.77 60412 22");
        var collector = new ConnectionCollector();
        var context = new ServerContext(new Server("alpine", "alpine.test", 22, "deploy", "key", "staging"), runner);

        var output = await collector.CollectAsync(context, CancellationToken.None);
        var parsed = collector.Parse(output);

        Assert.True(parsed.IsSuccess, parsed.Error);
        var result = parsed.Data!.Value.Deserialize<ConnectionDiscoveryResult>()!;
        Assert.Equal(3, result.Connections.Count);
        Assert.Equal([new SshSessionInfo("192.168.20.77", 22, 1, true)], result.SshSessions);
    }

    [Fact]
    public void ResultStoredBeforeSshSessionsDeserializesWithAnEmptyList()
    {
        var json = "{\"Connections\":[{\"Namespace\":\"host\",\"ContainerName\":null,\"Protocol\":\"tcp\",\"State\":\"ESTAB\","
            + "\"LocalAddress\":\"10.0.0.1\",\"LocalPort\":22,\"PeerAddress\":\"10.0.0.2\",\"PeerPort\":5000}],"
            + "\"Notes\":[],\"CollectedAtUtc\":\"2026-11-14T08:00:00+00:00\"}";

        var result = JsonSerializer.Deserialize<ConnectionDiscoveryResult>(json)!;
        var withNull = JsonSerializer.Deserialize<ConnectionDiscoveryResult>(json.Replace("\"Notes\":[]", "\"Notes\":[],\"SshSessions\":null"))!;
        var roundTrip = JsonSerializer.Deserialize<ConnectionDiscoveryResult>(JsonSerializer.Serialize(
            new ConnectionDiscoveryResult([], [], null, [new SshSessionInfo("10.0.0.2", 22, 3, true)])))!;

        Assert.Single(result.Connections);
        Assert.Empty(result.SshSessions);
        Assert.Empty(withNull.SshSessions);
        Assert.Equal(new SshSessionInfo("10.0.0.2", 22, 3, true), Assert.Single(roundTrip.SshSessions));
    }

    [Fact]
    public void RawOutputWithoutAgentSshClientStillParses()
    {
        var raw = "{\"HostOutput\":\"ESTAB 0 0 10.0.0.1:22 10.0.0.2:5000\\n\",\"ContainerOutputs\":{},\"Notes\":[],"
            + "\"CollectedAtUtc\":\"2026-11-14T08:00:00+00:00\"}";

        var parsed = new ConnectionCollector().Parse(new CollectorOutput(raw, string.Empty, 0, TimeSpan.Zero));

        Assert.True(parsed.IsSuccess, parsed.Error);
        Assert.Equal(0, parsed.Data!.Value.GetProperty("Connections").GetArrayLength());
        var session = Assert.Single(parsed.Data!.Value.Deserialize<ConnectionDiscoveryResult>()!.SshSessions);
        Assert.Equal(new SshSessionInfo("10.0.0.2", 22, 1, false), session);
    }

    [Fact]
    public void Parse_BusyBoxNetstatFixtureReadsOnlyEstablishedTcpConnections()
    {
        var result = ConnectionCollectorParser.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "busybox-netstat-tn.txt")));

        Assert.Equal(3, result.Connections.Count);
        var session = Assert.Single(result.SshSessions);
        Assert.Equal(new SshSessionInfo("192.168.20.77", 22, 1, false), session);
        Assert.All(result.Connections, item => Assert.Equal("ESTAB", item.State));
        Assert.Contains(result.Connections, item => item.Namespace == "host" && item.Protocol == "tcp"
            && item.LocalAddress == "192.168.20.5" && item.LocalPort == 80
            && item.PeerAddress == "192.168.20.9" && item.PeerPort == 51234);
        Assert.Contains(result.Connections, item => item.LocalPort == 43210 && item.PeerAddress == "10.10.0.4"
            && item.PeerPort == 5432);
        Assert.Contains(result.Connections, item => item.PeerPort == 51236 && item.LocalAddress == "192.168.20.5"
            && item.PeerAddress == "192.168.20.9");
        Assert.DoesNotContain(result.Connections, item => item.PeerPort == 51240);
    }

    [Fact]
    public async Task CollectAsync_FallsBackToNetstatWhenSsIsMissing()
    {
        var runner = new NoSsRunner();
        var collector = new ConnectionCollector();
        var context = new ServerContext(new Server("alpine", "alpine.test", 22, "deploy", "key", "staging"), runner);

        Assert.True(await collector.IsApplicableAsync(context, CancellationToken.None));
        var output = await collector.CollectAsync(context, CancellationToken.None);
        var parsed = collector.Parse(output);

        Assert.Equal(
            ["command -v ss >/dev/null 2>&1", "command -v netstat >/dev/null 2>&1", "ss -tnH state established", "netstat -tn",
                "printf '%s' \"$SSH_CLIENT\""],
            runner.Commands);
        Assert.Equal(0, output.ExitCode);
        Assert.True(parsed.IsSuccess, parsed.Error);
        Assert.Equal(3, parsed.Data!.Value.GetProperty("Connections").GetArrayLength());
        Assert.Equal(1, parsed.Data!.Value.GetProperty("SshSessions").GetArrayLength());
    }

    [Fact]
    public async Task IsApplicableAsync_IsFalseWithoutSsAndNetstat()
    {
        var context = new ServerContext(new Server("bare", "bare.test", 22, "deploy", "key", "staging"), new NoSsRunner(hasNetstat: false));

        Assert.False(await new ConnectionCollector().IsApplicableAsync(context, CancellationToken.None));
    }

    [Fact]
    public void NamespaceNetstatCommand_IsOnTheAppSudoList()
    {
        Assert.Equal("nsenter -t 42 -n netstat -tn", ConnectionCollector.NamespaceNetstatCommand(42));
        Assert.True(DevOpsAgent.Infrastructure.Monitoring.AppSudoCommands.IsAppCommand(
            "/usr/bin/sh -c 'nsenter -t 42 -n netstat -tn'"));
        Assert.False(DevOpsAgent.Infrastructure.Monitoring.AppSudoCommands.IsAppCommand(
            "/usr/bin/sh -c 'nsenter -t 42 -n netstat -tn; id'"));
    }

    private sealed class NoSsRunner(bool hasNetstat = true, string sshClient = "") : ISshRunner
    {
        public List<string> Commands { get; } = [];

        public Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options,
            CancellationToken cancellationToken)
        {
            Commands.Add(command);
            var (exitCode, output, error) = command switch
            {
                "command -v ss >/dev/null 2>&1" => (1, string.Empty, string.Empty),
                "command -v netstat >/dev/null 2>&1" => (hasNetstat ? 0 : 1, string.Empty, string.Empty),
                "ss -tnH state established" => (127, string.Empty, "sh: ss: not found"),
                "netstat -tn" => (0, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "busybox-netstat-tn.txt")), string.Empty),
                ConnectionCollector.SshClientCommand => (0, sshClient, string.Empty),
                _ => (0, string.Empty, string.Empty)
            };
            return Task.FromResult(new RemoteCommandResult(exitCode, output, error, TimeSpan.FromMilliseconds(1)));
        }
    }

    private static string Fixture() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ss-established.txt"));

    private sealed class RecordingSshRunner(string? hostOutput = null, string sshClient = "", bool failSshClient = false) : ISshRunner
    {
        public List<(string Command, SshRunOptions Options)> Calls { get; } = [];
        public IReadOnlyList<string> Commands => Calls.Select(call => call.Command).ToArray();

        public Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options,
            CancellationToken cancellationToken)
        {
            Calls.Add((command, options));
            if (command == ConnectionCollector.SshClientCommand)
            {
                if (failSshClient) throw new InvalidOperationException("channel closed");
                return Task.FromResult(new RemoteCommandResult(0, sshClient, string.Empty, TimeSpan.FromMilliseconds(1)));
            }

            var output = command == "ss -tnH state established" ? hostOutput ?? Fixture() : string.Empty;
            return Task.FromResult(new RemoteCommandResult(0, output, string.Empty, TimeSpan.FromMilliseconds(1)));
        }
    }
}
