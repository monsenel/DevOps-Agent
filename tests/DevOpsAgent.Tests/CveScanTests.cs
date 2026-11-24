using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Agent;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Application.Security;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevOpsAgent.Tests;

public sealed class CveScanTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TrivyFixtureParsesDeduplicatesAndOrdersBySeverity()
    {
        var output = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "trivy-rootfs.json"));

        var report = CveScanParser.Parse(Guid.NewGuid(), "srv-app", output, Now);

        Assert.Equal("trivy", report.Scanner);
        Assert.True(report.ToolAvailable);
        Assert.Null(report.Error);
        Assert.Equal(4, report.TotalCount);
        Assert.Equal(1, report.CriticalCount);
        Assert.Equal(1, report.HighCount);
        Assert.Equal(1, report.MediumCount);
        Assert.Equal(1, report.LowCount);
        Assert.Equal("CVE-2025-10001", report.Findings[0].Id);
        Assert.Equal("openssl", report.Findings[0].PackageName);
        Assert.Equal("3.0.15-1~deb12u1", report.Findings[0].FixedVersion);
        Assert.Equal("CVE-2025-20002", report.Findings[1].Id);
        Assert.Null(report.Findings[3].FixedVersion);
    }

    [Fact]
    public void GrypeFixtureParsesMatchesAndNormalizesNegligibleToLow()
    {
        var output = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "grype-rootfs.json"));

        var report = CveScanParser.Parse(Guid.NewGuid(), "srv-edge", output, Now);

        Assert.Equal("grype", report.Scanner);
        Assert.True(report.ToolAvailable);
        Assert.Null(report.Error);
        Assert.Equal(3, report.TotalCount);
        Assert.Equal(1, report.CriticalCount);
        Assert.Equal(1, report.HighCount);
        Assert.Equal(1, report.LowCount);
        Assert.Equal("CVE-2025-55555", report.Findings[0].Id);
        Assert.Equal("3.0.16-1", report.Findings[0].FixedVersion);
        Assert.Equal("Low", report.Findings[2].Severity);
    }

    [Fact]
    public void NeitherScannerInstalledReportsGracefullyWithoutThrowing()
    {
        var report = CveScanParser.Parse(Guid.NewGuid(), "srv-minimal", "__DVA_CVE_SCANNER__:none\n", Now);

        Assert.Equal("none", report.Scanner);
        Assert.False(report.ToolAvailable);
        Assert.Empty(report.Findings);
        Assert.Contains("Neither trivy nor grype", report.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MalformedJsonReportsErrorWithoutThrowing()
    {
        var report = CveScanParser.Parse(Guid.NewGuid(), "srv-bad", "__DVA_CVE_SCANNER__:trivy\n{ truncated", Now);

        Assert.NotNull(report.Error);
        Assert.Empty(report.Findings);
    }

    [Fact]
    public async Task CveScanServicePersistsReportAndRunsViaReadOnlyAgentTool()
    {
        await using var db = await CreateDatabaseAsync();
        var server = new Server("srv-prod", "10.0.1.10", 22, "dva", "id_ed25519", "prod");
        db.Context.Servers.Add(server);
        await db.Context.SaveChangesAsync();

        var fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "trivy-rootfs.json"));
        var runner = new FixedSshRunner(fixture);
        var settings = new AppSettingRepository(db.Context);
        var cveService = new CveScanService(new ServerRepository(db.Context), runner, settings, new FixedTime(Now), NullLogger<CveScanService>.Instance);

        var registry = new ToolRegistry();
        var executionModes = new ExecutionModeService(settings, new ServerRepository(db.Context), NullLogger<ExecutionModeService>.Instance);
        var approvals = new ApprovalService(registry, executionModes, new ServerRepository(db.Context),
            new ActionRecordRepository(db.Context), TestBanGuard.Create(), new FixedTime(Now));
        var commandBuilder = new ToolCommandBuilder(registry);
        var execution = new ActionExecutionService(new ActionRecordRepository(db.Context), new ServerRepository(db.Context),
            registry, executionModes, commandBuilder, runner, TestBanGuard.Create(), new FixedTime(Now));
        var discovery = new DiscoveryService(new ServerRepository(db.Context), new SnapshotRepository(db.Context),
            new GraphRepository(db.Context), new GraphBuilder(), runner, [], new FixedTime(Now));
        var dispatcher = new AgentToolDispatcher(registry, approvals, execution, commandBuilder,
            new ServerRepository(db.Context), new GraphRepository(db.Context), new SnapshotRepository(db.Context),
            discovery, runner, cveScan: cveService);

        // Read-only tool works immediately on a ReadOnly prod group without approval.
        var toolResult = await dispatcher.DispatchAsync("scan_cve",
            JsonSerializer.SerializeToElement(new { serverIds = new[] { server.Id } }), null, CancellationToken.None);

        Assert.False(toolResult.NeedsApproval);
        Assert.Contains("scanner=trivy", toolResult.Summary, StringComparison.Ordinal);
        Assert.Contains("CVE-2025-10001", toolResult.Summary, StringComparison.Ordinal);
        Assert.False(runner.LastUseSudo);

        var stored = await cveService.GetLatestAsync(server.Id, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(4, stored.TotalCount);
        Assert.Equal(Now, stored.ScannedAtUtc);
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
        public bool LastUseSudo { get; private set; }
        public Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options, CancellationToken cancellationToken)
        {
            LastUseSudo = options.UseSudo;
            return Task.FromResult(new RemoteCommandResult(0, stdout, string.Empty, TimeSpan.FromMilliseconds(20)));
        }
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
