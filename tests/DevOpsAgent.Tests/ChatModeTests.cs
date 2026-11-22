using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Agent;
using DevOpsAgent.Application.Chat;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Domain.Agent;
using DevOpsAgent.Domain.Chat;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevOpsAgent.Tests;

/// <summary>T9.4 / D-066: per-conversation chat execution mode dropdown (ReadOnly / Confirm / Auto) and ask-before-acting guardrails.</summary>
public sealed class ChatModeTests
{
    [Fact]
    public async Task ChatModePersistsPerConversationAndDefaultsToConfirm()
    {
        await using var chat = await ChatServiceHarness.CreateAsync();
        var convA = await chat.Service.CreateAsync(CancellationToken.None);
        var convB = await chat.Service.CreateAsync(CancellationToken.None);

        Assert.Equal(ChatMode.Confirm, await chat.Service.GetModeAsync(convA, CancellationToken.None));
        Assert.Equal(ChatMode.Confirm, await chat.Service.GetModeAsync(convB, CancellationToken.None));

        await chat.Service.SetModeAsync(convA, ChatMode.Auto, CancellationToken.None);
        await chat.Service.SetModeAsync(convB, ChatMode.ReadOnly, CancellationToken.None);

        Assert.Equal(ChatMode.Auto, await chat.Service.GetModeAsync(convA, CancellationToken.None));
        Assert.Equal(ChatMode.ReadOnly, await chat.Service.GetModeAsync(convB, CancellationToken.None));
    }

    [Fact]
    public void SelectToolsForMode_ExposesAllToolsInConfirmAndAutoIncludingPersianPromptsAndRestrictsReadOnly()
    {
        var registry = new ToolRegistry();
        const string persianNginxPrompt = "دامنه app.example.com رو به nginx اضافه کن که بزنه به 10.88.20.10:3000";

        var readOnlyTools = ChatService.SelectToolsForMode(persianNginxPrompt, registry, ChatMode.ReadOnly);
        Assert.DoesNotContain(readOnlyTools, tool => tool.Access == ToolAccess.Mutating);
        Assert.Contains(readOnlyTools, tool => tool.Name == "read_file");

        foreach (var mode in new[] { ChatMode.Confirm, ChatMode.Auto })
        {
            var tools = ChatService.SelectToolsForMode(persianNginxPrompt, registry, mode);
            Assert.Contains(tools, tool => tool.Name == "edit_config");
            Assert.Contains(tools, tool => tool.Name == "restart_container");
            Assert.Contains(tools, tool => tool.Name == "run_shell");
            Assert.Equal(registry.Tools.Count, tools.Length);
        }

        Assert.Empty(ChatService.SelectToolsForMode("anything I should look at?", registry, ChatMode.Auto));
    }

    [Fact]
    public async Task ReadOnlyChatModeBlocksMutatingToolCallsWithoutCreatingActionRecords()
    {
        await using var rig = await Rig.CreateAsync("staging", ExecutionMode.Confirm);

        var result = await rig.Dispatcher.DispatchAsync("restart_container",
            JsonSerializer.SerializeToElement(new { container = "web", serverIds = new[] { rig.Server.Id } }),
            null, CancellationToken.None, ChatMode.ReadOnly);

        Assert.False(result.NeedsApproval);
        Assert.Contains("Read-only mode", result.Summary, StringComparison.Ordinal);
        Assert.Empty(await rig.Context.ActionRecords.ToListAsync());
        Assert.Empty(rig.Runner.Commands);
    }

    [Fact]
    public async Task AutoChatModeExecutesSafeTypedToolsAndLogsThemButStillConfirmsHighRiskTools()
    {
        await using var rig = await Rig.CreateAsync("staging", ExecutionMode.Confirm);

        // Safe typed tool (restart_container) auto-executes and logs under "auto (chat)".
        var containerResult = await rig.Dispatcher.DispatchAsync("restart_container",
            JsonSerializer.SerializeToElement(new { container = "web", serverIds = new[] { rig.Server.Id } }),
            null, CancellationToken.None, ChatMode.Auto);
        Assert.False(containerResult.NeedsApproval);
        Assert.Contains("Auto-executed restart_container", containerResult.Summary, StringComparison.Ordinal);
        Assert.Single(rig.Runner.Commands);
        var containerRecord = await rig.Context.ActionRecords.SingleAsync();
        Assert.Equal(ActionRecordStatus.Succeeded, containerRecord.Status);
        Assert.Equal("auto (chat)", containerRecord.ApprovedBy);

        // Safe non-critical systemd unit auto-executes.
        var nginxRestart = await rig.Dispatcher.DispatchAsync("restart_service",
            JsonSerializer.SerializeToElement(new { unit = "nginx", serverIds = new[] { rig.Server.Id } }),
            null, CancellationToken.None, ChatMode.Auto);
        Assert.False(nginxRestart.NeedsApproval);
        Assert.Contains("Auto-executed restart_service", nginxRestart.Summary, StringComparison.Ordinal);

        // High-risk tools STILL require explicit approval even in Auto mode.
        var shellResult = await rig.Dispatcher.DispatchAsync("run_shell",
            JsonSerializer.SerializeToElement(new { command = "id", serverIds = new[] { rig.Server.Id } }),
            null, CancellationToken.None, ChatMode.Auto);
        Assert.True(shellResult.NeedsApproval);

        var updatesResult = await rig.Dispatcher.DispatchAsync("apply_updates",
            JsonSerializer.SerializeToElement(new { packages = new[] { "nginx" }, serverIds = new[] { rig.Server.Id } }),
            null, CancellationToken.None, ChatMode.Auto);
        Assert.True(updatesResult.NeedsApproval);

        var criticalRestart = await rig.Dispatcher.DispatchAsync("restart_service",
            JsonSerializer.SerializeToElement(new { unit = "sshd", confirmCritical = true, serverIds = new[] { rig.Server.Id } }),
            null, CancellationToken.None, ChatMode.Auto);
        Assert.True(criticalRestart.NeedsApproval);
    }

    [Fact]
    public async Task AutoChatModeNeverBypassesReadOnlyServerGroup()
    {
        await using var rig = await Rig.CreateAsync("prod", ExecutionMode.ReadOnly);

        var result = await rig.Dispatcher.DispatchAsync("restart_container",
            JsonSerializer.SerializeToElement(new { container = "web", serverIds = new[] { rig.Server.Id } }),
            null, CancellationToken.None, ChatMode.Auto);

        Assert.False(result.NeedsApproval);
        Assert.Contains("read-only mode", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await rig.Context.ActionRecords.ToListAsync());
        Assert.Empty(rig.Runner.Commands);
    }

    [Fact]
    public async Task SystemPromptIncludesClarifyingQuestionsRuleAndActiveModeGuidance()
    {
        await using var chat = await ChatServiceHarness.CreateAsync();
        var conversationId = await chat.Service.CreateAsync(CancellationToken.None);
        await chat.Service.SetModeAsync(conversationId, ChatMode.Auto, CancellationToken.None);

        await chat.SendAsync(conversationId, "دامنه جدید اضافه کن");

        var systemPrompt = chat.Client.Requests.Single()[0].Text;
        Assert.Contains("ask concise clarifying questions first", systemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Current chat mode: Auto", systemPrompt, StringComparison.Ordinal);
        Assert.Contains("ALWAYS ask clarifying questions first", systemPrompt, StringComparison.Ordinal);
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private Rig(SqliteConnection connection, AppDbContext context, Server server, RecordingRunner runner, AgentToolDispatcher dispatcher)
        {
            _connection = connection;
            Context = context;
            Server = server;
            Runner = runner;
            Dispatcher = dispatcher;
        }

        public AppDbContext Context { get; }
        public Server Server { get; }
        public RecordingRunner Runner { get; }
        public AgentToolDispatcher Dispatcher { get; }

        public static async Task<Rig> CreateAsync(string group, ExecutionMode groupMode)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
            await context.Database.EnsureCreatedAsync();

            var server = new Server("srv-edge", "10.88.10.10", 22, "dva", "id_ed25519", group);
            context.Servers.Add(server);
            await context.SaveChangesAsync();

            var settings = new AppSettingRepository(context);
            var serverRepo = new ServerRepository(context);
            var executionModes = new ExecutionModeService(settings, serverRepo, NullLogger<ExecutionModeService>.Instance);
            await executionModes.SaveAsync(new ExecutionModePolicy(group, groupMode), CancellationToken.None);

            var registry = new ToolRegistry();
            var builder = new ToolCommandBuilder(registry);
            var actions = new ActionRecordRepository(context);
            var approvals = new ApprovalService(registry, executionModes, serverRepo, actions, TestBanGuard.Create(), TimeProvider.System);
            var runner = new RecordingRunner();
            var execution = new ActionExecutionService(actions, serverRepo, registry, executionModes, builder, runner, TestBanGuard.Create(), TimeProvider.System);
            var snapshots = new SnapshotRepository(context);
            var graphs = new GraphRepository(context);
            var discovery = new DiscoveryService(serverRepo, snapshots, graphs, new GraphBuilder(), runner, [], TimeProvider.System);
            var dispatcher = new AgentToolDispatcher(registry, approvals, execution, builder, serverRepo, graphs, snapshots, discovery, runner);

            return new Rig(connection, context, server, runner, dispatcher);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class RecordingRunner : ISshRunner
    {
        public List<string> Commands { get; } = [];

        public Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options, CancellationToken cancellationToken)
        {
            Commands.Add(command);
            return Task.FromResult(new RemoteCommandResult(0, "ok\n", string.Empty, TimeSpan.FromMilliseconds(10)));
        }
    }
}
