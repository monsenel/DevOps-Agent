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

        Assert.Equal(18, registry.Tools.Count);
        Assert.Equal(
            new[] { "apply_updates", "block_ip", "restart_container", "restart_service", "run_shell", "unblock_ip" },
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
        Assert.Throws<ArgumentException>(() => registry.ValidateArguments("restart_container", Json("{\"container\":\"web; reboot\"}")));
        Assert.Throws<ArgumentException>(() => registry.ValidateArguments("missing_tool", Json("{}")));
        Assert.Equal("nginx", Assert.IsType<ServiceStatusArgs>(registry.ValidateArguments(
            "service_status", Json("{\"unit\":\"nginx\"}"))).Unit);
    }

    [Theory]
    [InlineData("read_file", "/home/deploy/.ssh/id_rsa")]
    [InlineData("read_file", "/root/.ssh/authorized_keys")]
    [InlineData("read_file", "/home/deploy/x/../.ssh/config")]
    [InlineData("read_file", "/srv/app/.env")]
    [InlineData("read_file", "/srv/app/.env.production")]
    [InlineData("tail_log", "/proc/1234/environ")]
    [InlineData("tail_log", "/proc/self/environ")]
    [InlineData("read_file", "/etc/ssl/private/site.pem")]
    [InlineData("read_file", "/etc/ssl/private/site.key")]
    [InlineData("read_file", "/opt/keys/id_ed25519")]
    [InlineData("read_file", "/opt/keys/id_ecdsa.bak")]
    [InlineData("read_file", "/etc/shadow")]
    [InlineData("read_file", "/etc//gshadow")]
    [InlineData("read_file", "/var/log/../../etc/shadow")]
    public void FileToolsRejectSensitivePaths(string tool, string path)
    {
        var registry = new ToolRegistry();
        var args = Json(JsonSerializer.Serialize(new { path, serverIds = new[] { Guid.NewGuid() } }));

        Assert.Throws<ArgumentException>(() => registry.ValidateArguments(tool, args));
    }

    [Theory]
    [InlineData("read_file", "/etc/nginx/nginx.conf")]
    [InlineData("tail_log", "/var/log/nginx/error.log")]
    [InlineData("tail_log", "/var/log/auth.log")]
    [InlineData("read_file", "/srv/app/environment.md")]
    [InlineData("read_file", "/home/deploy/ssh-notes.txt")]
    public void FileToolsStillAllowOrdinaryPaths(string tool, string path)
    {
        var registry = new ToolRegistry();
        var args = Json(JsonSerializer.Serialize(new { path, serverIds = new[] { Guid.NewGuid() } }));
        var typed = registry.ValidateArguments(tool, args);

        var command = new ToolCommandBuilder(registry).BuildReadOnly(tool, typed);

        Assert.Contains(path, command.Command, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => new ToolCommandBuilder(registry).BuildReadOnly(
            "read_file", new ReadFileArgs("/etc/shadow")));
    }

    [Theory]
    [InlineData("service_status", "{\"unit\":\"nginx\\n\"}")]
    [InlineData("restart_service", "{\"unit\":\"nginx\\n\",\"serverIds\":[\"c4d7d14f-897d-4b31-8d33-455cc6e926d6\"]}")]
    [InlineData("restart_container", "{\"container\":\"web\\n\",\"serverIds\":[\"c4d7d14f-897d-4b31-8d33-455cc6e926d6\"]}")]
    [InlineData("docker_logs", "{\"container\":\"web\\n\"}")]
    [InlineData("query_graph", "{\"view\":\"service\\n\"}")]
    public void RegexValidatedArgumentsRejectTrailingNewlines(string tool, string json)
    {
        Assert.Throws<ArgumentException>(() => new ToolRegistry().ValidateArguments(tool, Json(json)));
    }

    [Fact]
    public void BanToolsPreferFail2banThenUfwThenDedicatedNftablesSets()
    {
        var builder = new ToolCommandBuilder(new ToolRegistry());
        var read = builder.BuildReadOnly("list_bans", new ListBansArgs());
        var block = builder.Build(new ActionRecord("block_ip", "staging", "[]",
            "{\"address\":\"2001:db8::5\"}", null, DateTimeOffset.UtcNow));
        var unblock = builder.Build(new ActionRecord("unblock_ip", "staging", "[]",
            "{\"address\":\"2001:db8::5\"}", null, DateTimeOffset.UtcNow));

        Assert.Contains("fail2ban-client", read.Command, StringComparison.Ordinal);
        Assert.Contains("ufw", read.Command, StringComparison.Ordinal);
        Assert.Contains("nft list table inet dva", read.Command, StringComparison.Ordinal);
        Assert.Contains("blocked_ipv6", block.Command, StringComparison.Ordinal);
        Assert.Contains("timeout 24h", block.Command, StringComparison.Ordinal);
        Assert.True(block.UseSudo);
        Assert.Contains("nft delete element inet dva blocked_ipv6", unblock.Command, StringComparison.Ordinal);
        Assert.True(unblock.UseSudo);
    }

    [Fact]
    public void RemoteReadOnlyCommandsAreBuiltWithoutSudoAndTargetIdsAreValidated()
    {
        var registry = new ToolRegistry();
        var builder = new ToolCommandBuilder(registry);
        var arguments = registry.ValidateArguments("docker_ps", Json("{\"serverIds\":[\"c4d7d14f-897d-4b31-8d33-455cc6e926d6\"]}"));

        var command = builder.BuildReadOnly("docker_ps", arguments);

        Assert.False(command.UseSudo);
        Assert.Contains("docker ps", command.Command, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => registry.ValidateArguments("docker_ps", Json("{\"serverIds\":[]}")));
    }

    [Fact]
    public void DiskInspectionIsReadOnlyAndContainerRestartBuildsOneExactCommand()
    {
        var registry = new ToolRegistry();
        var builder = new ToolCommandBuilder(registry);
        var disk = builder.BuildReadOnly("disk_usage", new DiskUsageArgs());
        var dockerDisk = builder.BuildReadOnly("docker_disk_usage", new DockerDiskUsageArgs());
        var restart = builder.Build(new ActionRecord("restart_container", "staging", "[]",
            "{\"container\":\"web\"}", null, DateTimeOffset.UtcNow));

        Assert.True(disk.UseSudo);
        Assert.Contains("df -P", disk.Command, StringComparison.Ordinal);
        Assert.Contains("df -P -- / /lab /var/lib/docker", disk.Command, StringComparison.Ordinal);
        Assert.Contains("du -x -h --max-depth=1 -- \"$path\"", disk.Command, StringComparison.Ordinal);
        Assert.False(dockerDisk.UseSudo);
        Assert.Equal("docker system df -v", dockerDisk.Command);
        Assert.False(restart.UseSudo);
        Assert.Equal("docker restart -- 'web'", restart.Command);
    }

    [Fact]
    public void UpdateApplyCommandReportsRestartedServicesAndRebootRequirementForOneServer()
    {
        var registry = new ToolRegistry();
        var args = Json("{\"packages\":[\"nginx\"],\"serverIds\":[\"c4d7d14f-897d-4b31-8d33-455cc6e926d6\"]}");
        _ = registry.ValidateArguments("apply_updates", args);
        var command = new ToolCommandBuilder(registry).Build(new ActionRecord("apply_updates", "staging",
            "[\"c4d7d14f-897d-4b31-8d33-455cc6e926d6\"]", args.GetRawText(), null, DateTimeOffset.UtcNow));

        Assert.True(command.UseSudo);
        Assert.Contains("apt-get install -y", command.Command, StringComparison.Ordinal);
        Assert.Contains("RESTARTED_SERVICES_BEGIN", command.Command, StringComparison.Ordinal);
        // dnf's `needs-restarting -r` exits 1 when a reboot IS required, so only status 1 may report it (and set -e must not abort).
        Assert.Contains("needs-restarting -r >/dev/null 2>&1 || rc=$?", command.Command, StringComparison.Ordinal);
        Assert.Contains("if [ \"$rc\" -eq 1 ]; then printf '%s\\n' 'REBOOT_REQUIRED=true'", command.Command, StringComparison.Ordinal);
        Assert.DoesNotContain("&& needs-restarting", command.Command, StringComparison.Ordinal);
        Assert.Contains("REBOOT_REQUIRED=true", command.Command, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => registry.ValidateArguments("apply_updates",
            Json("{\"packages\":[\"nginx\"],\"serverIds\":[\"c4d7d14f-897d-4b31-8d33-455cc6e926d6\",\"392b7bfc-677d-4613-9e34-8e7ccfd8ce5a\"]}")));
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
            var records = await database.Context.ActionRecords.ToListAsync();
            if (expected == ToolApprovalState.ExecuteAutomatically)
            {
                Assert.Single(records);
                Assert.Equal(ActionRecordStatus.Proposed, records[0].Status);
            }
            else
            {
                Assert.Empty(records);
            }
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
    public async Task PackageUpdatesAlwaysRequireExplicitApproval()
    {
        await using var database = await CreateDatabaseAsync();
        var server = await AddServerAsync(database.Context, "staging");
        var registry = new ToolRegistry();
        var executionModes = new ExecutionModeService(new AppSettingRepository(database.Context), new ServerRepository(database.Context), registry);
        await executionModes.SaveAsync(new ExecutionModePolicy("staging", ExecutionMode.Auto, ["restart_service"]), CancellationToken.None);
        var service = new ApprovalService(registry, executionModes, new ServerRepository(database.Context),
            new ActionRecordRepository(database.Context), TimeProvider.System);

        var result = await service.EvaluateAsync(new ToolApprovalRequest("apply_updates",
            Json($"{{\"packages\":[\"nginx\"],\"serverIds\":[\"{server.Id}\"]}}"), [server.Id]), CancellationToken.None);

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
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(
            new ExecutionModePolicy("staging", ExecutionMode.Auto, ["apply_updates"]), CancellationToken.None));
    }

    [Fact]
    public async Task CorruptExecutionModeSettingFailsClosedToReadOnly()
    {
        await using var database = await CreateDatabaseAsync();
        await AddServerAsync(database.Context, "prod");
        var settings = new AppSettingRepository(database.Context);
        await settings.SetAsync("agent.execution-modes", "{ not valid json", CancellationToken.None);
        var service = new ExecutionModeService(settings, new ServerRepository(database.Context), new ToolRegistry());

        Assert.Equal(ExecutionMode.ReadOnly, (await service.GetForGroupAsync("prod", CancellationToken.None)).Mode);
        Assert.Equal(ExecutionMode.ReadOnly, (await service.GetAsync("never-seen", CancellationToken.None)).Mode);
        Assert.All(await service.ListAsync(CancellationToken.None), policy => Assert.Equal(ExecutionMode.ReadOnly, policy.Mode));

        await service.SaveAsync(new ExecutionModePolicy("prod", ExecutionMode.Confirm, []), CancellationToken.None);
        Assert.Equal(ExecutionMode.Confirm, (await service.GetForGroupAsync("prod", CancellationToken.None)).Mode);
        Assert.Equal(ExecutionMode.Confirm, (await service.GetForGroupAsync("unknown-group", CancellationToken.None)).Mode);
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
