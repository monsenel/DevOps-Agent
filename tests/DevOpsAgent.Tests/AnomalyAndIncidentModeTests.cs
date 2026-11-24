using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Application.Monitoring;
using DevOpsAgent.Application.Notifications;
using DevOpsAgent.Domain.Monitoring;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Infrastructure.Monitoring;
using DevOpsAgent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevOpsAgent.Tests;

public sealed class AnomalyAndIncidentModeTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EvaluateMetricFlagsSustainedThreeSigmaAnomalyAndIgnoresSingleSpike()
    {
        double[] baseline = [20, 22, 19, 21, 20, 23, 18, 21, 20, 22, 19, 21];

        var singleSpike = AnomalyBaselineService.EvaluateMetric("cpu_percent", baseline, [21, 20, 95]);
        Assert.Null(singleSpike);

        var sustained = AnomalyBaselineService.EvaluateMetric("cpu_percent", baseline, [88, 91, 94]);
        Assert.NotNull(sustained);
        Assert.Equal("cpu_percent", sustained.Metric);
        Assert.True(sustained.ZScore > 3.0);

        // Insufficient baseline points must not alert.
        var tooFew = AnomalyBaselineService.EvaluateMetric("cpu_percent", [20, 21, 22], [90, 91, 92]);
        Assert.Null(tooFew);
    }

    [Fact]
    public async Task AnomalyBaselineServiceReadsRollupsAndSamplesAndIntegratesWithAlertSignalSource()
    {
        await using var db = await CreateDatabaseAsync();
        var server = new Server("srv-app", "10.0.2.10", 22, "dva", "id_ed25519", "prod");
        db.Context.Servers.Add(server);

        // 12 hourly rollups with normal cpu_percent around 20%
        for (var i = 1; i <= 12; i++)
        {
            var avg = 20.0 + (i % 3);
            db.Context.MetricRollups.Add(new MetricRollup(server.Id, "cpu_percent", Now.AddHours(-i - 1), avg - 1, avg, avg + 1, 60));
        }

        // 3 recent sustained high samples (>3 sigma)
        db.Context.MetricSamples.AddRange(
            new MetricSample(server.Id, "cpu_percent", 82.0, Now.AddMinutes(-3)),
            new MetricSample(server.Id, "cpu_percent", 85.0, Now.AddMinutes(-2)),
            new MetricSample(server.Id, "cpu_percent", 89.0, Now.AddMinutes(-1)));
        await db.Context.SaveChangesAsync();

        var metrics = new MetricRepository(db.Context);
        var anomalyService = new AnomalyBaselineService(metrics, new FixedTime(Now));

        var conditions = await anomalyService.EvaluateServerAsync(server.Id, server.Name, CancellationToken.None);
        var alert = Assert.Single(conditions);
        Assert.Equal(AnomalyBaselineService.AlertRule, alert.Rule);
        Assert.Equal(AlertSeverity.Medium, alert.Severity);
        Assert.Contains("cpu_percent", alert.Target, StringComparison.Ordinal);

        // Verify AlertSignalSource emits the metric-anomaly condition when samples are fresh.
        var settings = new AppSettingRepository(db.Context);
        var signalSource = new AlertSignalSource(
            new ServerRepository(db.Context),
            metrics,
            new HealthCheckRepository(db.Context),
            new SnapshotRepository(db.Context),
            new FixedSshRunner("__DVA_DOCKER__\n__DVA_SYSTEMD_FAILED__\n"),
            new EmptyAuthLogSource(),
            new DriftService(new SnapshotRepository(db.Context), new GraphRepository(db.Context)),
            new ContainerRestartMonitor(settings, new FixedTime(Now), NullLogger<ContainerRestartMonitor>.Instance),
            new FixedTime(Now),
            anomalyBaselines: anomalyService);

        var evaluated = await signalSource.EvaluateSignalsAsync(CancellationToken.None);
        Assert.Contains(evaluated.Conditions, c => c.Rule == AnomalyBaselineService.AlertRule && c.Severity == AlertSeverity.Medium);
    }

    [Fact]
    public async Task IncidentModePersistsPrioritizesAlertsAndSuppressesLowNotifications()
    {
        await using var db = await CreateDatabaseAsync();
        var server = new Server("srv-edge", "10.0.2.1", 22, "dva", "id_ed25519", "prod");
        db.Context.Servers.Add(server);
        await db.Context.SaveChangesAsync();

        var settings = new AppSettingRepository(db.Context);
        var incidentMode = new IncidentModeService(settings, new FixedTime(Now), NullLogger<IncidentModeService>.Instance);

        var initial = await incidentMode.GetAsync(CancellationToken.None);
        Assert.False(initial.Active);

        var active = await incidentMode.ActivateAsync("Database failover", "admin", CancellationToken.None);
        Assert.True(active.Active);
        Assert.Equal("Database failover", active.Reason);
        Assert.Equal("admin", active.ActivatedBy);
        Assert.Equal(Now, active.ActivatedAtUtc);

        // Prioritization puts Critical -> High -> Medium -> Low
        AlertDto[] alerts =
        [
            new(Guid.NewGuid(), "low-rule", AlertSeverity.Low, AlertState.Open, server.Id, "t", "Low", "d", Now, Now, null, null, 1),
            new(Guid.NewGuid(), "high-rule", AlertSeverity.High, AlertState.Open, server.Id, "t", "High", "d", Now, Now, null, null, 1),
            new(Guid.NewGuid(), "crit-rule", AlertSeverity.Critical, AlertState.Open, server.Id, "t", "Crit", "d", Now, Now, null, null, 1),
            new(Guid.NewGuid(), "med-rule", AlertSeverity.Medium, AlertState.Open, server.Id, "t", "Med", "d", Now, Now, null, null, 1)
        ];

        var ordered = IncidentModeService.PrioritizeAlerts(alerts, incidentModeActive: true);
        Assert.Equal([AlertSeverity.Critical, AlertSeverity.High, AlertSeverity.Medium, AlertSeverity.Low],
            ordered.Select(a => a.Severity).ToArray());

        // Low notifications are suppressed during active Incident Mode even when MinimumSeverity is Low; High still sends.
        var notifier = new RecordingNotifier();
        var notifications = new NotificationService(settings, new PlainProtector(), notifier, new ServerRepository(db.Context), incidentMode);
        await notifications.SaveAsync(new NotificationSettingsInput(true, "bot-token", "123", false, "https://ntfy.sh", "", "", AlertSeverity.Low), CancellationToken.None);

        await notifications.NotifyOpenedAsync(alerts[0], CancellationToken.None); // Low -> suppressed
        await notifications.NotifyOpenedAsync(alerts[1], CancellationToken.None); // High -> sent
        var sent = Assert.Single(notifier.Sent);
        Assert.Contains("[High]", sent, StringComparison.Ordinal);

        // Deactivating incident mode restores Low notifications.
        var ended = await incidentMode.DeactivateAsync(CancellationToken.None);
        Assert.False(ended.Active);
        await notifications.NotifyOpenedAsync(alerts[0], CancellationToken.None);
        Assert.Equal(2, notifier.Sent.Count);
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
            Task.FromResult(new RemoteCommandResult(0, stdout, string.Empty, TimeSpan.FromMilliseconds(5)));
    }

    private sealed class EmptyAuthLogSource : IAuthLogScanSource
    {
        public Task<IReadOnlyList<AlertCondition>> ScanAsync(Server server, CancellationToken cancellationToken, DateTimeOffset? deadlineUtc = null) =>
            Task.FromResult<IReadOnlyList<AlertCondition>>([]);
        public Task<IReadOnlyList<AlertCondition>> GetCachedAsync(Server server, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AlertCondition>>([]);
    }

    private sealed class RecordingNotifier : INotifier
    {
        public List<string> Sent { get; } = [];
        public Task SendAsync(NotificationEndpoint endpoint, string message, CancellationToken cancellationToken)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class PlainProtector : ISecretProtector
    {
        public string Protect(string plaintext) => "enc:" + plaintext;
        public string Unprotect(string ciphertext) => ciphertext.StartsWith("enc:", StringComparison.Ordinal) ? ciphertext[4..] : ciphertext;
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
