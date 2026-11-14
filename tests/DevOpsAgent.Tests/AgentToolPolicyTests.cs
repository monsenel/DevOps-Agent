using System.Text.Json;
using DevOpsAgent.Application.Agent;
using DevOpsAgent.Domain.Agent;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DevOpsAgent.Tests;

public sealed class AgentToolPolicyTests
{
    [Fact]
    public void RegistryContainsEveryAgreedToolAndMarksMutations()
    {
        var registry = new ToolRegistry();

        Assert.Equal(16, registry.Tools.Count);
        Assert.Equal(
            new[] { "apply_updates", "block_ip", "restart_service", "run_shell", "unblock_ip" },
            registry.Tools.Where(tool => tool.Access == ToolAccess.Mutating).Select(tool => tool.Name).Order().ToArray());
        Assert.True(registry.TryGet("run_shell", out var shell));
        Assert.True(shell.RequiresApproval);
    }

    [Fact]
    public void RegistryValidatesRequiredArgumentsAndRejectsUnknownProperties()
    {
        var registry = new ToolRegistry();

        Assert.Throws<ArgumentException>(() => registry.ValidateArguments("service_status", Json("{}")));
        Assert.Throws<ArgumentException>(() => registry.ValidateArguments("service_status", Json("{\"unit\":\"nginx\",\"sudo\":true}")));
        Assert.Throws<ArgumentException>(() => registry.ValidateArguments("restart_service", Json("{\"unit\":\"nginx; reboot\"}")));
        Assert.Throws<ArgumentException>(() => registry.ValidateArguments("block_ip", Json("{\"address\":\"10.0.0.1; reboot\"}")));
        Assert.Throws<ArgumentException>(() => registry.ValidateArguments("apply_updates", Json("{\"packages\":[\"nginx; reboot\"]}")));
        Assert.Throws<ArgumentException>(() => registry.ValidateArguments("missing_tool", Json("{}")));
        Assert.Equal("nginx", Assert.IsType<ServiceStatusArgs>(registry.ValidateArguments(
            "service_status", Json("{\"unit\":\"nginx\"}"))).Unit);
    }

    [Theory]
    [InlineData(ExecutionMode.ReadOnly, ToolAccess.ReadOnly, ToolApprovalState.ExecuteReadOnly)]
    [InlineData(ExecutionMode.ReadOnly, ToolAccess.Mutating, ToolApprovalState.Denied)]
    [InlineData(ExecutionMode.Confirm, ToolAccess.ReadOnly, ToolApprovalState.ExecuteReadOnly)]
    [InlineData(ExecutionMode.Confirm, ToolAccess.Mutating, ToolApprovalState.PendingApproval)]
    [InlineData(ExecutionMode.Auto, ToolAccess.ReadOnly, ToolApprovalState.ExecuteReadOnly)]
    [InlineData(ExecutionMode.Auto, ToolAccess.Mutating, ToolApprovalState.ExecuteAutomatically)]
    public async Task ApprovalPolicyEnforcesModeAndToolType(
        ExecutionMode mode,
        ToolAccess access,
        ToolApprovalState expected)
    {
        await using var database = await CreateDatabaseAsync();
        var server = await AddServerAsync(database.Context, "prod");
        var registry = new ToolRegistry();
        var settings = new AppSettingRepository(database.Context);
        var executionModes = new ExecutionModeService(settings, new ServerRepository(database.Context), registry);
        string[] allowlist = mode == ExecutionMode.Auto && access == ToolAccess.Mutating ? ["restart_service"] : [];
        await executionModes.SaveAsync(new ExecutionModePolicy("prod", mode, allowlist), CancellationToken.None);
        var service = new ApprovalService(registry, executionModes, new ServerRepository(database.Context),
            new ActionRecordRepository(database.Context), TimeProvider.System);
        var toolName = access == ToolAccess.ReadOnly ? "service_status" : "restart_service";
        const string args = "{\"unit\":\"nginx\"}";

        var result = await service.EvaluateAsync(new ToolApprovalRequest(toolName, Json(args), [server.Id]), CancellationToken.None);

        Assert.Equal(expected, result.State);
        if (expected == ToolApprovalState.PendingApproval)
        {
            var record = await database.Context.ActionRecords.SingleAsync();
            Assert.Equal(ActionRecordStatus.Proposed, record.Status);
            Assert.Equal(toolName, record.ToolName);
            Assert.Equal(server.Id.ToString(), JsonSerializer.Deserialize<string[]>(record.ServerIdsJson)![0]);
        }
        else
        {
            Assert.Empty(await database.Context.ActionRecords.ToListAsync());
        }
    }

    [Theory]
    [InlineData(ExecutionMode.Confirm)]
    [InlineData(ExecutionMode.Auto)]
    public async Task RunShellAlwaysCreatesPendingApprovalEvenInAutomaticMode(ExecutionMode mode)
    {
        await using var database = await CreateDatabaseAsync();
        var server = await AddServerAsync(database.Context, "prod");
        var registry = new ToolRegistry();
        var executionModes = new ExecutionModeService(new AppSettingRepository(database.Context),
            new ServerRepository(database.Context), registry);
        string[] allowlist = mode == ExecutionMode.Auto ? ["restart_service"] : [];
        await executionModes.SaveAsync(new ExecutionModePolicy("prod", mode, allowlist), CancellationToken.None);
        var service = new ApprovalService(registry, executionModes, new ServerRepository(database.Context),
            new ActionRecordRepository(database.Context), TimeProvider.System);

        var result = await service.EvaluateAsync(new ToolApprovalRequest("run_shell", Json("{\"command\":\"id\"}"), [server.Id]),
            CancellationToken.None);

        Assert.Equal(ToolApprovalState.PendingApproval, result.State);
        Assert.Equal(ActionRecordStatus.Proposed, (await database.Context.ActionRecords.SingleAsync()).Status);
    }

    [Fact]
    public async Task AutomaticModeRequiresToolAllowlistAndReadOnlyGroupBlocksChanges()
    {
        await using var database = await CreateDatabaseAsync();
        var server = await AddServerAsync(database.Context, "prod");
        var registry = new ToolRegistry();
        var settings = new AppSettingRepository(database.Context);
        var executionModes = new ExecutionModeService(settings, new ServerRepository(database.Context), registry);
        var service = new ApprovalService(registry, executionModes, new ServerRepository(database.Context),
            new ActionRecordRepository(database.Context), TimeProvider.System);

        await executionModes.SaveAsync(new ExecutionModePolicy("prod", ExecutionMode.Auto, []), CancellationToken.None);
        var outsideAllowlist = await service.EvaluateAsync(new ToolApprovalRequest("restart_service", Json("{\"unit\":\"nginx\"}"), [server.Id]),
            CancellationToken.None);
        Assert.Equal(ToolApprovalState.PendingApproval, outsideAllowlist.State);

        await executionModes.SaveAsync(new ExecutionModePolicy("prod", ExecutionMode.ReadOnly, []), CancellationToken.None);
        var readOnlyResult = await service.EvaluateAsync(new ToolApprovalRequest("restart_service", Json("{\"unit\":\"nginx\"}"), [server.Id]),
            CancellationToken.None);
        Assert.Equal(ToolApprovalState.Denied, readOnlyResult.State);
    }

    [Fact]
    public async Task RemoteToolRequiresTargetServer()
    {
        await using var database = await CreateDatabaseAsync();
        var registry = new ToolRegistry();
        var executionModes = new ExecutionModeService(new AppSettingRepository(database.Context),
            new ServerRepository(database.Context), registry);
        var service = new ApprovalService(registry, executionModes, new ServerRepository(database.Context),
            new ActionRecordRepository(database.Context), TimeProvider.System);

        var result = await service.EvaluateAsync(new ToolApprovalRequest("service_status", Json("{\"unit\":\"nginx\"}"), []),
            CancellationToken.None);

        Assert.Equal(ToolApprovalState.Denied, result.State);
        Assert.Contains("target server", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecutionModeSettingsPersistWithConfirmAsDefault()
    {
        await using var database = await CreateDatabaseAsync();
        var server = await AddServerAsync(database.Context, "staging");
        var registry = new ToolRegistry();
        var service = new ExecutionModeService(new AppSettingRepository(database.Context),
            new ServerRepository(database.Context), registry);

        var defaultPolicy = Assert.Single(await service.ListAsync(CancellationToken.None));
        Assert.Equal("staging", defaultPolicy.Group);
        Assert.Equal(ExecutionMode.Confirm, defaultPolicy.Mode);
        Assert.Empty(defaultPolicy.AllowedTools);
        await service.SaveAsync(new ExecutionModePolicy("staging", ExecutionMode.Auto, ["block_ip"]), CancellationToken.None);
        var loaded = await service.GetForGroupAsync(server.Group, CancellationToken.None);
        Assert.Equal(ExecutionMode.Auto, loaded.Mode);
        Assert.Equal(new[] { "block_ip" }, loaded.AllowedTools);
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(
            new ExecutionModePolicy("staging", ExecutionMode.Auto, ["run_shell"]), CancellationToken.None));
    }

    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();

    private static async Task<Server> AddServerAsync(AppDbContext context, string group)
    {
        var server = new Server("server-" + group, "host.example.internal", 22, "deploy", "key", group);
        context.Servers.Add(server);
        await context.SaveChangesAsync();
        return server;
    }

    private static async Task<SqliteTestDatabase> CreateDatabaseAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var context = new AppDbContext(options);
        await context.Database.EnsureCreatedAsync();
        return new SqliteTestDatabase(connection, context);
    }

    private sealed class SqliteTestDatabase(SqliteConnection connection, AppDbContext context) : IAsyncDisposable
    {
        public AppDbContext Context { get; } = context;

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
