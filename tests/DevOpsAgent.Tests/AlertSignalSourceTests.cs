using Microsoft.Extensions.Logging.Abstractions;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Application.Monitoring;
using DevOpsAgent.Domain.Discovery;
using DevOpsAgent.Domain.Monitoring;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Infrastructure.Monitoring;
using Microsoft.Extensions.Options;

namespace DevOpsAgent.Tests;

public sealed class AlertSignalSourceTests
{
    [Fact]
    public async Task UnreachableServersProduceDistinctAlertKeys()
    {
        var servers = new FakeServers(
            new Server("edge", "edge.test", 22, "dva", "test_key", "staging"),
            new Server("app", "app.test", 22, "dva", "test_key", "staging"));
        var ssh = new FakeSshRunner();
        var scanner = new AuthLogScanner(ssh, new FakeSettings(), Options.Create(new SecurityMonitoringOptions()), TimeProvider.System);
        var source = new AlertSignalSource(servers, new FakeMetrics(), new FakeHealthChecks(), new FakeSnapshots(), ssh,
            scanner, new DriftService(new FakeSnapshots(), null!),
            new ContainerRestartMonitor(new FakeSettings(), TimeProvider.System, NullLogger<ContainerRestartMonitor>.Instance), TimeProvider.System);

        var conditions = await source.GetActiveConditionsAsync(CancellationToken.None);
        var unreachable = conditions.Where(condition => condition.Rule == "server-unreachable").ToArray();

        Assert.Equal(2, unreachable.Length);
        Assert.Equal(2, unreachable.Select(condition => condition.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.All(unreachable, condition => Assert.Equal(condition.ServerId!.Value.ToString("N"), condition.Key["server-unreachable:".Length..]));
    }

    [Fact]
    public async Task HostKeyMismatchIsReportedDistinctlyFromUnreachable()
    {
        var server = new Server("edge", "edge.test", 22, "dva", "test_key", "staging");
        var ssh = new MismatchSshRunner();
        var scanner = new AuthLogScanner(ssh, new FakeSettings(), Options.Create(new SecurityMonitoringOptions()), TimeProvider.System);
        var source = new AlertSignalSource(new FakeServers(server), new FakeMetrics(), new FakeHealthChecks(), new FakeSnapshots(), ssh,
            scanner, new DriftService(new FakeSnapshots(), null!),
            new ContainerRestartMonitor(new FakeSettings(), TimeProvider.System, NullLogger<ContainerRestartMonitor>.Instance), TimeProvider.System);

        var conditions = await source.GetActiveConditionsAsync(CancellationToken.None);

        var mismatch = Assert.Single(conditions, condition => condition.Rule == "ssh-host-key-mismatch");
        Assert.Equal(AlertSeverity.Critical, mismatch.Severity);
        Assert.Equal(server.Id, mismatch.ServerId);
        Assert.Contains("SHA256:abc123", mismatch.Details, StringComparison.Ordinal);
        Assert.DoesNotContain(conditions, condition => condition.Rule == "server-unreachable");
    }

    [Fact]
    public async Task JumpHostKeyMismatchIsOnlyReportedForTheJumpHostItself()
    {
        var gateway = new Server("gateway", "gateway.test", 22, "dva", "test_key", "staging");
        var behind1 = new Server("app-1", "app-1.test", 22, "dva", "test_key", "staging");
        var behind2 = new Server("app-2", "app-2.test", 22, "dva", "test_key", "staging");
        // Every connection attempt fails on the gateway's key, exactly as it does for servers routed through it.
        var ssh = new MismatchSshRunner("gateway.test");
        var scanner = new AuthLogScanner(ssh, new FakeSettings(), Options.Create(new SecurityMonitoringOptions()), TimeProvider.System);
        var source = new AlertSignalSource(new FakeServers(gateway, behind1, behind2), new FakeMetrics(), new FakeHealthChecks(),
            new FakeSnapshots(), ssh, scanner, new DriftService(new FakeSnapshots(), null!),
            new ContainerRestartMonitor(new FakeSettings(), TimeProvider.System, NullLogger<ContainerRestartMonitor>.Instance), TimeProvider.System);

        var conditions = await source.GetActiveConditionsAsync(CancellationToken.None);

        var mismatch = Assert.Single(conditions, condition => condition.Rule == "ssh-host-key-mismatch");
        Assert.Equal(gateway.Id, mismatch.ServerId);
        var unreachable = conditions.Where(condition => condition.Rule == "server-unreachable").ToArray();
        Assert.Equal(new[] { behind1.Id, behind2.Id }.Order(), unreachable.Select(condition => condition.ServerId!.Value).Order());
        Assert.All(unreachable, condition => Assert.Contains("gateway.test:22", condition.Details, StringComparison.Ordinal));
    }

    [Fact]
    public async Task RejectedSudoPassword_OpensOneMediumSudoAuthFailedAlert_UntilSuccessOrRemoval()
    {
        var db = new Server("db", "db.test", 22, "dvapw", "test_key", "staging");
        var other = new Server("app", "app.test", 22, "dva", "test_key", "staging");
        db.SetSudoPassword("protected-secret-token");
        other.SetSudoPassword("protected-other");
        var source = Source(db, other);
        Assert.DoesNotContain(await source.GetActiveConditionsAsync(CancellationToken.None), c => c.Rule == "sudo-auth-failed");

        db.RecordSudoPasswordRejected(new DateTimeOffset(2026, 11, 16, 8, 0, 0, TimeSpan.Zero));
        var paused = Assert.Single(await source.GetActiveConditionsAsync(CancellationToken.None), c => c.Rule == "sudo-auth-failed");
        Assert.Equal(AlertSignalSource.SudoAuthFailedRule, paused.Rule);
        Assert.Equal(AlertSeverity.Medium, paused.Severity);
        Assert.Equal(db.Id, paused.ServerId);
        Assert.Equal($"sudo-auth-failed:{db.Id:N}", paused.Key);
        Assert.Equal("db: sudo password rejected", paused.Title);
        Assert.DoesNotContain("protected-secret-token", paused.Details, StringComparison.Ordinal);

        // An edit lifts the pause but is no proof that sudo works: the alert stays open (same key, so no new notification).
        db.SetSudoPassword("protected-secret-token-2");
        var edited = Assert.Single(await source.GetActiveConditionsAsync(CancellationToken.None), c => c.Rule == "sudo-auth-failed");
        Assert.Equal(paused.Key, edited.Key);
        Assert.Contains("edited", edited.Details, StringComparison.Ordinal);

        db.RecordSudoPasswordSucceeded();
        Assert.DoesNotContain(await source.GetActiveConditionsAsync(CancellationToken.None), c => c.Rule == "sudo-auth-failed");

        db.RecordSudoPasswordRejected(DateTimeOffset.UnixEpoch);
        Assert.Single(await source.GetActiveConditionsAsync(CancellationToken.None), c => c.Rule == "sudo-auth-failed");
        db.ClearSudoPassword();
        Assert.DoesNotContain(await source.GetActiveConditionsAsync(CancellationToken.None), c => c.Rule == "sudo-auth-failed");
    }

    [Fact]
    public async Task UnknownSudoPasswordOutcome_OpensTheSameMediumAlert_UntilAConfirmedSudo()
    {
        var db = new Server("db", "db.test", 22, "dvapw", "test_key", "staging");
        db.SetSudoPassword("protected-secret-token");
        var source = Source(db);

        db.RecordSudoPasswordOutcomeUnknown(new DateTimeOffset(2026, 11, 16, 8, 0, 0, TimeSpan.Zero));
        var unknown = Assert.Single(await source.GetActiveConditionsAsync(CancellationToken.None), c => c.Rule == "sudo-auth-failed");
        Assert.Equal(AlertSeverity.Medium, unknown.Severity);
        Assert.Equal($"sudo-auth-failed:{db.Id:N}", unknown.Key);
        Assert.Equal("db: sudo password outcome unknown, paused", unknown.Title);
        Assert.Contains("Retry now", unknown.Details, StringComparison.Ordinal);
        Assert.DoesNotContain("protected-secret-token", unknown.Details, StringComparison.Ordinal);

        // Retry now lifts the pause; the alert stays until the marker confirms the password.
        db.ClearSudoPasswordOutcomeUnknown();
        Assert.Single(await source.GetActiveConditionsAsync(CancellationToken.None), c => c.Rule == "sudo-auth-failed");
        db.RecordSudoPasswordSucceeded();
        Assert.DoesNotContain(await source.GetActiveConditionsAsync(CancellationToken.None), c => c.Rule == "sudo-auth-failed");
    }

    [Fact]
    public void SudoAuthFailedNotification_CarriesOnlyServerRuleSeverityAndState()
    {
        Assert.Equal("[Medium] db · sudo-auth-failed · opened",
            DevOpsAgent.Application.Notifications.NotificationService.FormatAlertMessage("db", AlertSignalSource.SudoAuthFailedRule, AlertSeverity.Medium, resolved: false));
        Assert.Equal("[Medium] db · sudo-auth-failed · resolved",
            DevOpsAgent.Application.Notifications.NotificationService.FormatAlertMessage("db", AlertSignalSource.SudoAuthFailedRule, AlertSeverity.Medium, resolved: true));
    }

    private static AlertSignalSource Source(params Server[] servers)
    {
        var ssh = new FakeSshRunner();
        var scanner = new AuthLogScanner(ssh, new FakeSettings(), Options.Create(new SecurityMonitoringOptions()), TimeProvider.System);
        return new AlertSignalSource(new FakeServers(servers), new FakeMetrics(), new FakeHealthChecks(), new FakeSnapshots(), ssh,
            scanner, new DriftService(new FakeSnapshots(), null!),
            new ContainerRestartMonitor(new FakeSettings(), TimeProvider.System, NullLogger<ContainerRestartMonitor>.Instance), TimeProvider.System);
    }

    private sealed class MismatchSshRunner(string mismatchedHost = "edge.test") : ISshRunner
    {
        public Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options, CancellationToken cancellationToken) =>
            throw new SshHostKeyMismatchException(mismatchedHost, 22, "SHA256:abc123");
    }

    private sealed class FakeServers(params Server[] items) : IServerRepository
    {
        public Task<IReadOnlyList<Server>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Server>>(items);
        public Task<Server?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(items.FirstOrDefault(item => item.Id == id));
        public Task AddAsync(Server server, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SaveAsync(Server server, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteAsync(Server server, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeSshRunner : ISshRunner
    {
        public Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options, CancellationToken cancellationToken) =>
            Task.FromResult(new RemoteCommandResult(1, string.Empty, "unreachable", TimeSpan.Zero));
    }

    private sealed class FakeSettings : IAppSettingRepository
    {
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public Task SetAsync(string key, string value, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeMetrics : IMetricRepository
    {
        public Task AddSamplesAsync(IReadOnlyCollection<MetricSample> samples, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<MetricSample>> GetRecentAsync(Guid serverId, DateTimeOffset sinceUtc, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MetricSample>>([]);
        public Task<IReadOnlyList<MetricSample>> GetLatestAsync(Guid serverId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MetricSample>>([]);
        public Task<IReadOnlyList<MetricSample>> GetUnrolledAsync(DateTimeOffset beforeUtc, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MetricSample>>([]);
        public Task SaveRollupsAsync(IReadOnlyCollection<MetricRollup> rollups, DateTimeOffset rawPruneBeforeUtc, DateTimeOffset rollupPruneBeforeUtc, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeHealthChecks : IHealthCheckRepository
    {
        public Task<IReadOnlyList<HealthCheck>> ListAsync(Guid serverId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<HealthCheck>>([]);
        public Task<IReadOnlyList<HealthCheck>> ListEnabledAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<HealthCheck>>([]);
        public Task<HealthCheck?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<HealthCheck?>(null);
        public Task AddAsync(HealthCheck healthCheck, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SaveAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteAsync(HealthCheck healthCheck, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeSnapshots : ISnapshotRepository
    {
        public Task<Snapshot?> GetLatestAsync(CancellationToken cancellationToken) => Task.FromResult<Snapshot?>(null);
        public Task<Snapshot?> GetLatestRunAsync(CancellationToken cancellationToken) => Task.FromResult<Snapshot?>(null);
        public Task<IReadOnlyList<Snapshot>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Snapshot>>([]);
        public Task<Snapshot> CreateAsync(IReadOnlyCollection<Guid> serverIds, DateTimeOffset startedAtUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CompleteAsync(Snapshot snapshot, IReadOnlyCollection<CollectorRun> collectorRuns, DateTimeOffset finishedAtUtc, int succeededServerCount, int failedServerCount, bool cancelled, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task FailAsync(Snapshot snapshot, IReadOnlyCollection<CollectorRun> collectorRuns, DateTimeOffset finishedAtUtc, int succeededServerCount, int failedServerCount, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
