using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Agent;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Application.Monitoring;
using DevOpsAgent.Application.Security;
using DevOpsAgent.Domain.Monitoring;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Infrastructure.Monitoring;
using DevOpsAgent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevOpsAgent.Tests;

public sealed class FileIntegrityTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task FirstCheckInitializesBaselineAndSecondCheckDetectsDriftAndRaisesHighAlert()
    {
        await using var db = await CreateDatabaseAsync();
        var server = new Server("srv-edge", "10.0.1.5", 22, "dva", "id_ed25519", "prod");
        db.Context.Servers.Add(server);
        await db.Context.SaveChangesAsync();

        var baselineOut = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fim-baseline.txt"));
        var driftedOut = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fim-drifted.txt"));
        var runner = new SwitchableSshRunner(baselineOut);
        var settings = new AppSettingRepository(db.Context);
        var fim = new FileIntegrityService(new ServerRepository(db.Context), runner, settings, new FixedTime(T0), NullLogger<FileIntegrityService>.Instance);

        // 1. Initial baseline capture
        var first = await fim.CheckAsync(server.Id, CancellationToken.None);
        Assert.True(first.BaselineInitialized);
        Assert.False(first.HasDrift);
        Assert.Equal(5, first.CurrentFiles.Count);
        Assert.Empty(await fim.GetAlertConditionsAsync(CancellationToken.None));

        // 2. Matching check produces zero drift
        var clean = await fim.CheckAsync(server.Id, CancellationToken.None);
        Assert.False(clean.BaselineInitialized);
        Assert.False(clean.HasDrift);
        Assert.Empty(await fim.GetAlertConditionsAsync(CancellationToken.None));

        // 3. Drift detected across Modified, Removed, and Unreadable files
        runner.Output = driftedOut;
        var drifted = await fim.CheckAsync(server.Id, CancellationToken.None);
        Assert.True(drifted.HasDrift);
        Assert.Equal(3, drifted.Changes.Count);
        Assert.Contains(drifted.Changes, c => c.Path == "/etc/ssh/sshd_config" && c.ChangeType == "Modified");
        Assert.Contains(drifted.Changes, c => c.Path == "/etc/sudoers" && c.ChangeType == "Unreadable");
        Assert.Contains(drifted.Changes, c => c.Path == "/root/.ssh/authorized_keys" && c.ChangeType == "Removed");

        var alert = Assert.Single(await fim.GetAlertConditionsAsync(CancellationToken.None));
        Assert.Equal(FileIntegrityService.AlertRule, alert.Rule);
        Assert.Equal(AlertSeverity.High, alert.Severity);
        Assert.Equal(server.Id, alert.ServerId);
        Assert.Contains("/etc/ssh/sshd_config", alert.Target, StringComparison.Ordinal);

        // 4. AlertSignalSource consumes the FIM High alert condition
        var statusOutput = "__DVA_DOCKER__\n__DVA_SYSTEMD_FAILED__\n";
        var probeRunner = new SwitchableSshRunner(statusOutput);
        var signalSource = new AlertSignalSource(
            new ServerRepository(db.Context),
            new MetricRepository(db.Context),
            new HealthCheckRepository(db.Context),
            new SnapshotRepository(db.Context),
            probeRunner,
            new EmptyAuthLogSource(),
            new DriftService(new SnapshotRepository(db.Context), new GraphRepository(db.Context)),
            new ContainerRestartMonitor(settings, new FixedTime(T0), NullLogger<ContainerRestartMonitor>.Instance),
            new FixedTime(T0),
            fileIntegrity: fim);

        var evaluated = await signalSource.EvaluateSignalsAsync(CancellationToken.None);
        Assert.Contains(evaluated.Conditions, c => c.Rule == FileIntegrityService.AlertRule && c.Severity == AlertSeverity.High);

        // 5. Resetting baseline clears the drift alert
        var reset = await fim.ResetBaselineAsync(server.Id, CancellationToken.None);
        Assert.True(reset.BaselineInitialized);
        Assert.False(reset.HasDrift);
        Assert.Empty(await fim.GetAlertConditionsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TransientSshErrorPreservesDriftAlertAndErrorReportEmitsHeldScopeAndShutdownRethrows()
    {
        await using var db = await CreateDatabaseAsync();
        var server = new Server("srv-edge", "10.0.1.5", 22, "dva", "id_ed25519", "prod");
        db.Context.Servers.Add(server);
        await db.Context.SaveChangesAsync();

        var baselineOut = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fim-baseline.txt"));
        var driftedOut = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fim-drifted.txt"));
        var runner = new SwitchableSshRunner(baselineOut);
        var settings = new AppSettingRepository(db.Context);
        var fim = new FileIntegrityService(new ServerRepository(db.Context), runner, settings, new FixedTime(T0), NullLogger<FileIntegrityService>.Instance);

        await fim.CheckAsync(server.Id, CancellationToken.None);
        runner.Output = driftedOut;
        var drifted = await fim.CheckAsync(server.Id, CancellationToken.None);
        Assert.True(drifted.HasDrift);

        // Transient SSH error must NOT clear the open drift report or resolve the High alert.
        runner.ThrowException = new InvalidOperationException("connection reset");
        var failed = await fim.CheckAsync(server.Id, CancellationToken.None);
        Assert.NotNull(failed.Error);
        var stillOpen = await fim.GetAlertConditionForServerAsync(server.Id, server.Name, CancellationToken.None);
        Assert.NotNull(stillOpen);
        Assert.Equal(AlertSeverity.High, stillOpen.Severity);

        // Shutdown must rethrow without overwriting persisted state.
        runner.ThrowException = new SshShutdownException();
        await Assert.ThrowsAsync<SshShutdownException>(() => fim.CheckAsync(server.Id, CancellationToken.None));

        // When a server's only stored report is an error report, AlertSignalSource holds existing FIM alerts (T7.R20).
        var server2 = new Server("srv-hold", "10.0.1.6", 22, "dva", "id_ed25519", "prod");
        db.Context.Servers.Add(server2);
        await db.Context.SaveChangesAsync();
        runner.ThrowException = new InvalidOperationException("temporary ssh error");
        await fim.CheckAsync(server2.Id, CancellationToken.None);

        var signalSource = new AlertSignalSource(
            new ServerRepository(db.Context),
            new MetricRepository(db.Context),
            new HealthCheckRepository(db.Context),
            new SnapshotRepository(db.Context),
            new SwitchableSshRunner("__DVA_DOCKER__\n__DVA_SYSTEMD_FAILED__\n"),
            new EmptyAuthLogSource(),
            new DriftService(new SnapshotRepository(db.Context), new GraphRepository(db.Context)),
            new ContainerRestartMonitor(settings, new FixedTime(T0), NullLogger<ContainerRestartMonitor>.Instance),
            new FixedTime(T0),
            fileIntegrity: fim);

        var evaluated = await signalSource.EvaluateSignalsAsync(CancellationToken.None);
        Assert.Contains(evaluated.HeldScopes, s => s.ServerId == server2.Id && s.Rule == FileIntegrityService.AlertRule);
    }

    [Fact]
    public async Task CheckFileIntegrityToolDispatchesReadOnlyWithoutApproval()
    {
        await using var db = await CreateDatabaseAsync();
        var server = new Server("srv-prod", "10.0.1.9", 22, "dva", "id_ed25519", "prod");
        db.Context.Servers.Add(server);
        await db.Context.SaveChangesAsync();

        var baselineOut = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "fim-baseline.txt"));
        var runner = new SwitchableSshRunner(baselineOut);
        var settings = new AppSettingRepository(db.Context);
        var fim = new FileIntegrityService(new ServerRepository(db.Context), runner, settings, new FixedTime(T0), NullLogger<FileIntegrityService>.Instance);

        var registry = new ToolRegistry();
        var executionModes = new ExecutionModeService(settings, new ServerRepository(db.Context), NullLogger<ExecutionModeService>.Instance);
        var approvals = new ApprovalService(registry, executionModes, new ServerRepository(db.Context),
            new ActionRecordRepository(db.Context), TestBanGuard.Create(), new FixedTime(T0));
        var commandBuilder = new ToolCommandBuilder(registry);
        var execution = new ActionExecutionService(new ActionRecordRepository(db.Context), new ServerRepository(db.Context),
            registry, executionModes, commandBuilder, runner, TestBanGuard.Create(), new FixedTime(T0));
        var discovery = new DiscoveryService(new ServerRepository(db.Context), new SnapshotRepository(db.Context),
            new GraphRepository(db.Context), new GraphBuilder(), runner, [], new FixedTime(T0));
        var dispatcher = new AgentToolDispatcher(registry, approvals, execution, commandBuilder,
            new ServerRepository(db.Context), new GraphRepository(db.Context), new SnapshotRepository(db.Context),
            discovery, runner, fileIntegrity: fim);

        var result = await dispatcher.DispatchAsync("check_file_integrity",
            JsonSerializer.SerializeToElement(new { serverIds = new[] { server.Id } }), null, CancellationToken.None);

        Assert.False(result.NeedsApproval);
        Assert.Contains("baseline initialized", result.Summary, StringComparison.OrdinalIgnoreCase);
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

    private sealed class SwitchableSshRunner(string output) : ISshRunner
    {
        public string Output { get; set; } = output;
        public Exception? ThrowException { get; set; }
        public Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options, CancellationToken cancellationToken)
        {
            if (ThrowException is not null) throw ThrowException;
            return Task.FromResult(new RemoteCommandResult(0, Output, string.Empty, TimeSpan.FromMilliseconds(10)));
        }
    }

    private sealed class EmptyAuthLogSource : IAuthLogScanSource
    {
        public Task<IReadOnlyList<AlertCondition>> ScanAsync(Server server, CancellationToken cancellationToken, DateTimeOffset? deadlineUtc = null) =>
            Task.FromResult<IReadOnlyList<AlertCondition>>([]);
        public Task<IReadOnlyList<AlertCondition>> GetCachedAsync(Server server, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AlertCondition>>([]);
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
