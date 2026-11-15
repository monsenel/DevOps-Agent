using System.Text.Json;
using DevOpsAgent.Application.Agent;
using DevOpsAgent.Domain.Agent;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

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
    [InlineData("read_file", "/mnt/backup/etc/shadow-")]
    [InlineData("read_file", "/mnt/backup/etc/gshadow")]
    [InlineData("read_file", "/proc/1/root/etc/shadow")]
    [InlineData("read_file", "/proc/self/root/etc/hostname")]
    [InlineData("tail_log", "/proc/4242/task/4242/environ")]
    [InlineData("read_file", "/etc/ssh/ssh_host_ed25519_key")]
    [InlineData("read_file", "/etc/ssh/ssh_host_rsa_key")]
    [InlineData("read_file", "/home/deploy/.aws/credentials")]
    [InlineData("read_file", "/home/deploy/.docker/config.json")]
    [InlineData("read_file", "/home/deploy/.git-credentials")]
    [InlineData("read_file", "/home/deploy/.netrc")]
    [InlineData("tail_log", "/home/deploy/.bash_history")]
    [InlineData("tail_log", "/root/.mysql_history")]
    [InlineData("read_file", "/etc/wireguard/wg0.conf")]
    [InlineData("read_file", "/etc/nginx/.htpasswd")]
    [InlineData("read_file", "/etc/letsencrypt/live/example.com/privkey.pem")]
    [InlineData("read_file", "/etc/pki/tls/server-key.pem")]
    [InlineData("read_file", "/etc/nginx/tls/server.pem")]
    [InlineData("read_file", "/etc/nginx/tls/combined.pem")]
    [InlineData("read_file", "/srv/app/tls.PEM")]
    [InlineData("read_file", "/srv/app/identity.p12")]
    [InlineData("read_file", "/srv/app/identity.PFX")]
    [InlineData("read_file", "/srv/app/truststore.jks")]
    [InlineData("read_file", "/srv/app/app.keystore")]
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
    [InlineData("read_file", "/etc/letsencrypt/live/example.com/fullchain.pem")]
    [InlineData("read_file", "/etc/letsencrypt/live/example.com/cert.pem")]
    [InlineData("read_file", "/etc/letsencrypt/live/example.com/chain.pem")]
    [InlineData("read_file", "/etc/ssl/certs/ca.pem")]
    [InlineData("read_file", "/etc/ssl/certs/ca-bundle.pem")]
    [InlineData("read_file", "/etc/ssl/certs/site.crt")]
    [InlineData("read_file", "/etc/ssh/ssh_host_ed25519_key.pub")]
    [InlineData("read_file", "/etc/ssh/sshd_config")]
    [InlineData("read_file", "/proc/cpuinfo")]
    [InlineData("read_file", "/proc/1234/status")]
    [InlineData("read_file", "/etc/wireguard-tools.txt")]
    [InlineData("tail_log", "/var/log/history.log")]
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
        Assert.Contains("df -P -- / /var/lib/docker", disk.Command, StringComparison.Ordinal);
        Assert.Contains("for path in / /var/lib/docker; do", disk.Command, StringComparison.Ordinal);
        Assert.DoesNotContain("/lab", disk.Command, StringComparison.Ordinal);
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
    public async Task ApprovalPolicyEnforcesModeAndToolType(
        ExecutionMode mode,
        ToolAccess access,
        ToolApprovalState expected)
    {
        await using var database = await CreateDatabaseAsync();
        var server = await AddServerAsync(database.Context, "prod");
        var registry = new ToolRegistry();
        var settings = new AppSettingRepository(database.Context);
        var executionModes = new ExecutionModeService(settings, new ServerRepository(database.Context), NullLogger<ExecutionModeService>.Instance);
        await executionModes.SaveAsync(new ExecutionModePolicy("prod", mode), CancellationToken.None);
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
    [InlineData(ToolAccess.ReadOnly, ToolApprovalState.ExecuteReadOnly)]
    [InlineData(ToolAccess.Mutating, ToolApprovalState.PendingApproval)]
    public async Task StoredAutoModeBehavesAsConfirmAndStoredAllowlistsAreIgnored(ToolAccess access, ToolApprovalState expected)
    {
        await using var database = await CreateDatabaseAsync();
        var server = await AddServerAsync(database.Context, "prod");
        var settings = new AppSettingRepository(database.Context);
        // Data written while "Auto within allowlist" existed (mode 2, with an allowlist naming the very tool being called).
        await settings.SetAsync("agent.execution-modes",
            "{\"prod\":{\"group\":\"prod\",\"mode\":2,\"allowedTools\":[\"restart_service\"]}}", CancellationToken.None);
        var executionModes = new ExecutionModeService(settings, new ServerRepository(database.Context), NullLogger<ExecutionModeService>.Instance);
        var service = new ApprovalService(new ToolRegistry(), executionModes, new ServerRepository(database.Context),
            new ActionRecordRepository(database.Context), TimeProvider.System);
        var toolName = access == ToolAccess.ReadOnly ? "service_status" : "restart_service";

        var policy = await executionModes.GetForGroupAsync("prod", CancellationToken.None);
        var result = await service.EvaluateAsync(new ToolApprovalRequest(toolName, Json("{\"unit\":\"nginx\"}"), [server.Id]), CancellationToken.None);

        Assert.Equal(ExecutionMode.Confirm, policy.Mode);
        Assert.False(await executionModes.IsStoredSettingUnreadableAsync(CancellationToken.None));
        Assert.Equal(expected, result.State);
        Assert.Equal(expected == ToolApprovalState.PendingApproval ? ActionRecordStatus.Proposed : (ActionRecordStatus?)null,
            (await database.Context.ActionRecords.SingleOrDefaultAsync())?.Status);
    }

    [Fact]
    public async Task OnlyReadOnlyAndConfirmCanBeSavedAndNothingProducesAuto()
    {
        await using var database = await CreateDatabaseAsync();
        await AddServerAsync(database.Context, "prod");
        var settings = new AppSettingRepository(database.Context);
        var service = new ExecutionModeService(settings, new ServerRepository(database.Context), NullLogger<ExecutionModeService>.Instance);

        // A cast value 2 (the retired Auto) must be rejected by save validation.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.SaveAsync(new ExecutionModePolicy("prod", (ExecutionMode)2), CancellationToken.None));
        Assert.Equal(new[] { ExecutionMode.ReadOnly, ExecutionMode.Confirm }, Enum.GetValues<ExecutionMode>());
        await service.SaveAsync(new ExecutionModePolicy("prod", ExecutionMode.Confirm), CancellationToken.None);
        Assert.DoesNotContain("allowedTools", (await settings.GetAsync("agent.execution-modes", CancellationToken.None))!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("{\"prod\":{\"group\":\"prod\",\"mode\":3}}")]
    [InlineData("{\"prod\":{\"group\":\"prod\",\"mode\":-1}}")]
    [InlineData("{\"prod\":null}")]
    public async Task UnknownStoredModesAreStillCorruptAndFailClosed(string stored)
    {
        await using var database = await CreateDatabaseAsync();
        await AddServerAsync(database.Context, "prod");
        var settings = new AppSettingRepository(database.Context);
        await settings.SetAsync("agent.execution-modes", stored, CancellationToken.None);
        var service = new ExecutionModeService(settings, new ServerRepository(database.Context), NullLogger<ExecutionModeService>.Instance);

        Assert.True(await service.IsStoredSettingUnreadableAsync(CancellationToken.None));
        Assert.Equal(ExecutionMode.ReadOnly, (await service.GetForGroupAsync("prod", CancellationToken.None)).Mode);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RunShellAlwaysCreatesPendingApprovalEvenForStoredAutoMode(int storedMode)
    {
        await using var database = await CreateDatabaseAsync();
        var server = await AddServerAsync(database.Context, "prod");
        var registry = new ToolRegistry();
        var executionModes = new ExecutionModeService(new AppSettingRepository(database.Context),
            new ServerRepository(database.Context), NullLogger<ExecutionModeService>.Instance);
        await new AppSettingRepository(database.Context).SetAsync("agent.execution-modes",
            $"{{\"prod\":{{\"group\":\"prod\",\"mode\":{storedMode},\"allowedTools\":[\"run_shell\"]}}}}", CancellationToken.None);
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
        var executionModes = new ExecutionModeService(new AppSettingRepository(database.Context), new ServerRepository(database.Context), NullLogger<ExecutionModeService>.Instance);
        await new AppSettingRepository(database.Context).SetAsync("agent.execution-modes",
            "{\"staging\":{\"group\":\"staging\",\"mode\":2,\"allowedTools\":[\"apply_updates\"]}}", CancellationToken.None);
        var service = new ApprovalService(registry, executionModes, new ServerRepository(database.Context),
            new ActionRecordRepository(database.Context), TimeProvider.System);

        var result = await service.EvaluateAsync(new ToolApprovalRequest("apply_updates",
            Json($"{{\"packages\":[\"nginx\"],\"serverIds\":[\"{server.Id}\"]}}"), [server.Id]), CancellationToken.None);

        Assert.Equal(ToolApprovalState.PendingApproval, result.State);
        Assert.Equal(ActionRecordStatus.Proposed, (await database.Context.ActionRecords.SingleAsync()).Status);
    }

    [Fact]
    public async Task StoredKeysAreNormalisedOnReadSoMixedCaseKeysStillApply()
    {
        await using var database = await CreateDatabaseAsync();
        await AddServerAsync(database.Context, "prod");
        var settings = new AppSettingRepository(database.Context);
        await settings.SetAsync("agent.execution-modes",
            "{\" Prod \":{\"group\":\" Prod \",\"mode\":0,\"allowedTools\":[]}}", CancellationToken.None);
        var service = new ExecutionModeService(settings, new ServerRepository(database.Context), NullLogger<ExecutionModeService>.Instance);

        Assert.False(await service.IsStoredSettingUnreadableAsync(CancellationToken.None));
        Assert.Equal(ExecutionMode.ReadOnly, (await service.GetForGroupAsync("prod", CancellationToken.None)).Mode);
        Assert.Equal(ExecutionMode.ReadOnly, (await service.GetForGroupAsync("PROD", CancellationToken.None)).Mode);
        Assert.Equal("prod", Assert.Single(await service.ListAsync(CancellationToken.None)).Group);
    }

    [Theory]
    [InlineData("{\"prod\":{\"group\":\"prod\",\"mode\":0,\"allowedTools\":[]},\"PROD\":{\"group\":\"PROD\",\"mode\":2,\"allowedTools\":[]}}")]
    [InlineData("{\"prod\":{\"group\":\"prod\",\"mode\":0,\"allowedTools\":[]},\" prod\":{\"group\":\"prod\",\"mode\":1,\"allowedTools\":[]}}")]
    [InlineData("{\"  \":{\"group\":\"x\",\"mode\":1,\"allowedTools\":[]}}")]
    public async Task CollidingOrBlankNormalisedKeysAreTreatedAsCorrupt(string stored)
    {
        await using var database = await CreateDatabaseAsync();
        await AddServerAsync(database.Context, "prod");
        var settings = new AppSettingRepository(database.Context);
        await settings.SetAsync("agent.execution-modes", stored, CancellationToken.None);
        var service = new ExecutionModeService(settings, new ServerRepository(database.Context), NullLogger<ExecutionModeService>.Instance);

        Assert.True(await service.IsStoredSettingUnreadableAsync(CancellationToken.None));
        // Fail closed: the weaker colliding entry must not win.
        Assert.Equal(ExecutionMode.ReadOnly, (await service.GetForGroupAsync("prod", CancellationToken.None)).Mode);
    }

    [Fact]
    public async Task ConfirmModeRequiresApprovalAndReadOnlyGroupBlocksChanges()
    {
        await using var database = await CreateDatabaseAsync();
        var server = await AddServerAsync(database.Context, "prod");
        var registry = new ToolRegistry();
        var settings = new AppSettingRepository(database.Context);
        var executionModes = new ExecutionModeService(settings, new ServerRepository(database.Context), NullLogger<ExecutionModeService>.Instance);
        var service = new ApprovalService(registry, executionModes, new ServerRepository(database.Context),
            new ActionRecordRepository(database.Context), TimeProvider.System);

        await executionModes.SaveAsync(new ExecutionModePolicy("prod", ExecutionMode.Confirm), CancellationToken.None);
        var needsApproval = await service.EvaluateAsync(new ToolApprovalRequest("restart_service", Json("{\"unit\":\"nginx\"}"), [server.Id]),
            CancellationToken.None);
        Assert.Equal(ToolApprovalState.PendingApproval, needsApproval.State);

        await executionModes.SaveAsync(new ExecutionModePolicy("prod", ExecutionMode.ReadOnly), CancellationToken.None);
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
            new ServerRepository(database.Context), NullLogger<ExecutionModeService>.Instance);
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
        var service = new ExecutionModeService(new AppSettingRepository(database.Context),
            new ServerRepository(database.Context), NullLogger<ExecutionModeService>.Instance);

        var defaultPolicy = Assert.Single(await service.ListAsync(CancellationToken.None));
        Assert.Equal("staging", defaultPolicy.Group);
        Assert.Equal(ExecutionMode.Confirm, defaultPolicy.Mode);
        await service.SaveAsync(new ExecutionModePolicy("staging", ExecutionMode.ReadOnly), CancellationToken.None);
        Assert.Equal(ExecutionMode.ReadOnly, (await service.GetForGroupAsync(server.Group, CancellationToken.None)).Mode);
        await service.SaveAsync(new ExecutionModePolicy("staging", ExecutionMode.Confirm), CancellationToken.None);
        Assert.Equal(ExecutionMode.Confirm, (await service.GetForGroupAsync(server.Group, CancellationToken.None)).Mode);
    }

    [Fact]
    public async Task CorruptExecutionModeSettingFailsClosedToReadOnly()
    {
        await using var database = await CreateDatabaseAsync();
        await AddServerAsync(database.Context, "prod");
        var settings = new AppSettingRepository(database.Context);
        await settings.SetAsync("agent.execution-modes", "{ not valid json", CancellationToken.None);
        var service = new ExecutionModeService(settings, new ServerRepository(database.Context), NullLogger<ExecutionModeService>.Instance);

        Assert.Equal(ExecutionMode.ReadOnly, (await service.GetForGroupAsync("prod", CancellationToken.None)).Mode);
        Assert.Equal(ExecutionMode.ReadOnly, (await service.GetAsync("never-seen", CancellationToken.None)).Mode);
        Assert.All(await service.ListAsync(CancellationToken.None), policy => Assert.Equal(ExecutionMode.ReadOnly, policy.Mode));

        await service.SaveAsync(new ExecutionModePolicy("prod", ExecutionMode.Confirm), CancellationToken.None);
        Assert.Equal(ExecutionMode.Confirm, (await service.GetForGroupAsync("prod", CancellationToken.None)).Mode);
        Assert.Equal(ExecutionMode.Confirm, (await service.GetForGroupAsync("unknown-group", CancellationToken.None)).Mode);
    }

    [Fact]
    public async Task SavingOneGroupOfACorruptSettingKeepsEveryOtherKnownGroupReadOnly()
    {
        await using var database = await CreateDatabaseAsync();
        await AddServerAsync(database.Context, "prod");
        await AddServerAsync(database.Context, "staging");
        var settings = new AppSettingRepository(database.Context);
        await settings.SetAsync("agent.execution-modes", "{ not valid json", CancellationToken.None);
        var service = new ExecutionModeService(settings, new ServerRepository(database.Context), NullLogger<ExecutionModeService>.Instance);
        Assert.True(await service.IsStoredSettingUnreadableAsync(CancellationToken.None));

        await service.SaveAsync(new ExecutionModePolicy("staging", ExecutionMode.Confirm), CancellationToken.None);

        Assert.False(await service.IsStoredSettingUnreadableAsync(CancellationToken.None));
        Assert.Equal(ExecutionMode.Confirm, (await service.GetForGroupAsync("staging", CancellationToken.None)).Mode);
        // prod was read-only while the setting was unreadable and must not fall back to Confirm.
        Assert.Equal(ExecutionMode.ReadOnly, (await service.GetForGroupAsync("prod", CancellationToken.None)).Mode);
        Assert.Equal(new[] { ExecutionMode.ReadOnly, ExecutionMode.Confirm },
            (await service.ListAsync(CancellationToken.None)).OrderBy(item => item.Group).Select(item => item.Mode));
    }

    [Theory]
    [InlineData("{\"prod\":null}")]
    [InlineData("{\"prod\":{\"group\":\"prod\",\"mode\":99,\"allowedTools\":[]}}")]
    [InlineData("null")]
    public async Task NullEntriesAndUndefinedModesAreTreatedAsCorrupt(string stored)
    {
        await using var database = await CreateDatabaseAsync();
        await AddServerAsync(database.Context, "prod");
        var settings = new AppSettingRepository(database.Context);
        await settings.SetAsync("agent.execution-modes", stored, CancellationToken.None);
        var service = new ExecutionModeService(settings, new ServerRepository(database.Context), NullLogger<ExecutionModeService>.Instance);

        Assert.True(await service.IsStoredSettingUnreadableAsync(CancellationToken.None));
        Assert.Equal(ExecutionMode.ReadOnly, (await service.GetForGroupAsync("prod", CancellationToken.None)).Mode);
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
