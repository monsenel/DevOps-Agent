using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Application.Monitoring;
using DevOpsAgent.Application.Notifications;
using DevOpsAgent.Domain.Discovery;
using DevOpsAgent.Domain.Monitoring;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Infrastructure.Monitoring;
using DevOpsAgent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DevOpsAgent.Tests;

/// <summary>T7.R20 / D-061: open alerts must not resolve from outages, truncated probes, or stale data.</summary>
public sealed class StaleAlertHoldTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 23, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("unreachable")]
    [InlineData("auth-failed")]
    [InlineData("truncated")]
    public async Task ProbeDrivenAlertsStayOpenDuringOutageAndResolveOnFirstFreshHealthyProbe(string outageKind)
    {
        await using var db = await CreateDatabaseAsync();
        var server = new Server("srv-app", "10.88.10.20", 22, "dva", "id_ed25519", "staging");
        db.Context.Servers.Add(server);
        await db.Context.SaveChangesAsync();

        var clock = new MutableTimeProvider(Start);
        var ssh = new SwitchableSshRunner();
        ssh.NextStatusResult = () => new RemoteCommandResult(0,
            "__DVA_DOCKER__\nweb\texited\n__DVA_SYSTEMD_FAILED__\nworker.service loaded failed failed Worker\n",
            string.Empty, TimeSpan.FromMilliseconds(20));

        var notifications = new RecordingNotifications();
        var source = CreateSource(db.Context, ssh, clock);
        var alertService = new AlertService(new AlertRepository(db.Context), source, clock, notifications, NullLogger<AlertService>.Instance);

        await alertService.EvaluateAsync(CancellationToken.None);
        var open = await alertService.ListAsync(AlertState.Open, CancellationToken.None);
        Assert.Contains(open, item => item.Rule == "container-state" && item.Target == "web");
        Assert.Contains(open, item => item.Rule == "unit-failed" && item.Target == "worker.service");

        // Now the server becomes unreachable, auth-failed, or returns a truncated exit-0 probe.
        clock.Advance(TimeSpan.FromMinutes(10));
        ssh.NextStatusResult = outageKind switch
        {
            "unreachable" => () => throw new HostUnreachableException(server.Id, server.Name,
                new HostReachabilityState(server.Id, Start, Start.AddMinutes(20), 2, "could not connect", false, null)),
            "auth-failed" => () => throw new HostUnreachableException(server.Id, server.Name,
                new HostReachabilityState(server.Id, Start, Start.AddMinutes(20), 2, "authentication failed", false, null, HostFailureKind.AuthFailed)),
            _ => () => new RemoteCommandResult(0, "partial output with no markers", string.Empty, TimeSpan.FromMilliseconds(10))
        };

        await alertService.EvaluateAsync(CancellationToken.None);
        var stillOpen = await alertService.ListAsync(AlertState.Open, CancellationToken.None);
        Assert.Contains(stillOpen, item => item.Rule == "container-state" && item.Target == "web");
        Assert.Contains(stillOpen, item => item.Rule == "unit-failed" && item.Target == "worker.service");
        Assert.DoesNotContain(notifications.Resolved, item => item.Rule is "container-state" or "unit-failed");

        // First fresh healthy probe: the held alerts resolve cleanly.
        clock.Advance(TimeSpan.FromMinutes(2));
        ssh.NextStatusResult = () => new RemoteCommandResult(0,
            "__DVA_DOCKER__\nweb\trunning\n__DVA_SYSTEMD_FAILED__\n", string.Empty, TimeSpan.FromMilliseconds(15));

        await alertService.EvaluateAsync(CancellationToken.None);
        var afterRecovery = await alertService.ListAsync(AlertState.Open, CancellationToken.None);
        Assert.DoesNotContain(afterRecovery, item => item.Rule == "container-state");
        Assert.DoesNotContain(afterRecovery, item => item.Rule == "unit-failed");
        Assert.Contains(notifications.Resolved, item => item.Rule == "container-state");
        Assert.Contains(notifications.Resolved, item => item.Rule == "unit-failed");
    }

    [Fact]
    public async Task HeldAlertsExpireAfterMaxHoldDurationSoRetiredHostsDoNotHoldForever()
    {
        await using var db = await CreateDatabaseAsync();
        var server = new Server("srv-old", "10.88.10.21", 22, "dva", "id_ed25519", "staging");
        db.Context.Servers.Add(server);
        await db.Context.SaveChangesAsync();

        var clock = new MutableTimeProvider(Start);
        var ssh = new SwitchableSshRunner();
        ssh.NextStatusResult = () => new RemoteCommandResult(0,
            "__DVA_DOCKER__\n__DVA_DOCKER_DENIED__\n__DVA_SYSTEMD_FAILED__\n", string.Empty, TimeSpan.FromMilliseconds(10));

        var source = CreateSource(db.Context, ssh, clock);
        var alertService = new AlertService(new AlertRepository(db.Context), source, clock, new RecordingNotifications(), NullLogger<AlertService>.Instance);

        await alertService.EvaluateAsync(CancellationToken.None);
        Assert.Contains(await alertService.ListAsync(AlertState.Open, CancellationToken.None), item => item.Rule == "docker-status-unavailable");

        // Unreachable for 23 hours -> still held.
        ssh.NextStatusResult = () => throw new HostUnreachableException(server.Id, server.Name,
            new HostReachabilityState(server.Id, Start, Start.AddHours(25), 5, "could not connect", false, null));
        clock.Advance(TimeSpan.FromHours(23));
        await alertService.EvaluateAsync(CancellationToken.None);
        Assert.Contains(await alertService.ListAsync(AlertState.Open, CancellationToken.None), item => item.Rule == "docker-status-unavailable");

        // Unreachable past 24 hours -> expires cleanly.
        clock.Advance(TimeSpan.FromHours(2));
        await alertService.EvaluateAsync(CancellationToken.None);
        Assert.DoesNotContain(await alertService.ListAsync(AlertState.Open, CancellationToken.None), item => item.Rule == "docker-status-unavailable");
    }

    [Fact]
    public async Task ResourceThresholdsEvaluateWithoutProbeAndHoldWhenSamplesAreStale()
    {
        await using var db = await CreateDatabaseAsync();
        var server = new Server("srv-disk", "10.88.10.22", 22, "dva", "id_ed25519", "staging");
        db.Context.Servers.Add(server);
        db.Context.MetricSamples.Add(new MetricSample(server.Id, "disk_percent", 95.0, Start));
        await db.Context.SaveChangesAsync();

        var clock = new MutableTimeProvider(Start.AddSeconds(30));
        var ssh = new SwitchableSshRunner();
        // SSH status probe fails, yet fresh stored metrics still open the disk threshold alert.
        ssh.NextStatusResult = () => throw new HostUnreachableException(server.Id, server.Name,
            new HostReachabilityState(server.Id, Start, Start.AddMinutes(5), 1, "could not connect", false, null));

        var source = CreateSource(db.Context, ssh, clock);
        var alertService = new AlertService(new AlertRepository(db.Context), source, clock, new RecordingNotifications(), NullLogger<AlertService>.Instance);

        await alertService.EvaluateAsync(CancellationToken.None);
        Assert.Contains(await alertService.ListAsync(AlertState.Open, CancellationToken.None), item => item.Rule == "threshold-disk");

        // 15 minutes later without new metric samples: the disk alert stays held instead of resolving.
        clock.Advance(TimeSpan.FromMinutes(15));
        await alertService.EvaluateAsync(CancellationToken.None);
        Assert.Contains(await alertService.ListAsync(AlertState.Open, CancellationToken.None), item => item.Rule == "threshold-disk");

        // Fresh healthy metric sample arrives -> disk alert resolves.
        db.Context.MetricSamples.Add(new MetricSample(server.Id, "disk_percent", 42.0, clock.GetUtcNow()));
        await db.Context.SaveChangesAsync();
        await alertService.EvaluateAsync(CancellationToken.None);
        Assert.DoesNotContain(await alertService.ListAsync(AlertState.Open, CancellationToken.None), item => item.Rule == "threshold-disk");
    }

    [Fact]
    public async Task AuthLogScannerFailurePathsHoldSecurityAlertsInsteadOfAgingByWallClock()
    {
        await using var db = await CreateDatabaseAsync();
        var server = new Server("srv-sec", "10.88.10.23", 22, "dva", "id_ed25519", "staging");
        db.Context.Servers.Add(server);
        await db.Context.SaveChangesAsync();

        var clock = new MutableTimeProvider(Start);
        var ssh = new SwitchableSshRunner();
        var settings = new AppSettingRepository(db.Context);
        var scanner = new AuthLogScanner(ssh, settings, Options.Create(new SecurityMonitoringOptions()), clock);

        // First scan observes an unauthorized sudo event -> opens ssh-unusual-sudo alert.
        ssh.NextAuthResult = () => new RemoteCommandResult(0,
            "__DVA_TZ__+00:00\n"
            + "2026-11-23T07:59:50+00:00 srv-sec sudo[101]:   eve : TTY=pts/0 ; PWD=/tmp ; USER=root ; COMMAND=/bin/sh\n"
            + "__DVA_BAN_TOOLS__\n__DVA_BAN_STATE__\n",
            string.Empty, TimeSpan.FromMilliseconds(10));
        var initial = await scanner.ScanAsync(server, CancellationToken.None);
        Assert.Contains(initial, item => item.Rule == "ssh-unusual-sudo");

        // 20 minutes pass (> 5 min window), and the next scan fails with non-zero exit / journal error / exception.
        clock.Advance(TimeSpan.FromMinutes(20));
        ssh.NextAuthResult = () => new RemoteCommandResult(1, string.Empty, "permission denied", TimeSpan.FromMilliseconds(5));
        var onNonZero = await scanner.ScanAsync(server, CancellationToken.None);
        Assert.Contains(onNonZero, item => item.Rule == "ssh-unusual-sudo");

        ssh.NextAuthResult = () => new RemoteCommandResult(0,
            "__DVA_JOURNAL_ERROR__\n__DVA_BAN_TOOLS__\n__DVA_BAN_STATE__\n", string.Empty, TimeSpan.FromMilliseconds(5));
        var onJournalError = await scanner.ScanAsync(server, CancellationToken.None);
        Assert.Contains(onJournalError, item => item.Rule == "ssh-unusual-sudo");

        ssh.NextAuthResult = () => throw new InvalidOperationException("transient ssh error");
        var onException = await scanner.ScanAsync(server, CancellationToken.None);
        Assert.Contains(onException, item => item.Rule == "ssh-unusual-sudo");
    }

    [Fact]
    public void ContainerRestartIncreaseAcrossUnsampledGapDoesNotOpenFreshLoopAlert()
    {
        var history = new[]
        {
            new ContainerCycle(Start, [new ContainerStatus("cid1", "api", "running", 0)]),
            // Server/app was down for 3 hours; container restarted 4 times over those 3 hours, now running cleanly.
            new ContainerCycle(Start.AddHours(3), [new ContainerStatus("cid1", "api", "running", 4)]),
        };

        var loops = ContainerRestartMonitor.Evaluate(history, Start.AddHours(3));
        Assert.Empty(loops);
    }

    [Fact]
    public void RemoteServiceStatusParserMarksOutputWithoutBothMarkersIncomplete()
    {
        Assert.False(RemoteServiceStatusParser.Parse(string.Empty).IsComplete);
        Assert.False(RemoteServiceStatusParser.Parse("__DVA_DOCKER__\nweb\trunning\n").IsComplete);
        Assert.False(RemoteServiceStatusParser.Parse("__DVA_SYSTEMD_FAILED__\n").IsComplete);
        Assert.True(RemoteServiceStatusParser.Parse("__DVA_DOCKER__\nweb\trunning\n__DVA_SYSTEMD_FAILED__\n").IsComplete);
    }

    private static AlertSignalSource CreateSource(AppDbContext db, ISshRunner ssh, TimeProvider clock)
    {
        var settings = new AppSettingRepository(db);
        var scanner = new AuthLogScanner(ssh, settings, Options.Create(new SecurityMonitoringOptions()), clock);
        var snapshots = new SnapshotRepository(db);
        return new AlertSignalSource(
            new ServerRepository(db),
            new MetricRepository(db),
            new HealthCheckRepository(db),
            snapshots,
            ssh,
            scanner,
            new DriftService(snapshots, new GraphRepository(db)),
            new ContainerRestartMonitor(settings, clock, NullLogger<ContainerRestartMonitor>.Instance),
            clock);
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

    private sealed class SwitchableSshRunner : ISshRunner
    {
        public Func<RemoteCommandResult>? NextStatusResult { get; set; }
        public Func<RemoteCommandResult>? NextAuthResult { get; set; }

        public Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options, CancellationToken cancellationToken)
        {
            if (command.Contains("__DVA_DOCKER__", StringComparison.Ordinal))
            {
                return Task.FromResult(NextStatusResult is not null
                    ? NextStatusResult()
                    : new RemoteCommandResult(0, "__DVA_DOCKER__\n__DVA_SYSTEMD_FAILED__\n", string.Empty, TimeSpan.Zero));
            }

            if (NextAuthResult is not null)
            {
                return Task.FromResult(NextAuthResult());
            }

            return Task.FromResult(new RemoteCommandResult(0, "__DVA_TZ__+00:00\n__DVA_BAN_TOOLS__\n__DVA_BAN_STATE__\n", string.Empty, TimeSpan.Zero));
        }
    }

    private sealed class RecordingNotifications : IAlertNotificationService
    {
        public List<AlertDto> Opened { get; } = [];
        public List<AlertDto> Resolved { get; } = [];

        public Task NotifyOpenedAsync(AlertDto alert, CancellationToken cancellationToken)
        {
            Opened.Add(alert);
            return Task.CompletedTask;
        }

        public Task NotifyResolvedAsync(AlertDto alert, CancellationToken cancellationToken)
        {
            Resolved.Add(alert);
            return Task.CompletedTask;
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _now = initial;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
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
