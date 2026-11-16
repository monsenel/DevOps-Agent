using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Domain.Graph;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Infrastructure.Collectors;
using DevOpsAgent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DevOpsAgent.Tests;

public sealed class SshSessionTests
{
    private static readonly Func<string, bool> Nothing = _ => false;

    // ---- Policy -------------------------------------------------------------------------------------------------

    [Fact]
    public void Policy_NoSessionsIsLevelNone()
    {
        var status = SshSessionPolicy.Evaluate([], allowListConfigured: true, Nothing, Nothing);

        Assert.Equal(SshSessionLevel.None, status.Level);
        Assert.Empty(status.Sessions);
        Assert.Equal(string.Empty, status.Summary);
    }

    [Fact]
    public void Policy_OnlyAgentAndImplicitlyAllowedSessionsAreLevelNone()
    {
        SshSessionInfo[] sessions =
        [
            new("10.0.0.5", 22, 2, true),
            new("10.0.0.9", 22, 1, false)
        ];

        foreach (var configured in new[] { true, false })
        {
            var status = SshSessionPolicy.Evaluate(sessions, configured, Nothing, address => address == "10.0.0.9");

            Assert.Equal(SshSessionLevel.None, status.Level);
            Assert.Equal(string.Empty, status.Summary);
            Assert.Equal(SshSessionVerdict.Agent, status.Sessions.Single(item => item.Session.SourceAddress == "10.0.0.5").Verdict);
            Assert.Equal(SshSessionVerdict.AllowedImplicitly,
                status.Sessions.Single(item => item.Session.SourceAddress == "10.0.0.9").Verdict);
        }
    }

    [Fact]
    public void Policy_AgentFlagWinsOverTheAllowList()
    {
        var status = SshSessionPolicy.Evaluate([new SshSessionInfo("10.0.0.5", 22, 1, true)], true, Nothing, Nothing);

        Assert.Equal(SshSessionLevel.None, status.Level);
        Assert.Equal(SshSessionVerdict.Agent, Assert.Single(status.Sessions).Verdict);
    }

    [Fact]
    public void Policy_WithoutAListEveryOtherSourceIsUncheckedNotice()
    {
        SshSessionInfo[] sessions = [new("203.0.113.7", 22, 2, false), new("203.0.113.8", 22, 1, false)];

        var status = SshSessionPolicy.Evaluate(sessions, allowListConfigured: false, _ => true, Nothing);

        Assert.Equal(SshSessionLevel.Notice, status.Level);
        Assert.Equal("SSH from 2 sources", status.Summary);
        Assert.All(status.Sessions, item => Assert.Equal(SshSessionVerdict.Unchecked, item.Verdict));
    }

    [Fact]
    public void Policy_AllowedSourcesOnlyAreNotice()
    {
        var status = SshSessionPolicy.Evaluate([new SshSessionInfo("203.0.113.7", 22, 2, false)], true,
            address => address == "203.0.113.7", Nothing);

        Assert.Equal(SshSessionLevel.Notice, status.Level);
        Assert.Equal("SSH from 1 source", status.Summary);
        Assert.Equal(SshSessionVerdict.Allowed, Assert.Single(status.Sessions).Verdict);
    }

    [Fact]
    public void Policy_ANotAllowedSourceIsAWarningEvenNextToAllowedAndAgentSessions()
    {
        SshSessionInfo[] sessions =
        [
            new("10.0.0.5", 22, 1, true),
            new("203.0.113.7", 22, 1, false),
            new("198.51.100.4", 22, 3, false),
            new("192.0.2.1", 2222, 1, false)
        ];

        var status = SshSessionPolicy.Evaluate(sessions, true, address => address == "203.0.113.7", Nothing);

        Assert.Equal(SshSessionLevel.Warning, status.Level);
        Assert.Equal("SSH from 2 sources that are not allowed", status.Summary);
        // NotAllowed first (by address), then the rest by address.
        Assert.Equal(["192.0.2.1", "198.51.100.4", "10.0.0.5", "203.0.113.7"],
            status.Sessions.Select(item => item.Session.SourceAddress));
        Assert.Equal(
            [SshSessionVerdict.NotAllowed, SshSessionVerdict.NotAllowed, SshSessionVerdict.Agent, SshSessionVerdict.Allowed],
            status.Sessions.Select(item => item.Verdict));
    }

    [Fact]
    public void Policy_SingleNotAllowedSourceUsesTheSingularSummaryAndCountsSourcesNotPorts()
    {
        SshSessionInfo[] sessions = [new("192.0.2.1", 22, 1, false), new("192.0.2.1", 2222, 1, false)];

        var status = SshSessionPolicy.Evaluate(sessions, true, Nothing, Nothing);

        Assert.Equal(SshSessionLevel.Warning, status.Level);
        Assert.Equal("SSH from a source that is not allowed", status.Summary);
    }

    [Fact]
    public void Policy_NotAllowedIsOnlyPossibleWithAListAndImplicitAllowanceBeatsIt()
    {
        var withoutList = SshSessionPolicy.Evaluate([new SshSessionInfo("192.0.2.1", 22, 1, false)], false, Nothing, Nothing);
        var jumpHost = SshSessionPolicy.Evaluate([new SshSessionInfo("192.0.2.1", 22, 1, false)], true, Nothing, _ => true);

        Assert.Equal(SshSessionLevel.Notice, withoutList.Level);
        Assert.Equal(SshSessionLevel.None, jumpHost.Level);
    }

    // ---- GraphBuilder -------------------------------------------------------------------------------------------

    [Fact]
    public void GraphBuilder_PutsSshSessionsOnTheHostAttributesWithoutNodesOrEdges()
    {
        var server = new Server("web", "web.test", 22, "deploy", "key", "lab");
        var data = new ServerDiscoveryData(server, null, null, null, null, null,
            new ConnectionDiscoveryResult(
                [],
                [],
                DateTimeOffset.Parse("2026-11-16T08:00:00Z"),
                [new SshSessionInfo("203.0.113.7", 22, 2, false), new SshSessionInfo("10.0.0.5", 22, 1, true)]));

        var graph = new GraphBuilder().Build(Guid.NewGuid(), [data]);

        var host = Assert.Single(graph.Nodes);
        Assert.Equal(GraphNodeKind.Host, host.Kind);
        Assert.Empty(graph.Edges);
        using var attributes = JsonDocument.Parse(host.AttributesJson);
        var sessions = attributes.RootElement.GetProperty("SshSessions").EnumerateArray().ToArray();
        Assert.Equal(2, sessions.Length);
        Assert.Equal("203.0.113.7", sessions[0].GetProperty("Address").GetString());
        Assert.Equal(22, sessions[0].GetProperty("Port").GetInt32());
        Assert.Equal(2, sessions[0].GetProperty("Count").GetInt32());
        Assert.False(sessions[0].GetProperty("IsAgent").GetBoolean());
        Assert.True(sessions[1].GetProperty("IsAgent").GetBoolean());
        var evidence = Assert.Single(graph.Evidence, item => item.Collector == "connections");
        Assert.Equal(host.Id, evidence.NodeId);
        Assert.Equal("SSH sessions: 2 from 203.0.113.7 (port 22); 1 from 10.0.0.5 (DevOps Agent)", evidence.Description);
    }

    [Fact]
    public void GraphBuilder_SshSessionDoesNotCreateAnUnknownNodeOrEdgeButOtherConnectionsStillDo()
    {
        var server = new Server("web", "web.test", 22, "deploy", "key", "lab");
        var data = new ServerDiscoveryData(server, null, null, null, null, null,
            new ConnectionDiscoveryResult(
                [new EstablishedConnectionInfo("host", null, "tcp", "ESTAB", "10.0.0.1", 57006, "10.0.0.3", 9200)],
                [],
                null,
                [new SshSessionInfo("203.0.113.7", 22, 1, false)]));

        var graph = new GraphBuilder().Build(Guid.NewGuid(), [data]);

        var unknown = Assert.Single(graph.Nodes, node => node.Kind == GraphNodeKind.Unknown);
        Assert.Contains("10.0.0.3", unknown.AttributesJson, StringComparison.Ordinal);
        Assert.DoesNotContain(graph.Nodes, node => node.AttributesJson.Contains("203.0.113.7", StringComparison.Ordinal)
            && node.Kind != GraphNodeKind.Host);
        Assert.Equal(9200, Assert.Single(graph.Edges).Port);
    }

    [Fact]
    public void GraphBuilder_HostWithoutSessionsGetsAnEmptyArrayAndNoSshEvidence()
    {
        var server = new Server("web", "web.test", 22, "deploy", "key", "lab");
        var withConnections = new ServerDiscoveryData(server, null, null, null, null, null, new ConnectionDiscoveryResult([], []));
        var withoutConnections = new ServerDiscoveryData(new Server("db", "db.test", 22, "deploy", "key", "lab"),
            null, null, null, null);

        var graph = new GraphBuilder().Build(Guid.NewGuid(), [withConnections, withoutConnections]);

        Assert.Equal(2, graph.Nodes.Count);
        foreach (var node in graph.Nodes)
        {
            using var attributes = JsonDocument.Parse(node.AttributesJson);
            Assert.Equal(0, attributes.RootElement.GetProperty("SshSessions").GetArrayLength());
        }

        Assert.DoesNotContain(graph.Evidence, item => item.Description.StartsWith("SSH sessions", StringComparison.Ordinal));
    }

    // ---- Collector with a previous "listeners" result -----------------------------------------------------------

    [Fact]
    public async Task Collector_TreatsTheSshdListenerPortFromTheListenersResultAsSsh()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var dbContext = new AppDbContext(options);
        await dbContext.Database.EnsureCreatedAsync();
        var server = new Server("edge", "edge.example.internal", 22, "deploy", "id_ed25519", "lab");
        dbContext.Servers.Add(server);
        await dbContext.SaveChangesAsync();
        var service = new DiscoveryService(
            new ServerRepository(dbContext),
            new SnapshotRepository(dbContext),
            new GraphRepository(dbContext),
            new GraphBuilder(),
            new ConnectionRunner(),
            [new ListenersFake(), new ConnectionCollector()],
            TimeProvider.System);

        await service.RunAsync(progress: null, CancellationToken.None);
        var run = await dbContext.CollectorRuns.SingleAsync(item => item.CollectorName == "connections");
        var result = JsonSerializer.Deserialize<ConnectionDiscoveryResult>(run.ResultJson!)!;

        // 9000 is a listener of another process, not sshd, so it stays an ordinary connection.
        Assert.Equal([443, 9000], result.Connections.Select(item => item.LocalPort).Order());
        Assert.Equal(
            [new SshSessionInfo("203.0.113.9", 22, 1, false), new SshSessionInfo("203.0.113.9", 2222, 1, false)],
            result.SshSessions);
        await connection.DisposeAsync();
    }

    private sealed class ListenersFake : ICollector
    {
        public string Name => "listeners";

        public Task<bool> IsApplicableAsync(ServerContext context, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<CollectorOutput> CollectAsync(ServerContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new CollectorOutput("listeners", string.Empty, 0, TimeSpan.Zero));

        public CollectorResult Parse(CollectorOutput output) => new(JsonSerializer.SerializeToElement(
            new ListenerDiscoveryResult([
                new ListenerInfo("tcp", "LISTEN", "0.0.0.0", 443, "0.0.0.0", "nginx", 220),
                new ListenerInfo("tcp", "LISTEN", "0.0.0.0", 2222, "0.0.0.0", "sshd", 1000),
                new ListenerInfo("tcp", "LISTEN", "0.0.0.0", 9000, "0.0.0.0", "other", 1001)
            ])));
    }

    private sealed class ConnectionRunner : ISshRunner
    {
        public Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options,
            CancellationToken cancellationToken)
        {
            var output = command == "ss -tnH state established"
                ? string.Join('\n',
                    "ESTAB 0 0 10.0.0.5:22 203.0.113.9:50000",
                    "ESTAB 0 0 10.0.0.5:2222 203.0.113.9:50001",
                    "ESTAB 0 0 10.0.0.5:443 203.0.113.9:50002",
                    "ESTAB 0 0 10.0.0.5:9000 203.0.113.9:50003") + "\n"
                : string.Empty;
            return Task.FromResult(new RemoteCommandResult(0, output, string.Empty, TimeSpan.FromMilliseconds(1)));
        }
    }
}
