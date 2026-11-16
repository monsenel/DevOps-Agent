using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Infrastructure.Collectors;
using DevOpsAgent.Infrastructure.Monitoring;

namespace DevOpsAgent.Tests;

public sealed class ListenerCollectorTests
{
    [Fact]
    public void Parse_FixtureHandlesWildcardIpv6ScopedAddressesAndMissingProcessInfo()
    {
        var listeners = ListenerCollectorParser.Parse(Fixture());

        Assert.Equal(5, listeners.Count);
        Assert.Contains(listeners, item => item.Protocol == "tcp" && item.Address == "0.0.0.0" &&
            item.Port == 22 && item.ProcessName == "sshd" && item.ProcessId == 1000);
        Assert.Contains(listeners, item => item.Protocol == "tcp" && item.Address == "::" &&
            item.Port == 80 && item.ProcessName == "nginx" && item.ProcessId == 220);
        Assert.Contains(listeners, item => item.Protocol == "udp" && item.Address == "*" && item.Port == 53);
        Assert.Contains(listeners, item => item.Protocol == "udp" && item.Address == "fe80::1%eth0" &&
            item.Port == 546 && item.ProcessName == "NetworkManager");

        var withoutProcess = listeners.Single(item => item.Port == 5432);
        Assert.Equal("127.0.0.1", withoutProcess.Address);
        Assert.Null(withoutProcess.ProcessName);
        Assert.Null(withoutProcess.ProcessId);
    }

    [Fact]
    public async Task CollectAsync_UsesSudoOnlyWhenCapabilityIsKnown()
    {
        var runner = new RecordingSshRunner();
        var collector = new ListenerCollector();
        var server = new Server("edge", "edge.example.internal", 22, "deploy", "id_ed25519", "prod");
        var context = new ServerContext(server, runner);

        Assert.True(await collector.IsApplicableAsync(context, CancellationToken.None));
        var output = await collector.CollectAsync(context, CancellationToken.None);
        var parsed = collector.Parse(output);

        Assert.False(runner.Calls[^1].Options.UseSudo);
        Assert.Equal("ss -tulpnH", runner.Calls[^1].Command);
        Assert.True(parsed.IsSuccess, parsed.Error);
        Assert.Equal(5, parsed.Data!.Value.GetProperty("Listeners").GetArrayLength());

        server.RecordCapabilities(sudoCapable: true, osInfo: null, DateTimeOffset.UtcNow);
        await collector.CollectAsync(new ServerContext(server, runner), CancellationToken.None);
        Assert.True(runner.Calls[^1].Options.UseSudo);
    }

    [Fact]
    public void Parse_BusyBoxNetstatFixtureReadsAddressPortProtocolProcessAndPid()
    {
        var listeners = ListenerCollectorParser.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "busybox-netstat-tulpn.txt")));

        Assert.Equal(7, listeners.Count);
        Assert.Contains(listeners, item => item.Protocol == "tcp" && item.Address == "0.0.0.0" && item.Port == 80
            && item.State == "LISTEN" && item.ProcessName == "nginx" && item.ProcessId == 12 && item.PeerAddress == null);
        Assert.Contains(listeners, item => item.Address == "0.0.0.0" && item.Port == 443 && item.ProcessName == "nginx");
        Assert.Contains(listeners, item => item.Address == "::" && item.Port == 80 && item.ProcessName == "nginx"
            && item.ProcessId == 12);
        Assert.Contains(listeners, item => item.Port == 22 && item.Address == "0.0.0.0" && item.ProcessName == "sshd"
            && item.ProcessId == 9);
        Assert.Contains(listeners, item => item.Protocol == "udp" && item.Port == 68 && item.State == "UNCONN"
            && item.ProcessName == "init" && item.ProcessId == 1);

        var withoutProcess = listeners.Single(item => item.Port == 41234);
        Assert.Equal("127.0.0.11", withoutProcess.Address);
        Assert.Null(withoutProcess.ProcessName);
        Assert.Null(withoutProcess.ProcessId);
    }

    [Fact]
    public void Parse_NetstatHandlesTcp6ProtocolPathsAndUnprivilegedOutputWithoutProgramColumn()
    {
        var listeners = ListenerCollectorParser.Parse(string.Join('\n',
            "Proto Recv-Q Send-Q Local Address           Foreign Address         State       PID/Program name",
            "tcp6       0      0 :::8080                 :::*                    LISTEN      301//usr/sbin/app -x",
            "tcp        0      0 10.0.0.1:5432           0.0.0.0:*               LISTEN",
            "udp6       0      0 :::123                  :::*                                77/chronyd",
            "raw        0      0 0.0.0.0:1               0.0.0.0:*               7           -"));

        Assert.Equal(3, listeners.Count);
        var app = listeners.Single(item => item.Port == 8080);
        Assert.Equal("tcp", app.Protocol);
        Assert.Equal("::", app.Address);
        Assert.Equal("app", app.ProcessName);
        Assert.Equal(301, app.ProcessId);
        Assert.Null(listeners.Single(item => item.Port == 5432).ProcessName);
        Assert.Equal("udp", listeners.Single(item => item.Port == 123).Protocol);
    }

    [Fact]
    public async Task CollectAsync_FallsBackToNetstatWhenSsIsMissingAndKeepsSudoAndReadOnly()
    {
        var runner = new NetstatOnlyRunner();
        var collector = new ListenerCollector();
        var server = new Server("alpine", "alpine.example.internal", 22, "deploy", "id_ed25519", "staging");
        server.RecordCapabilities(sudoCapable: true, osInfo: null, DateTimeOffset.UtcNow);
        var context = new ServerContext(server, runner);

        Assert.True(await collector.IsApplicableAsync(context, CancellationToken.None));
        var output = await collector.CollectAsync(context, CancellationToken.None);
        var parsed = collector.Parse(output);

        Assert.Equal(
            ["command -v ss >/dev/null 2>&1", "command -v netstat >/dev/null 2>&1",
                "command -v ss >/dev/null 2>&1", "netstat -tulpn"],
            runner.Calls.Select(call => call.Command));
        Assert.True(runner.Calls[^1].Options.UseSudo);
        Assert.All(runner.Calls.Take(3), call => Assert.False(call.Options.UseSudo));
        Assert.True(parsed.IsSuccess, parsed.Error);
        var data = parsed.Data!.Value.GetProperty("Listeners");
        Assert.Equal(7, data.GetArrayLength());
        Assert.Contains(data.EnumerateArray(), item => item.GetProperty("ProcessName").GetString() == "nginx"
            && item.GetProperty("Port").GetInt32() == 80);
        Assert.True(AppSudoCommands.IsAppCommand("/usr/bin/sh -c '" + ListenerCollector.NetstatCommand + "'"));
        Assert.False(AppSudoCommands.IsAppCommand("/usr/bin/sh -c '" + ListenerCollector.NetstatCommand + "; id'"));
    }

    [Fact]
    public async Task IsApplicableAsync_IsFalseOnlyWhenNeitherSsNorNetstatExists()
    {
        var server = new Server("bare", "bare.example.internal", 22, "deploy", "id_ed25519", "staging");

        Assert.False(await new ListenerCollector().IsApplicableAsync(
            new ServerContext(server, new NetstatOnlyRunner(hasNetstat: false)), CancellationToken.None));
    }

    [Fact]
    public void Parse_NetstatWithoutProgramColumnKeepsListenersWithoutProcess()
    {
        var listeners = ListenerCollectorParser.Parse(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "busybox-netstat-tuln.txt")));

        Assert.Equal(4, listeners.Count);
        Assert.All(listeners, item => Assert.Null(item.ProcessName));
        Assert.All(listeners, item => Assert.Null(item.ProcessId));
        Assert.Contains(listeners, item => item.Protocol == "tcp" && item.State == "LISTEN" && item.Port == 80
            && item.Address == "0.0.0.0");
        Assert.Contains(listeners, item => item.Address == "::" && item.Port == 80);
        Assert.Contains(listeners, item => item.Protocol == "udp" && item.State == "UNCONN" && item.Port == 68);
    }

    [Fact]
    public async Task CollectAsync_FallsBackToNetstatWithoutProcessInfoWhenPIsAnInvalidOption()
    {
        var runner = new NetstatOnlyRunner(noProcessSupport: true);
        var collector = new ListenerCollector();
        var server = new Server("alpine", "alpine.example.internal", 22, "deploy", "id_ed25519", "staging");
        server.RecordCapabilities(sudoCapable: true, osInfo: null, DateTimeOffset.UtcNow);
        var context = new ServerContext(server, runner);

        var output = await collector.CollectAsync(context, CancellationToken.None);
        var parsed = collector.Parse(output);

        Assert.Equal(
            ["command -v ss >/dev/null 2>&1", "netstat -tulpn", "netstat -tuln"],
            runner.Calls.Select(call => call.Command));
        Assert.True(runner.Calls[1].Options.UseSudo);
        Assert.False(runner.Calls[2].Options.UseSudo);
        Assert.Equal(0, output.ExitCode);
        Assert.Equal(string.Empty, output.StandardError);
        Assert.True(parsed.IsSuccess, parsed.Error);
        var data = parsed.Data!.Value.GetProperty("Listeners");
        Assert.Equal(4, data.GetArrayLength());
        Assert.All(data.EnumerateArray(), item => Assert.Equal(System.Text.Json.JsonValueKind.Null, item.GetProperty("ProcessName").ValueKind));
    }

    [Fact]
    public async Task CollectAsync_DoesNotRetryNetstatWhenTheFullCommandSucceeds()
    {
        var runner = new NetstatOnlyRunner();
        var server = new Server("alpine", "alpine.example.internal", 22, "deploy", "id_ed25519", "staging");

        await new ListenerCollector().CollectAsync(new ServerContext(server, runner), CancellationToken.None);

        Assert.DoesNotContain(runner.Calls, call => call.Command == ListenerCollector.NetstatNoProcessCommand);
    }

    private sealed class NetstatOnlyRunner(bool hasNetstat = true, bool noProcessSupport = false) : ISshRunner
    {
        public List<(string Command, SshRunOptions Options)> Calls { get; } = [];

        public Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options,
            CancellationToken cancellationToken)
        {
            Calls.Add((command, options));
            string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
            var (exitCode, output, error) = command switch
            {
                "command -v ss >/dev/null 2>&1" => (1, string.Empty, string.Empty),
                "command -v netstat >/dev/null 2>&1" => (hasNetstat ? 0 : 1, string.Empty, string.Empty),
                "netstat -tulpn" when noProcessSupport => (1, string.Empty, Fixture("busybox-netstat-invalid-p.txt")),
                "netstat -tulpn" => (0, Fixture("busybox-netstat-tulpn.txt"), string.Empty),
                "netstat -tuln" => (0, Fixture("busybox-netstat-tuln.txt"), string.Empty),
                _ => (127, string.Empty, string.Empty)
            };
            return Task.FromResult(new RemoteCommandResult(exitCode, output, error, TimeSpan.FromMilliseconds(1)));
        }
    }

    private static string Fixture() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ss-tulpn.txt"));

    private sealed class RecordingSshRunner : ISshRunner
    {
        public List<(string Command, SshRunOptions Options)> Calls { get; } = [];

        public Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options,
            CancellationToken cancellationToken)
        {
            Calls.Add((command, options));
            var output = command == "ss -tulpnH" ? Fixture() : string.Empty;
            return Task.FromResult(new RemoteCommandResult(0, output, string.Empty, TimeSpan.FromMilliseconds(1)));
        }
    }
}
