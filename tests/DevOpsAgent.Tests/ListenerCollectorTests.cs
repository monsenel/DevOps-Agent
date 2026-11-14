using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Infrastructure.Collectors;

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
