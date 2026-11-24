using System.Runtime.CompilerServices;
using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Agent;
using DevOpsAgent.Application.Chat;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Application.Servers;
using DevOpsAgent.Application.Updates;
using DevOpsAgent.Domain.Discovery;
using DevOpsAgent.Domain.Graph;
using DevOpsAgent.Domain.Llm;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Infrastructure.Parsers;
using DevOpsAgent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevOpsAgent.Tests;

/// <summary>
/// T7.R12 test gaps: ChatService.SendAsync streaming + tool loop, discovery max degree 8,
/// unix-socket edge resolution, dnf update parsing &amp; reboot report, and no-sudo capability path.
/// </summary>
public sealed class T7R12TestGapsTests
{
    [Fact]
    public async Task ChatService_SendAsync_StreamsChunksAndExecutesNativeToolLoop()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await context.Database.EnsureCreatedAsync();

        var provider = new LlmProvider("stream-llm", ProviderType.Ollama, "http://127.0.0.1:11434/v1", "qwen3:8b");
        provider.Update("stream-llm", ProviderType.Ollama, "http://127.0.0.1:11434/v1", "qwen3:8b", 0, 0.2, 60,
            nativeToolCallingEnabled: true, streamingEnabled: true, redactSecretsBeforeSending: false, null);
        provider.SetDefault(true);
        context.LlmProviders.Add(provider);
        context.Servers.Add(new Server("srv-web", "10.0.0.10", 22, "ops", "key", "staging"));
        await context.SaveChangesAsync();

        var client = new StreamingToolLoopClient();
        var registry = new ToolRegistry();
        var serverRepo = new ServerRepository(context);
        var snapshots = new SnapshotRepository(context);
        var graphs = new GraphRepository(context);
        var runner = new FixedSshRunner((_, _) => new RemoteCommandResult(0, string.Empty, string.Empty, TimeSpan.Zero));
        var actionRepo = new ActionRecordRepository(context);
        var modes = new ExecutionModeService(new AppSettingRepository(context), serverRepo, NullLogger<ExecutionModeService>.Instance);
        var commandBuilder = new ToolCommandBuilder(registry);
        var approvals = new ApprovalService(registry, modes, serverRepo, actionRepo, TestBanGuard.Create(), TimeProvider.System);
        var execution = new ActionExecutionService(actionRepo, serverRepo, registry, modes, commandBuilder, runner, TestBanGuard.Create(), TimeProvider.System);
        var discovery = new DiscoveryService(serverRepo, snapshots, graphs, new GraphBuilder(), runner, [], TimeProvider.System);
        var dispatcher = new AgentToolDispatcher(registry, approvals, execution, commandBuilder, serverRepo, graphs, snapshots, discovery, runner);
        var contextBuilder = new AgentContextBuilder(serverRepo, snapshots, graphs, new AlertRepository(context), new MetricRepository(context), new DriftService(snapshots, graphs));

        var chat = new ChatService(
            new ChatConversationRepository(context),
            new LlmProviderRepository(context),
            new SingleClientFactory(client),
            registry,
            contextBuilder,
            dispatcher,
            TimeProvider.System,
            settings: new AppSettingRepository(context));

        var conversationId = await chat.CreateAsync(CancellationToken.None);
        var events = new List<ChatStreamEvent>();
        await foreach (var ev in chat.SendAsync(conversationId, "list my servers"))
            events.Add(ev);

        // Round 0 invoked list_servers via streaming FunctionCallContent; round 1 streamed 3 text chunks.
        Assert.Equal(2, client.CallCount);
        Assert.Contains(events, ev => ev.Kind == "tool_run" && ev.Content.Contains("list_servers", StringComparison.Ordinal));
        Assert.Contains(events, ev => ev.Kind == "tool_turn" && ev.Content.Contains("Ran 1 tool", StringComparison.Ordinal));
        var textChunks = events.Where(ev => ev.Kind == "text").Select(ev => ev.Content).ToArray();
        Assert.Equal(["Found ", "one server: ", "srv-web."], textChunks);

        var messages = await chat.GetMessagesAsync(conversationId, CancellationToken.None);
        var assistant = Assert.Single(messages, m => m.Role == "assistant");
        Assert.Equal("Found one server: srv-web.", assistant.Content);
    }

    [Fact]
    public async Task DiscoveryService_LimitsParallelismToMaxDegreeEight()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await context.Database.EnsureCreatedAsync();

        for (var i = 0; i < 16; i++)
            context.Servers.Add(new Server($"srv-{i:00}", $"10.99.0.{i + 1}", 22, "dva", "key", "lab"));
        await context.SaveChangesAsync();

        var collector = new ConcurrencyTrackingCollector();
        var discovery = new DiscoveryService(
            new ServerRepository(context),
            new SnapshotRepository(context),
            new GraphRepository(context),
            new GraphBuilder(),
            new FixedSshRunner((_, _) => new RemoteCommandResult(0, string.Empty, string.Empty, TimeSpan.Zero)),
            [collector],
            TimeProvider.System);

        var snapshot = await discovery.RunAsync(null, CancellationToken.None);

        Assert.Equal(SnapshotStatus.Completed, snapshot.Status);
        Assert.Equal(16, collector.TotalRuns);
        Assert.InRange(collector.MaxObservedConcurrency, 2, 8);
    }

    [Fact]
    public void EdgeResolver_AndGraphBuilder_ResolveUnixSocketTargetsAsUnknownUnixEndpoints()
    {
        var directUnix = NginxEndpointParser.Parse("unix:/run/php/php8.2-fpm.sock");
        Assert.Equal("unix", directUnix.Protocol);
        Assert.Null(directUnix.Port);
        Assert.False(directUnix.IsUnresolved);

        var httpUnix = NginxEndpointParser.Parse("http://unix:/var/run/gunicorn.sock:/api");
        Assert.Equal("unix", httpUnix.Protocol);
        Assert.Null(httpUnix.Port);
        Assert.False(httpUnix.IsUnresolved);

        var edgeServer = new Server("srv-edge", "10.0.1.10", 22, "dva", "key", "prod");
        var sourceData = new ServerDiscoveryData(
            edgeServer,
            new HostDiscoveryResult("srv-edge", [], [], []),
            null,
            null,
            null,
            new NginxDiscoveryResult(
                [new NginxServerInfo("http",
                    [new NginxSourceValue("80", "/etc/nginx/sites-enabled/app.conf", 3)],
                    [new NginxSourceValue("app.example.test", "/etc/nginx/sites-enabled/app.conf", 2)],
                    [],
                    [new NginxProxyInfo("proxy_pass", "http://unix:/var/run/gunicorn.sock:/", false, "/etc/nginx/sites-enabled/app.conf", 5)],
                    "/etc/nginx/sites-enabled/app.conf", 1)],
                []),
            null);

        var resolver = new EdgeResolver();
        var resolution = resolver.Resolve(httpUnix, sourceData, [sourceData], new IpIndex());
        Assert.True(resolution.IsUnknown);
        Assert.Contains("Unix socket", resolution.Reason, StringComparison.OrdinalIgnoreCase);

        var graph = new GraphBuilder().Build(Guid.NewGuid(), [sourceData]);
        var unknown = Assert.Single(graph.Nodes, node => node.Kind == GraphNodeKind.Unknown);
        using var attrs = JsonDocument.Parse(unknown.AttributesJson);
        Assert.Equal("unix", attrs.RootElement.GetProperty("Protocol").GetString());
        Assert.Contains("Unix socket", attrs.RootElement.GetProperty("Reason").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UpdatesParser_ParsesDnfOutputAndSecurityAdvisories_AndUpdateApplyReportHandlesRebootStates()
    {
        const string dnfCheckOutput = """
            Last metadata expiration check: 0:14:22 ago on Mon 23 Nov 2026 09:00:00 AM UTC.

            openssl.x86_64                  1:3.0.7-28.el9_4          baseos
            curl.x86_64                     7.76.1-29.el9_4.1         baseos
            tzdata.noarch                   2024a-2.el9               appstream
            Obsoleting Packages
            python3-new.x86_64              3.11.9-1.el9              appstream
            """;

        const string dnfSecurityOutput = """
            RHSA-2026:1234 Important/Sec. openssl-1:3.0.7-28.el9_4.x86_64
            RHSA-2026:5678 Moderate/Sec.  curl-7.76.1-29.el9_4.1.x86_64
            """;

        var packages = UpdatesParser.ParseDnf(dnfCheckOutput, dnfSecurityOutput);

        Assert.Equal(4, packages.Count);
        var openssl = Assert.Single(packages, p => p.Name == "openssl.x86_64");
        Assert.Equal("1:3.0.7-28.el9_4", openssl.CandidateVersion);
        Assert.Equal("baseos", openssl.Repository);
        Assert.True(openssl.IsSecurity);

        var curl = Assert.Single(packages, p => p.Name == "curl.x86_64");
        Assert.True(curl.IsSecurity);

        var tzdata = Assert.Single(packages, p => p.Name == "tzdata.noarch");
        Assert.False(tzdata.IsSecurity);

        // Reboot required = true with restarted services
        var rebootYes = UpdateApplyReport.Parse("srv-rhel", 0,
            "Upgraded: openssl\nRESTARTED_SERVICES_BEGIN\nnginx.service\nsshd.service\nRESTARTED_SERVICES_END\nREBOOT_REQUIRED=true\n",
            null);
        Assert.True(rebootYes.RebootRequired);
        Assert.Equal(["nginx.service", "sshd.service"], rebootYes.RestartedServices);
        Assert.Contains("Restarted services: nginx.service, sshd.service. Reboot required: yes.", rebootYes.Describe(), StringComparison.Ordinal);

        // Reboot required = false, no restarted services
        var rebootNo = UpdateApplyReport.Parse("srv-rhel", 0,
            "RESTARTED_SERVICES_BEGIN\nRESTARTED_SERVICES_END\nREBOOT_REQUIRED=false\n",
            null);
        Assert.False(rebootNo.RebootRequired);
        Assert.Empty(rebootNo.RestartedServices);
        Assert.Contains("Restarted services: none. Reboot required: no.", rebootNo.Describe(), StringComparison.Ordinal);

        // Missing marker -> unknown
        var rebootUnknown = UpdateApplyReport.Parse("srv-rhel", 0, "Complete!\n", null);
        Assert.Null(rebootUnknown.RebootRequired);
        Assert.Contains("Reboot required: unknown.", rebootUnknown.Describe(), StringComparison.Ordinal);

        // Non-zero exit -> failed description
        var failed = UpdateApplyReport.Parse("srv-rhel", 1, string.Empty, "dnf lock held");
        Assert.Contains("update failed (dnf lock held)", failed.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServerCapabilityService_RecordsNoSudoCapable_WhenSudoProbeReturnsNonZero()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await context.Database.EnsureCreatedAsync();

        var server = new Server("srv-nosudo", "10.0.5.5", 22, "readonly", "key", "prod");
        context.Servers.Add(server);
        await context.SaveChangesAsync();

        var runner = new FixedSshRunner((command, _) => command switch
        {
            "cat /etc/os-release" => new RemoteCommandResult(0, "NAME=\"Rocky Linux\"\nVERSION_ID=\"9.4\"\n", string.Empty, TimeSpan.FromMilliseconds(5)),
            "uptime -p" => new RemoteCommandResult(0, "up 4 days, 2 hours\n", string.Empty, TimeSpan.FromMilliseconds(5)),
            "sudo -n true" => new RemoteCommandResult(1, string.Empty, "sudo: a password is required\n", TimeSpan.FromMilliseconds(5)),
            _ when command.Contains("command -v", StringComparison.Ordinal) =>
                new RemoteCommandResult(0, "ip\nss\nsystemctl\n", string.Empty, TimeSpan.FromMilliseconds(5)),
            _ => new RemoteCommandResult(1, string.Empty, "unexpected", TimeSpan.Zero)
        });

        var service = new ServerCapabilityService(runner, new ServerRepository(context), TimeProvider.System);
        var result = await service.TestAsync(server.Id, CancellationToken.None);

        Assert.True(result.Reachable);
        Assert.False(result.SudoCapable);
        Assert.Equal("Rocky Linux 9.4", result.OsInfo);
        Assert.Equal("up 4 days, 2 hours", result.Uptime);
        Assert.Equal(["ip", "ss", "systemctl"], result.AvailableCommands);

        var reloaded = await new ServerRepository(context).GetAsync(server.Id, CancellationToken.None);
        Assert.NotNull(reloaded);
        Assert.False(reloaded!.SudoCapable);
        Assert.False(reloaded.CanUseSudo);
    }

    private sealed class SingleClientFactory(IChatClient client) : IChatClientFactory
    {
        public IChatClient Create(LlmProvider provider) => client;
    }

    private sealed class StreamingToolLoopClient : IChatClient
    {
        public int CallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Expected streaming path.");

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CallCount++;
            await Task.Yield();
            if (CallCount == 1)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant,
                    [new FunctionCallContent("call-1", "list_servers", new Dictionary<string, object?>())]);
                yield break;
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, "Found ");
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "one server: ");
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "srv-web.");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class ConcurrencyTrackingCollector : ICollector
    {
        private int _active;
        private int _maxObserved;
        private int _totalRuns;

        public string Name => "tracking";
        public int MaxObservedConcurrency => Volatile.Read(ref _maxObserved);
        public int TotalRuns => Volatile.Read(ref _totalRuns);

        public Task<bool> IsApplicableAsync(ServerContext context, CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public async Task<CollectorOutput> CollectAsync(ServerContext context, CancellationToken cancellationToken)
        {
            var current = Interlocked.Increment(ref _active);
            Interlocked.Increment(ref _totalRuns);
            int observed;
            do
            {
                observed = Volatile.Read(ref _maxObserved);
                if (current <= observed) break;
            }
            while (Interlocked.CompareExchange(ref _maxObserved, current, observed) != observed);

            try
            {
                await Task.Delay(35, cancellationToken);
                return new CollectorOutput("{}", string.Empty, 0, TimeSpan.FromMilliseconds(35));
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        public CollectorResult Parse(CollectorOutput output)
        {
            using var doc = JsonDocument.Parse("{}");
            return new CollectorResult(doc.RootElement.Clone());
        }
    }

    private sealed class FixedSshRunner(Func<string, SshRunOptions, RemoteCommandResult> handler) : ISshRunner
    {
        public Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options, CancellationToken cancellationToken) =>
            Task.FromResult(handler(command, options));
    }
}
