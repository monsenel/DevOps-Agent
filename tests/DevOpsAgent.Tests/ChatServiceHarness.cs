using System.Runtime.CompilerServices;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Agent;
using DevOpsAgent.Application.Chat;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Domain.Llm;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevOpsAgent.Tests;

/// <summary>
/// A real <see cref="ChatService"/> over an in-memory database with a recording chat client: what the model would receive is
/// in <see cref="Requests"/>.
/// </summary>
internal sealed class ChatServiceHarness : IAsyncDisposable
{
    private readonly SqliteConnection _connection;

    private ChatServiceHarness(SqliteConnection connection, AppDbContext context, RecordingChatClient client, ChatService service, LlmProvider provider,
        ToolCallLimitService limits)
    {
        Limits = limits;
        _connection = connection;
        Context = context;
        Client = client;
        Service = service;
        Provider = provider;
    }

    public AppDbContext Context { get; }
    public RecordingChatClient Client { get; }
    public ChatService Service { get; }
    public LlmProvider Provider { get; }
    public ToolCallLimitService Limits { get; }
    public IReadOnlyList<IReadOnlyList<ChatMessage>> Requests => Client.Requests;
    public IReadOnlyList<ChatOptions?> Options => Client.OptionsSeen;

    /// <param name="baseUrl">A cloud URL by default; use a loopback one for a local provider.</param>
    public static async Task<ChatServiceHarness> CreateAsync(IKnownSecrets? secrets = null, string baseUrl = "https://openrouter.ai/api/v1",
        ProviderType type = ProviderType.OpenRouter, string model = "test/model", IEnumerable<Server>? servers = null, string reply = "done",
        IModelCapabilities? capabilities = null, Func<int, IReadOnlyList<ChatMessage>, ChatOptions?, ChatMessage>? script = null, bool native = false,
        bool streaming = false, bool redactSecrets = true)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await context.Database.EnsureCreatedAsync();
        var provider = new LlmProvider("test", type, baseUrl, model);
        provider.Update("test", type, baseUrl, model, 0, 0.2, 60, nativeToolCallingEnabled: native, streamingEnabled: streaming,
            redactSecretsBeforeSending: redactSecrets, null);
        provider.SetDefault(true);
        context.LlmProviders.Add(provider);
        if (servers is not null) context.Servers.AddRange(servers);
        await context.SaveChangesAsync();

        var registry = new ToolRegistry();
        var serverRepository = new ServerRepository(context);
        var snapshots = new SnapshotRepository(context);
        var graphs = new GraphRepository(context);
        var runner = new NoRunner();
        var actionRepository = new ActionRecordRepository(context);
        var executionModes = new ExecutionModeService(new AppSettingRepository(context), serverRepository, NullLogger<ExecutionModeService>.Instance);
        var commandBuilder = new ToolCommandBuilder(registry);
        var approvals = new ApprovalService(registry, executionModes, serverRepository, actionRepository, TestBanGuard.Create(), TimeProvider.System);
        var execution = new ActionExecutionService(actionRepository, serverRepository, registry, executionModes, commandBuilder,
            runner, TestBanGuard.Create(), TimeProvider.System);
        var discovery = new DiscoveryService(serverRepository, snapshots, graphs, new GraphBuilder(), runner, [], TimeProvider.System);
        var dispatcher = new AgentToolDispatcher(registry, approvals, execution, commandBuilder, serverRepository, graphs, snapshots, discovery, runner);
        var contextBuilder = new AgentContextBuilder(serverRepository, snapshots, graphs, new AlertRepository(context),
            new MetricRepository(context), new DriftService(snapshots, graphs));
        var client = new RecordingChatClient(reply, script);
        var limits = new ToolCallLimitService(new AppSettingRepository(context));
        var service = new ChatService(new ChatConversationRepository(context), new LlmProviderRepository(context), new Factory(client),
            registry, contextBuilder, dispatcher, TimeProvider.System, secrets, capabilities, limits, new AppSettingRepository(context));
        return new ChatServiceHarness(connection, context, client, service, provider, limits);
    }

    public async Task<IReadOnlyList<ChatStreamEvent>> SendAsync(Guid conversationId, string text, IReadOnlyList<ChatAttachmentInput>? attachments = null,
        bool allowUnredactedImages = false)
    {
        var events = new List<ChatStreamEvent>();
        await foreach (var item in Service.SendAsync(conversationId, text, attachments, allowUnredactedImages: allowUnredactedImages)) events.Add(item);
        return events;
    }

    public async Task<IReadOnlyList<ChatStreamEvent>> ContinueAsync(Guid conversationId)
    {
        var events = new List<ChatStreamEvent>();
        await foreach (var item in Service.ContinueAsync(conversationId)) events.Add(item);
        return events;
    }

    /// <summary>Everything the model received, as one string.</summary>
    public string SentText => string.Join("\n", Requests.SelectMany(request => request).Select(message => message.Text));

    public async ValueTask DisposeAsync()
    {
        await Context.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private sealed class Factory(IChatClient client) : IChatClientFactory
    {
        public IChatClient Create(LlmProvider provider) => client;
    }

    internal sealed class RecordingChatClient(string reply, Func<int, IReadOnlyList<ChatMessage>, ChatOptions?, ChatMessage>? script = null) : IChatClient
    {
        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];
        public List<ChatOptions?> OptionsSeen { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var sent = messages.ToArray();
            Requests.Add(sent);
            OptionsSeen.Add(options);
            // A script answers by the number of the call (0, 1, 2 ...): tool calls, then text.
            return Task.FromResult(new ChatResponse(script is null ? new ChatMessage(ChatRole.Assistant, reply) : script(Requests.Count - 1, sent, options)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates()) yield return update;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class NoRunner : ISshRunner
    {
        public Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options, CancellationToken cancellationToken) =>
            Task.FromResult(new RemoteCommandResult(0, string.Empty, string.Empty, TimeSpan.Zero));
    }
}

/// <summary>A fixed list of secrets for tests.</summary>
internal sealed class FixedSecrets(params string[] secrets) : IKnownSecrets
{
    public Task<IReadOnlyList<string>> GetAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>(secrets.OrderByDescending(secret => secret.Length).ToArray());

    public void Invalidate() { }
}

/// <summary>Fixed model capabilities for tests.</summary>
internal sealed class FixedCapabilities(bool reasoningEffort, bool images) : IModelCapabilities
{
    public Task<ModelCapabilities> GetAsync(LlmProvider provider, CancellationToken cancellationToken) =>
        Task.FromResult(new ModelCapabilities(reasoningEffort, images));
}
