using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;
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
            scanner, new DriftService(new FakeSnapshots(), null!), TimeProvider.System);

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
            scanner, new DriftService(new FakeSnapshots(), null!), TimeProvider.System);

        var conditions = await source.GetActiveConditionsAsync(CancellationToken.None);

        var mismatch = Assert.Single(conditions, condition => condition.Rule == "ssh-host-key-mismatch");
        Assert.Equal(AlertSeverity.High, mismatch.Severity);
        Assert.Equal(server.Id, mismatch.ServerId);
        Assert.Contains("SHA256:abc123", mismatch.Details, StringComparison.Ordinal);
        Assert.DoesNotContain(conditions, condition => condition.Rule == "server-unreachable");
    }

    private sealed class MismatchSshRunner : ISshRunner
    {
        public Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options, CancellationToken cancellationToken) =>
            throw new SshHostKeyMismatchException("edge.test", 22, "SHA256:abc123");
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
