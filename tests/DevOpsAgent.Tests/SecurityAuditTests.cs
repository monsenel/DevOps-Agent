using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Agent;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Application.Security;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Domain.Updates;
using DevOpsAgent.Infrastructure.Monitoring;
using DevOpsAgent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevOpsAgent.Tests;

/// <summary>T9.3 / D-065: deterministic security hardening audit parser, persistence, sudo exemption, and agent tool.</summary>
public sealed class SecurityAuditTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void HardenedDebianFixtureProducesAllPassWithZeroHighOrWarning()
    {
        var output = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "security-audit-hardened-debian.txt"));

        var report = SecurityAuditParser.Parse(Guid.NewGuid(), "srv-edge", output, Now, pendingSecurityUpdates: 0);

        Assert.Equal(0, report.HighCount);
        Assert.Equal(0, report.WarningCount);
        Assert.Equal(7, report.PassCount);
        Assert.Contains(report.Findings, item => item.CheckId == "ssh-root-login" && item.Severity == SecurityFindingSeverity.Pass);
        Assert.Contains(report.Findings, item => item.CheckId == "ssh-password-auth" && item.Severity == SecurityFindingSeverity.Pass);
        Assert.Contains(report.Findings, item => item.CheckId == "firewall-status" && item.Severity == SecurityFindingSeverity.Pass);
        Assert.Contains(report.Findings, item => item.CheckId == "fail2ban-sshd" && item.Severity == SecurityFindingSeverity.Pass);
        Assert.Contains(report.Findings, item => item.CheckId == "unattended-upgrades" && item.Severity == SecurityFindingSeverity.Pass);
    }

    [Fact]
    public void ExposedAlpineFixtureFlagsHighRiskSshAndMissingFirewall()
    {
        var output = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "security-audit-exposed-alpine.txt"));

        var report = SecurityAuditParser.Parse(Guid.NewGuid(), "srv-exposed", output, Now, pendingSecurityUpdates: 3);

        Assert.Equal(4, report.HighCount);
        Assert.Equal(4, report.WarningCount);
        Assert.Contains(report.Findings, item => item.CheckId == "ssh-root-login" && item.Severity == SecurityFindingSeverity.High);
        Assert.Contains(report.Findings, item => item.CheckId == "ssh-password-auth" && item.Severity == SecurityFindingSeverity.High);
        Assert.Contains(report.Findings, item => item.CheckId == "ssh-empty-passwords" && item.Severity == SecurityFindingSeverity.High);
        Assert.Contains(report.Findings, item => item.CheckId == "firewall-status" && item.Severity == SecurityFindingSeverity.High);
        Assert.Contains(report.Findings, item => item.CheckId == "ssh-max-auth-tries" && item.Severity == SecurityFindingSeverity.Warning);
        Assert.Contains(report.Findings, item => item.CheckId == "fail2ban-sshd" && item.Severity == SecurityFindingSeverity.Warning);
        Assert.Contains(report.Findings, item => item.CheckId == "unattended-upgrades" && item.Severity == SecurityFindingSeverity.Warning);
        Assert.Contains(report.Findings, item => item.CheckId == "updates-security-pending" && item.Severity == SecurityFindingSeverity.Warning);
    }

    [Theory]
    [InlineData("TOOL=nft\ntable inet filter {\n  chain input { type filter hook input priority 0; policy drop; }\n}\n", SecurityFindingSeverity.Pass)]
    [InlineData("TOOL=nft\ntable inet filter {\n  chain input { type filter hook input priority 0; policy accept; }\n}\n", SecurityFindingSeverity.High)]
    [InlineData("TOOL=iptables\n-P INPUT DROP\n-P FORWARD DROP\n-P OUTPUT ACCEPT\n", SecurityFindingSeverity.Pass)]
    [InlineData("TOOL=iptables\n-P INPUT ACCEPT\n-P FORWARD ACCEPT\n-P OUTPUT ACCEPT\n", SecurityFindingSeverity.High)]
    public void FirewallParserDetectsActiveAndInactiveNftablesAndIptables(string firewallBlock, SecurityFindingSeverity expected)
    {
        var output = $"__DVA_SSHD__\npermitrootlogin no\n__DVA_FIREWALL__\n{firewallBlock}__DVA_FAIL2BAN__\nF2B_NONE\n__DVA_UNATTENDED__\nUNATTENDED_NONE\n";

        var report = SecurityAuditParser.Parse(Guid.NewGuid(), "srv-fw", output, Now);

        var fw = Assert.Single(report.Findings, item => item.CheckId == "firewall-status");
        Assert.Equal(expected, fw.Severity);
    }

    [Fact]
    public async Task SecurityAuditServicePersistsReportAndWorksViaReadOnlyAgentTool()
    {
        await using var db = await CreateDatabaseAsync();
        var server = new Server("srv-edge", "10.88.10.10", 22, "dva", "id_ed25519", "prod");
        db.Context.Servers.Add(server);
        db.Context.UpdatePackages.Add(new UpdatePackage(server.Id, "openssl", "3.0.11", "3.0.15", "Debian-Security", true, Now));
        await db.Context.SaveChangesAsync();

        var fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "security-audit-exposed-alpine.txt"));
        var runner = new FixedSshRunner(fixture);
        var auditService = new SecurityAuditService(
            new ServerRepository(db.Context),
            runner,
            new AppSettingRepository(db.Context),
            new FixedTime(Now),
            new UpdatePackageRepository(db.Context));

        var registry = new ToolRegistry();
        var executionModes = new ExecutionModeService(new AppSettingRepository(db.Context), new ServerRepository(db.Context), NullLogger<ExecutionModeService>.Instance);
        var approvals = new ApprovalService(registry, executionModes, new ServerRepository(db.Context),
            new ActionRecordRepository(db.Context), TestBanGuard.Create(), new FixedTime(Now));
        var commandBuilder = new ToolCommandBuilder(registry);
        var execution = new ActionExecutionService(new ActionRecordRepository(db.Context), new ServerRepository(db.Context),
            registry, executionModes, commandBuilder, runner, TestBanGuard.Create(), new FixedTime(Now));
        var discovery = new DiscoveryService(new ServerRepository(db.Context), new SnapshotRepository(db.Context),
            new GraphRepository(db.Context), new GraphBuilder(), runner, [], new FixedTime(Now));
        var dispatcher = new AgentToolDispatcher(registry, approvals, execution, commandBuilder,
            new ServerRepository(db.Context), new GraphRepository(db.Context), new SnapshotRepository(db.Context),
            discovery, runner, securityAudit: auditService);

        // Even on a read-only prod group, security_audit is read-only and executes immediately without approval.
        var toolResult = await dispatcher.DispatchAsync("security_audit",
            JsonSerializer.SerializeToElement(new { serverIds = new[] { server.Id } }), null, CancellationToken.None);

        Assert.False(toolResult.NeedsApproval);
        Assert.Contains("high=4", toolResult.Summary, StringComparison.Ordinal);
        Assert.Contains("Root SSH login is enabled", toolResult.Summary, StringComparison.Ordinal);
        Assert.Contains("1 security update(s) pending", toolResult.Summary, StringComparison.Ordinal);

        var persisted = await auditService.GetLatestAsync(server.Id, CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.Equal(4, persisted.HighCount);
        Assert.Equal(Now, persisted.AuditedAtUtc);
    }

    [Fact]
    public void AuditCommandIsExemptInAppSudoCommands()
    {
        var logged = "/usr/bin/sh -c '"
            + SecurityAuditParser.AuditCommand.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal)
            + "'";
        Assert.True(AppSudoCommands.IsAppCommand(logged));
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

    private sealed class FixedSshRunner(string stdout) : ISshRunner
    {
        public Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options, CancellationToken cancellationToken) =>
            Task.FromResult(new RemoteCommandResult(0, stdout, string.Empty, TimeSpan.FromMilliseconds(15)));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class SqliteTestDatabase(SqliteConnection connection, AppDbContext context) : IAsyncDisposable
    {
        public AppDbContext Context { get; } = context;
        public async ValueTask DisposeAsync() { await Context.DisposeAsync(); await connection.DisposeAsync(); }
    }
}
