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
    public async Task CollectAsync_UsesReadOnlyEstablishedSocketCommand()
    {
        var runner = new RecordingSshRunner();
        var collector = new ConnectionCollector();
        var context = new ServerContext(new Server("edge", "edge.test", 22, "deploy", "key", "prod"), runner);

        Assert.True(await collector.IsApplicableAsync(context, CancellationToken.None));
        var output = await collector.CollectAsync(context, CancellationToken.None);
        var parsed = collector.Parse(output);

        Assert.Equal(["command -v ss >/dev/null 2>&1", "ss -tnH state established"], runner.Commands);
        Assert.All(runner.Calls, call => Assert.False(call.Options.UseSudo));
        Assert.True(parsed.IsSuccess, parsed.Error);
        Assert.Equal(2, parsed.Data!.Value.GetProperty("Connections").GetArrayLength());
    }

    private static string Fixture() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ss-established.txt"));

    private sealed class RecordingSshRunner : ISshRunner
    {
        public List<(string Command, SshRunOptions Options)> Calls { get; } = [];
        public IReadOnlyList<string> Commands => Calls.Select(call => call.Command).ToArray();

        public Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options,
            CancellationToken cancellationToken)
        {
            Calls.Add((command, options));
            var output = command == "ss -tnH state established" ? Fixture() : string.Empty;
            return Task.FromResult(new RemoteCommandResult(0, output, string.Empty, TimeSpan.FromMilliseconds(1)));
        }
    }
}
