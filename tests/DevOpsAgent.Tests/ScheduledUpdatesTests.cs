using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Agent;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Application.Monitoring;
using DevOpsAgent.Application.Notifications;
using DevOpsAgent.Application.Updates;
using DevOpsAgent.Domain.Monitoring;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Domain.Updates;
using DevOpsAgent.Infrastructure.Monitoring;
using DevOpsAgent.Infrastructure.Persistence;
using DevOpsAgent.Web.Hosted;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DevOpsAgent.Tests;

/// <summary>T9.1 / D-063: scheduled update scans and security-updates-available alert lifecycle.</summary>
public sealed class ScheduledUpdatesTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task UpdateScheduleServicePersistsIntervalAndValidatesBounds()
    {
        await using var db = await CreateDatabaseAsync();
        var service = new UpdateScheduleService(new AppSettingRepository(db.Context));

        Assert.Equal(24, await service.GetIntervalHoursAsync(CancellationToken.None));
        await service.SaveIntervalHoursAsync(12, CancellationToken.None);
        Assert.Equal(12, await service.GetIntervalHoursAsync(CancellationToken.None));
        await service.SaveIntervalHoursAsync(0, CancellationToken.None);
        Assert.Equal(0, await service.GetIntervalHoursAsync(CancellationToken.None));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SaveIntervalHoursAsync(-1, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SaveIntervalHoursAsync(169, CancellationToken.None));

        Assert.False(UpdateScheduleService.IsDue(null, 0, Start));
        Assert.True(UpdateScheduleService.IsDue(null, 24, Start));
        Assert.False(UpdateScheduleService.IsDue(Start, 24, Start.AddHours(23)));
        Assert.True(UpdateScheduleService.IsDue(Start, 24, Start.AddHours(24)));
    }

    [Fact]
    public async Task SecurityUpdatesAlertOpensHoldsOnFailedScanAndResolvesWhenCleared()
    {
        await using var db = await CreateDatabaseAsync();
        var server = new Server("srv-edge", "10.88.10.10", 22, "dva", "id_ed25519", "staging");
        db.Context.Servers.Add(server);
        await db.Context.SaveChangesAsync();

        var clock = new MutableTimeProvider(Start);
        var scans = new UpdateScanRepository(db.Context);
        var packages = new UpdatePackageRepository(db.Context);
        var notifications = new RecordingNotifications();

        var ssh = new HealthyStatusRunner();
        var settings = new AppSettingRepository(db.Context);
        var scanner = new AuthLogScanner(ssh, settings, Options.Create(new SecurityMonitoringOptions()), clock);
        var snapshots = new SnapshotRepository(db.Context);
        var source = new AlertSignalSource(
            new ServerRepository(db.Context),
            new MetricRepository(db.Context),
            new HealthCheckRepository(db.Context),
            snapshots,
            ssh,
            scanner,
            new DriftService(snapshots, new GraphRepository(db.Context)),
            new ContainerRestartMonitor(settings, clock, NullLogger<ContainerRestartMonitor>.Instance),
            clock,
            updateScans: scans,
            updatePackages: packages);
        var alerts = new AlertService(new AlertRepository(db.Context), source, clock, notifications, NullLogger<AlertService>.Instance);

        // Non-security package alone does not alert.
        await packages.ReplaceForServerAsync(server.Id, [new UpdatePackage(server.Id, "curl", "7.88", "7.89", "Debian", false, Start)], CancellationToken.None);
        await scans.SaveAsync(new UpdateScan(server.Id, "Apt", Start, null), CancellationToken.None);
        await alerts.EvaluateAsync(CancellationToken.None);
        Assert.DoesNotContain(await alerts.ListAsync(AlertState.Open, CancellationToken.None), item => item.Rule == AlertSignalSource.SecurityUpdatesRule);

        // Security package found -> opens Medium security-updates-available alert.
        clock.Advance(TimeSpan.FromMinutes(1));
        await packages.ReplaceForServerAsync(server.Id,
            [new UpdatePackage(server.Id, "openssl", "3.0.11", "3.0.15", "Debian-Security", true, clock.GetUtcNow())], CancellationToken.None);
        await scans.SaveAsync(new UpdateScan(server.Id, "Apt", clock.GetUtcNow(), null), CancellationToken.None);
        await alerts.EvaluateAsync(CancellationToken.None);
        var opened = Assert.Single(await alerts.ListAsync(AlertState.Open, CancellationToken.None), item => item.Rule == AlertSignalSource.SecurityUpdatesRule);
        Assert.Equal(AlertSeverity.Medium, opened.Severity);
        Assert.Contains("openssl", opened.Details, StringComparison.Ordinal);

        // Next scan fails (e.g. apt repo unreachable) -> open security alert is held, not resolved.
        clock.Advance(TimeSpan.FromHours(1));
        await scans.SaveAsync(new UpdateScan(server.Id, null, clock.GetUtcNow(), "repository unreachable"), CancellationToken.None);
        await alerts.EvaluateAsync(CancellationToken.None);
        Assert.Contains(await alerts.ListAsync(AlertState.Open, CancellationToken.None), item => item.Rule == AlertSignalSource.SecurityUpdatesRule);
        Assert.DoesNotContain(notifications.Resolved, item => item.Rule == AlertSignalSource.SecurityUpdatesRule);

        // Clean scan with 0 security packages -> alert resolves cleanly.
        clock.Advance(TimeSpan.FromHours(1));
        await packages.ReplaceForServerAsync(server.Id, [], CancellationToken.None);
        await scans.SaveAsync(new UpdateScan(server.Id, "Apt", clock.GetUtcNow(), null), CancellationToken.None);
        await alerts.EvaluateAsync(CancellationToken.None);
        Assert.DoesNotContain(await alerts.ListAsync(AlertState.Open, CancellationToken.None), item => item.Rule == AlertSignalSource.SecurityUpdatesRule);
        Assert.Contains(notifications.Resolved, item => item.Rule == AlertSignalSource.SecurityUpdatesRule);
    }

    [Fact]
    public async Task UpdateScanHostedServiceRunsOnlyWhenEnabledAndDue()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var clock = new MutableTimeProvider(Start);
        var collector = new CountingUpdatesCollector();

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseSqlite(connection));
        services.AddScoped<IServerRepository, ServerRepository>();
        services.AddScoped<IAppSettingRepository, AppSettingRepository>();
        services.AddScoped<IUpdatePackageRepository, UpdatePackageRepository>();
        services.AddScoped<IUpdateScanRepository, UpdateScanRepository>();
        services.AddSingleton<IUpdatesCollector>(collector);
        services.AddScoped<ExecutionModeService>();
        services.AddScoped<UpdatesService>();
        services.AddScoped<UpdateScheduleService>();
        services.AddSingleton<TimeProvider>(clock);
        services.AddLogging();
        await using var provider = services.BuildServiceProvider();

        await using (var setup = provider.CreateAsyncScope())
        {
            var db = setup.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.Servers.Add(new Server("srv-edge", "10.88.10.10", 22, "dva", "id_ed25519", "staging"));
            await db.SaveChangesAsync();
            await setup.ServiceProvider.GetRequiredService<UpdateScheduleService>().SaveIntervalHoursAsync(6, CancellationToken.None);
        }

        var hosted = new UpdateScanHostedService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            clock,
            new StubLifetime(),
            NullLogger<UpdateScanHostedService>.Instance);

        // First run (no prior scan): executes immediately.
        Assert.True(await hosted.RunOnceIfDueAsync(CancellationToken.None));
        Assert.Equal(1, collector.Calls);

        // 2 hours later: not due yet.
        clock.Advance(TimeSpan.FromHours(2));
        Assert.False(await hosted.RunOnceIfDueAsync(CancellationToken.None));
        Assert.Equal(1, collector.Calls);

        // 4 more hours (6 total): due again.
        clock.Advance(TimeSpan.FromHours(4));
        Assert.True(await hosted.RunOnceIfDueAsync(CancellationToken.None));
        Assert.Equal(2, collector.Calls);

        // Disabled (0 h): does not run even after a week.
        await using (var disableScope = provider.CreateAsyncScope())
        {
            await disableScope.ServiceProvider.GetRequiredService<UpdateScheduleService>().SaveIntervalHoursAsync(0, CancellationToken.None);
        }

        clock.Advance(TimeSpan.FromDays(7));
        Assert.False(await hosted.RunOnceIfDueAsync(CancellationToken.None));
        Assert.Equal(2, collector.Calls);
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

    private sealed class CountingUpdatesCollector : IUpdatesCollector
    {
        public int Calls { get; private set; }

        public Task<CollectedUpdateResult> CollectAsync(Server server, bool cacheOnly, CancellationToken cancellationToken)
        {
            Calls++;
            var json = JsonSerializer.Serialize(new[]
            {
                new UpdatePackageDto("openssl", "3.0.11", "3.0.15", "Debian-Security", true)
            });
            return Task.FromResult(new CollectedUpdateResult(PackageManagerKind.Apt, json, DateTimeOffset.UtcNow, cacheOnly));
        }
    }

    private sealed class HealthyStatusRunner : ISshRunner
    {
        public Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options, CancellationToken cancellationToken) =>
            Task.FromResult(command.Contains("__DVA_DOCKER__", StringComparison.Ordinal)
                ? new RemoteCommandResult(0, "__DVA_DOCKER__\n__DVA_SYSTEMD_FAILED__\n", string.Empty, TimeSpan.Zero)
                : new RemoteCommandResult(0, "__DVA_TZ__+00:00\n__DVA_BAN_TOOLS__\n__DVA_BAN_STATE__\n", string.Empty, TimeSpan.Zero));
    }

    private sealed class RecordingNotifications : IAlertNotificationService
    {
        public List<AlertDto> Opened { get; } = [];
        public List<AlertDto> Resolved { get; } = [];
        public Task NotifyOpenedAsync(AlertDto alert, CancellationToken cancellationToken) { Opened.Add(alert); return Task.CompletedTask; }
        public Task NotifyResolvedAsync(AlertDto alert, CancellationToken cancellationToken) { Resolved.Add(alert); return Task.CompletedTask; }
    }

    private sealed class MutableTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _now = initial;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }

    private sealed class StubLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private sealed class SqliteTestDatabase(SqliteConnection connection, AppDbContext context) : IAsyncDisposable
    {
        public AppDbContext Context { get; } = context;
        public async ValueTask DisposeAsync() { await Context.DisposeAsync(); await connection.DisposeAsync(); }
    }
}
