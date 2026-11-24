using System.Runtime.CompilerServices;
using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Agent;
using DevOpsAgent.Application.Chat;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Application.Llm;
using DevOpsAgent.Domain.Discovery;
using DevOpsAgent.Domain.Graph;
using DevOpsAgent.Domain.Llm;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Infrastructure.Llm;
using DevOpsAgent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevOpsAgent.Tests;

/// <summary>v2 LLM Task Routing, Provider Fallback, and read-only LlmEdgeSuggestionService tests.</summary>
public sealed class LlmRoutingAndEdgeSuggestionTests
{
    [Fact]
    public async Task LlmRoutingService_PersistsTaskRoutesAndOrderedFallback_AndHandlesMissingOrCorruptState()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await context.Database.EnsureCreatedAsync();

        var local = new LlmProvider("Ollama Local", ProviderType.Ollama, "http://127.0.0.1:11434/v1", "llama3.1:8b");
        local.SetDefault(true);
        var fast = new LlmProvider("Fast Summary", ProviderType.OpenRouter, "https://openrouter.ai/api/v1", "openai/gpt-4o-mini");
        var security = new LlmProvider("Security Audit", ProviderType.OpenRouter, "https://openrouter.ai/api/v1", "anthropic/claude-sonnet");
        context.LlmProviders.AddRange(local, fast, security);
        await context.SaveChangesAsync();

        var settingsRepo = new AppSettingRepository(context);
        var providerRepo = new LlmProviderRepository(context);
        var routing = new LlmRoutingService(settingsRepo, providerRepo, NullLogger<LlmRoutingService>.Instance);

        // Default before saving: everything resolves to default provider only (fallback off).
        Assert.Equal([local.Id], (await routing.ResolveCandidatesAsync(LlmTaskCategory.Chat, CancellationToken.None)).Select(p => p.Id));

        // Reject unknown provider IDs.
        await Assert.ThrowsAsync<ArgumentException>(() => routing.SaveAsync(
            new LlmRoutingSettings(ChatProviderId: Guid.NewGuid()), CancellationToken.None));

        // Persist explicit routes + ordered fallback.
        await routing.SaveAsync(new LlmRoutingSettings(
            ChatProviderId: local.Id,
            QuickSummaryProviderId: fast.Id,
            SecurityAuditProviderId: security.Id,
            FallbackEnabled: true,
            FallbackProviderId: fast.Id), CancellationToken.None);

        var loaded = await routing.GetAsync(CancellationToken.None);
        Assert.Equal(local.Id, loaded.ChatProviderId);
        Assert.Equal(fast.Id, loaded.QuickSummaryProviderId);
        Assert.Equal(security.Id, loaded.SecurityAuditProviderId);
        Assert.True(loaded.FallbackEnabled);
        Assert.Equal(fast.Id, loaded.FallbackProviderId);

        // Chat: local -> fast (no duplicates).
        Assert.Equal([local.Id, fast.Id],
            (await routing.ResolveCandidatesAsync(LlmTaskCategory.Chat, CancellationToken.None)).Select(p => p.Id));
        // SecurityAudit: security -> fast (explicit fallback) -> local (default).
        Assert.Equal([security.Id, fast.Id, local.Id],
            (await routing.ResolveCandidatesAsync(LlmTaskCategory.SecurityAudit, CancellationToken.None)).Select(p => p.Id));
        // QuickSummary: fast -> local (default).
        Assert.Equal([fast.Id, local.Id],
            (await routing.ResolveCandidatesAsync(LlmTaskCategory.QuickSummary, CancellationToken.None)).Select(p => p.Id));

        // Corrupt JSON fails safely to defaults without throwing.
        await settingsRepo.SetAsync(LlmRoutingService.SettingKey, "{corrupt json", CancellationToken.None);
        Assert.Equal(LlmRoutingSettings.Default, await routing.GetAsync(CancellationToken.None));

        Assert.Equal(LlmTaskCategory.SecurityAudit, LlmRoutingService.InferCategory("Run a security audit on srv-edge"));
        Assert.Equal(LlmTaskCategory.Chat, LlmRoutingService.InferCategory("Why is nginx returning 502?"));
    }

    [Fact]
    public async Task FallbackChatClient_FailsOverForBufferedAndStreamingCalls_WithoutSwallowingCallerCancellation()
    {
        var primary = new LlmProvider("Primary", ProviderType.Ollama, "http://127.0.0.1:11434/v1", "primary-model");
        var backup = new LlmProvider("Backup", ProviderType.OpenRouter, "https://openrouter.ai/api/v1", "backup-model");
        var factory = new PerProviderClientFactory(new Dictionary<Guid, IChatClient>
        {
            [primary.Id] = new FailingChatClient(new HttpRequestException("connection refused")),
            [backup.Id] = new FixedReplyChatClient("answered by backup")
        });

        string? fallbackNotice = null;
        using var fallbackClient = new FallbackChatClient([primary, backup], factory,
            (from, to, _) => fallbackNotice = $"{from.Name}->{to.Name}");

        var response = await fallbackClient.GetResponseAsync([new ChatMessage(ChatRole.User, "ping")]);
        Assert.Equal("answered by backup", response.Text);
        Assert.Equal(backup.Id, fallbackClient.ActiveProvider?.Id);
        Assert.Equal("Primary->Backup", fallbackNotice);

        var streamed = new List<string>();
        await foreach (var update in fallbackClient.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "ping")]))
            if (!string.IsNullOrEmpty(update.Text)) streamed.Add(update.Text);
        Assert.Equal("answered by backup", string.Concat(streamed));

        // Caller cancellation must propagate immediately without trying fallback.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelFactory = new PerProviderClientFactory(new Dictionary<Guid, IChatClient>
        {
            [primary.Id] = new FailingChatClient(new OperationCanceledException(cts.Token)),
            [backup.Id] = new FixedReplyChatClient("should not run")
        });
        using var cancelling = new FallbackChatClient([primary, backup], cancelFactory);
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            cancelling.GetResponseAsync([new ChatMessage(ChatRole.User, "ping")], cancellationToken: cts.Token));
        Assert.Equal(primary.Id, cancelling.ActiveProvider?.Id);
    }

    [Fact]
    public async Task ChatService_RoutesByTaskCategory_AndFailsOverWithReAppliedSecretRedaction()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await context.Database.EnsureCreatedAsync();

        var localPrimary = new LlmProvider("Local Ollama", ProviderType.Ollama, "http://127.0.0.1:11434/v1", "llama3.1:8b");
        localPrimary.Update("Local Ollama", ProviderType.Ollama, "http://127.0.0.1:11434/v1", "llama3.1:8b",
            0, 0.2, 60, nativeToolCallingEnabled: false, streamingEnabled: false, redactSecretsBeforeSending: true, null);
        localPrimary.SetDefault(true);

        var cloudFallback = new LlmProvider("Cloud Fallback", ProviderType.OpenRouter, "https://openrouter.ai/api/v1", "openai/gpt-4o");
        cloudFallback.Update("Cloud Fallback", ProviderType.OpenRouter, "https://openrouter.ai/api/v1", "openai/gpt-4o",
            0, 0.2, 60, nativeToolCallingEnabled: false, streamingEnabled: false, redactSecretsBeforeSending: true, null);

        var securityProvider = new LlmProvider("Security Specialist", ProviderType.OpenRouter, "https://openrouter.ai/api/v1", "anthropic/claude-sonnet");
        securityProvider.Update("Security Specialist", ProviderType.OpenRouter, "https://openrouter.ai/api/v1", "anthropic/claude-sonnet",
            0, 0.1, 60, nativeToolCallingEnabled: false, streamingEnabled: false, redactSecretsBeforeSending: true, null);

        context.LlmProviders.AddRange(localPrimary, cloudFallback, securityProvider);
        await context.SaveChangesAsync();

        var settingsRepo = new AppSettingRepository(context);
        var providerRepo = new LlmProviderRepository(context);
        var routing = new LlmRoutingService(settingsRepo, providerRepo, NullLogger<LlmRoutingService>.Instance);
        await routing.SaveAsync(new LlmRoutingSettings(
            ChatProviderId: localPrimary.Id,
            SecurityAuditProviderId: securityProvider.Id,
            FallbackEnabled: true,
            FallbackProviderId: cloudFallback.Id), CancellationToken.None);

        var fallbackRecorder = new RecordingReplyClient("handled by cloud fallback");
        var securityRecorder = new RecordingReplyClient("security audit complete");
        var factory = new PerProviderClientFactory(new Dictionary<Guid, IChatClient>
        {
            [localPrimary.Id] = new FailingChatClient(new HttpRequestException("ollama unreachable")),
            [cloudFallback.Id] = fallbackRecorder,
            [securityProvider.Id] = securityRecorder
        });

        var registry = new ToolRegistry();
        var serverRepo = new ServerRepository(context);
        var snapshots = new SnapshotRepository(context);
        var graphs = new GraphRepository(context);
        var runner = new NoopSshRunner();
        var actionRepo = new ActionRecordRepository(context);
        var modes = new ExecutionModeService(settingsRepo, serverRepo, NullLogger<ExecutionModeService>.Instance);
        var commandBuilder = new ToolCommandBuilder(registry);
        var approvals = new ApprovalService(registry, modes, serverRepo, actionRepo, TestBanGuard.Create(), TimeProvider.System);
        var execution = new ActionExecutionService(actionRepo, serverRepo, registry, modes, commandBuilder, runner, TestBanGuard.Create(), TimeProvider.System);
        var discovery = new DiscoveryService(serverRepo, snapshots, graphs, new GraphBuilder(), runner, [], TimeProvider.System);
        var dispatcher = new AgentToolDispatcher(registry, approvals, execution, commandBuilder, serverRepo, graphs, snapshots, discovery, runner);
        var contextBuilder = new AgentContextBuilder(serverRepo, snapshots, graphs, new AlertRepository(context), new MetricRepository(context), new DriftService(snapshots, graphs));

        var chat = new ChatService(
            new ChatConversationRepository(context),
            providerRepo,
            factory,
            registry,
            contextBuilder,
            dispatcher,
            TimeProvider.System,
            settings: settingsRepo,
            routing: routing);

        // 1. Regular chat tries localPrimary -> fails -> falls back to cloudFallback AND redacts secret before sending to cloud!
        var conv1 = await chat.CreateAsync(CancellationToken.None);
        var events1 = new List<ChatStreamEvent>();
        await foreach (var ev in chat.SendAsync(conv1, "check DB_PASSWORD=super-secret-value on app"))
            events1.Add(ev);

        Assert.Contains(events1, ev => ev.Kind == "provider_fallback" && ev.Content.Contains("Cloud Fallback", StringComparison.Ordinal));
        Assert.Contains(events1, ev => ev.Kind == "text" && ev.Content == "handled by cloud fallback");
        var sentToCloud = string.Join("\n", fallbackRecorder.ReceivedMessages.SelectMany(m => m).Select(m => m.Text));
        Assert.DoesNotContain("super-secret-value", sentToCloud, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", sentToCloud, StringComparison.Ordinal);

        // 2. Security audit prompt routes directly to Security Specialist provider.
        var conv2 = await chat.CreateAsync(CancellationToken.None);
        var events2 = new List<ChatStreamEvent>();
        await foreach (var ev in chat.SendAsync(conv2, "please run a security audit on our servers"))
            events2.Add(ev);

        Assert.Single(securityRecorder.ReceivedMessages);
        Assert.Contains(events2, ev => ev.Kind == "text" && ev.Content == "security audit complete");
    }

    [Fact]
    public async Task LlmEdgeSuggestionService_SuggestsInferredEdgesReadOnly_FiltersInvalidAndExisting_AndRedactsSecrets()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await context.Database.EnsureCreatedAsync();

        var srv = new Server("srv-app", "10.0.0.20", 22, "dva", "key", "staging");
        context.Servers.Add(srv);
        var snapshot = new Snapshot([srv.Id], DateTimeOffset.UtcNow.AddMinutes(-2));
        snapshot.Complete(DateTimeOffset.UtcNow, 1, 0);
        context.Snapshots.Add(snapshot);

        var provider = new LlmProvider("Cloud", ProviderType.OpenRouter, "https://openrouter.ai/api/v1", "openai/gpt-4o-mini");
        provider.Update("Cloud", ProviderType.OpenRouter, "https://openrouter.ai/api/v1", "openai/gpt-4o-mini",
            0, 0.2, 60, false, false, true, null);
        provider.SetDefault(true);
        context.LlmProviders.Add(provider);
        await context.SaveChangesAsync();

        var host = new GraphNode(snapshot.Id, $"host:{srv.Id:N}", GraphNodeKind.Host, "srv-app", null, srv.Id, "{}");
        var api = new GraphNode(snapshot.Id, $"container:{srv.Id:N}:api", GraphNodeKind.Container, "api", host.Id, srv.Id, "{}");
        var redis = new GraphNode(snapshot.Id, $"container:{srv.Id:N}:redis", GraphNodeKind.Container, "redis", host.Id, srv.Id, "{}");
        var db = new GraphNode(snapshot.Id, $"container:{srv.Id:N}:postgres", GraphNodeKind.Container, "postgres", host.Id, srv.Id, "{}");
        var existingEdge = new GraphEdge(snapshot.Id, api.Id, db.Id, GraphEdgeKind.Declared, 5432, "tcp");

        var realGraphRepo = new GraphRepository(context);
        await realGraphRepo.SaveAsync(snapshot.Id, new GraphBuildResult(
            [host, api, redis, db],
            [existingEdge],
            [new GraphListener(snapshot.Id, redis.Id, "0.0.0.0", 6379, "tcp", "redis-server", 10)],
            []), CancellationToken.None);

        var spyGraph = new ReadOnlyAssertingGraphRepository(realGraphRepo);
        var llmResponse = JsonSerializer.Serialize(new
        {
            edges = new object[]
            {
                // Valid missing edge
                new { sourceId = api.Id, targetId = redis.Id, port = 6379, protocol = "tcp", reason = "API uses Redis cache with dbpass=secret-xyz", confidence = 1.5 },
                // Duplicate of existing edge (must be filtered out)
                new { sourceId = api.Id, targetId = db.Id, port = 5432, protocol = "tcp", reason = "Already exists", confidence = 0.9 },
                // Self-loop (must be filtered out)
                new { sourceId = api.Id, targetId = api.Id, port = 8080, protocol = "http", reason = "Self", confidence = 0.9 },
                // Unknown node ID (must be filtered out)
                new { sourceId = api.Id, targetId = Guid.NewGuid(), port = 9000, protocol = "tcp", reason = "Hallucinated", confidence = 0.8 }
            }
        });

        var client = new RecordingReplyClient($"```json\n{llmResponse}\n```");
        var service = new LlmEdgeSuggestionService(
            new SnapshotRepository(context),
            spyGraph,
            new LlmProviderRepository(context),
            new PerProviderClientFactory(new Dictionary<Guid, IChatClient> { [provider.Id] = client }),
            knownSecrets: new FixedSecrets("Sudo-Secret-99"));

        var result = await service.SuggestAsync(CancellationToken.None);

        // Strictly read-only: SaveAsync and MergeAsync were never called!
        Assert.Equal(0, spyGraph.WriteCalls);
        Assert.Equal(snapshot.Id, result.SnapshotId);
        Assert.Equal("Cloud", result.ProviderName);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(GraphEdgeKind.Inferred, candidate.Kind);
        Assert.Equal(api.Id, candidate.SourceNodeId);
        Assert.Equal(redis.Id, candidate.TargetNodeId);
        Assert.Equal(6379, candidate.Port);
        Assert.Equal("tcp", candidate.Protocol);
        Assert.Equal(1.0, candidate.Confidence); // clamped from 1.5
        Assert.DoesNotContain("secret-xyz", candidate.Reason, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", candidate.Reason, StringComparison.Ordinal);

        // Helper builds an Inferred GraphEdge only when explicitly accepted by the user.
        var accepted = LlmEdgeSuggestionService.ToGraphEdge(snapshot.Id, candidate);
        Assert.Equal(GraphEdgeKind.Inferred, accepted.Kind);
        Assert.Equal(api.Id, accepted.SourceNodeId);
        Assert.Equal(redis.Id, accepted.TargetNodeId);
    }

    private sealed class PerProviderClientFactory(Dictionary<Guid, IChatClient> clients) : IChatClientFactory
    {
        public IChatClient Create(LlmProvider provider) => clients[provider.Id];
    }

    private sealed class FailingChatClient(Exception failure) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromException<ChatResponse>(failure);

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw failure;
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class FixedReplyChatClient(string reply) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, reply);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class RecordingReplyClient(string reply) : IChatClient
    {
        public List<IReadOnlyList<ChatMessage>> ReceivedMessages { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            ReceivedMessages.Add(messages.ToArray());
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ReceivedMessages.Add(messages.ToArray());
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, reply);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class NoopSshRunner : ISshRunner
    {
        public Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options, CancellationToken cancellationToken) =>
            Task.FromResult(new RemoteCommandResult(0, string.Empty, string.Empty, TimeSpan.Zero));
    }

    private sealed class ReadOnlyAssertingGraphRepository(GraphRepository inner) : IGraphRepository
    {
        public int WriteCalls { get; private set; }

        public Task SaveAsync(Guid snapshotId, GraphBuildResult graph, CancellationToken cancellationToken)
        {
            WriteCalls++;
            return inner.SaveAsync(snapshotId, graph, cancellationToken);
        }

        public Task MergeAsync(Guid snapshotId, Guid previousSnapshotId, GraphBuildResult graph, CancellationToken cancellationToken)
        {
            WriteCalls++;
            return inner.MergeAsync(snapshotId, previousSnapshotId, graph, cancellationToken);
        }

        public Task<GraphSnapshotDto?> GetAsync(Guid snapshotId, GraphView view, CancellationToken cancellationToken) =>
            inner.GetAsync(snapshotId, view, cancellationToken);

        public Task<GraphBuildResult?> GetEntitiesAsync(Guid snapshotId, CancellationToken cancellationToken) =>
            inner.GetEntitiesAsync(snapshotId, cancellationToken);
    }
}
