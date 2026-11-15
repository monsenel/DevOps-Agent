using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Application.Monitoring;
using DevOpsAgent.Domain.Discovery;
using DevOpsAgent.Domain.Monitoring;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Infrastructure.Monitoring;
using DevOpsAgent.Infrastructure.Ssh;
using Microsoft.Extensions.Options;
using Renci.SshNet.Common;

namespace DevOpsAgent.Tests;

/// <summary>
/// T7.R17: metric and alert cycles end within their deadline when a host hangs, the other hosts' results are kept, a call's
/// deadline includes connect time, and the auth-log scan is skipped for a host whose status probe failed.
/// </summary>
public sealed class MonitoringCycleDeadlineTests
{
    [Fact]
    public async Task AHungHostEndsTheMetricCycleAtItsDeadlineAndTheOtherHostsAreStored()
    {
        using var lab = new HangLab();
        var hung = lab.Add("hung", "10.0.0.9", script => script.Commands = CommandMode.HangIgnoringToken);
        var healthy = Enumerable.Range(1, 3).Select(index => lab.Add($"srv-{index}", $"10.0.1.{index}")).ToArray();
        var metrics = new HangMetrics();
        // A command timeout far beyond the cycle deadline: only the deadline can end the cycle in time.
        var options = new MonitoringCycleOptions { MetricCommandTimeout = TimeSpan.FromSeconds(20), MetricCycleDeadline = TimeSpan.FromMilliseconds(400) };
        var service = new MetricService(lab.Servers, metrics, lab.Runner,
            new ContainerRestartMonitor(new HangSettings(), lab.Time, NullLogger<ContainerRestartMonitor>.Instance), lab.Time, options);

        var stopwatch = Stopwatch.StartNew();
        // The hung host's failure is still surfaced to the poller, after everything else was stored.
        await Assert.ThrowsAsync<TimeoutException>(() => service.PollAsync(CancellationToken.None));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"cycle took {stopwatch.ElapsedMilliseconds} ms");
        Assert.All(healthy, server => Assert.Equal(1, metrics.CyclesStored(server.Id)));
        Assert.Equal(0, metrics.CyclesStored(hung.Id));
        Assert.Equal(0, lab.Pool.ActiveLeases);
    }

    [Fact]
    public async Task AHostWhoseCommandsTimeOutIsSkippedByTheMetricCycleAfterFourTimeouts()
    {
        // The commands' timeout is what is being counted here, so connecting gets headroom beyond it (see HangLab.ConnectHeadroom).
        using var lab = new HangLab(connectHeadroom: HangLab.ConnectHeadroom);
        var hung = lab.Add("hung", "10.0.0.9", script => script.Commands = CommandMode.HangHonouringToken);
        var healthy = lab.Add("srv", "10.0.1.1");
        var metrics = new HangMetrics();
        var options = new MonitoringCycleOptions { MetricCommandTimeout = TimeSpan.FromMilliseconds(400), MetricCycleDeadline = TimeSpan.FromSeconds(10) };
        var service = new MetricService(lab.Servers, metrics, lab.Runner,
            new ContainerRestartMonitor(new HangSettings(), lab.Time, NullLogger<ContainerRestartMonitor>.Instance), lab.Time, options);

        var durations = new List<TimeSpan>();
        for (var cycle = 1; cycle <= IHostReachability.CommandTimeoutThreshold; cycle++)
        {
            var stopwatch = Stopwatch.StartNew();
            await Assert.ThrowsAsync<SshOperationTimeoutException>(() => service.PollAsync(CancellationToken.None));
            durations.Add(stopwatch.Elapsed);
        }

        // Each of the first four cycles waited for the hung host's command timeout (one timeout per cycle) ...
        Assert.All(durations, duration => Assert.True(duration >= TimeSpan.FromMilliseconds(350), $"{duration.TotalMilliseconds:0} ms"));
        Assert.Equal(HostFailureKind.CommandTimeout, lab.Reachability.Get(hung.Id)!.Kind);

        // ... the fifth skips it without waiting or sending anything, and is no failure of the poller.
        var commands = lab.Transports.Commands(hung);
        var fifth = Stopwatch.StartNew();
        await service.PollAsync(CancellationToken.None);
        Assert.True(fifth.Elapsed < TimeSpan.FromMilliseconds(300), $"fifth cycle took {fifth.ElapsedMilliseconds} ms");
        Assert.Equal(commands, lab.Transports.Commands(hung));
        Assert.Equal(5, metrics.CyclesStored(healthy.Id));
    }

    [Fact]
    public async Task TheContainerQueryHasItsOwnShortBudgetAndKeepsTheMetricResult()
    {
        // The metric command answers, the container query (a different command on the same host) hangs.
        var runner = new SelectiveRunner(command => command == MetricsParser.CollectionCommand ? Behaviour.Answer : Behaviour.HangUntilCancelled);
        var server = new Server("docker-host", "10.0.2.1", 22, "dva", "key", "staging");
        var metrics = new HangMetrics();
        var options = new MonitoringCycleOptions
        {
            MetricCommandTimeout = TimeSpan.FromSeconds(30),
            MetricCycleDeadline = TimeSpan.FromSeconds(30),
            ContainerQueryTimeout = TimeSpan.FromMilliseconds(300)
        };
        var service = new MetricService(new HangServers([server]), metrics, runner,
            new ContainerRestartMonitor(new HangSettings(), TimeProvider.System, NullLogger<ContainerRestartMonitor>.Instance), TimeProvider.System, options);

        var stopwatch = Stopwatch.StartNew();
        await service.PollAsync(CancellationToken.None);

        // 20 s used to be added here; now the side query is cut off after its own budget and the metrics are stored.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"took {stopwatch.ElapsedMilliseconds} ms");
        Assert.Equal(1, metrics.CyclesStored(server.Id));
    }

    [Fact]
    public async Task AHungHostEndsTheAlertCycleAtItsDeadlineAndTheOthersAreEvaluated()
    {
        using var lab = new HangLab();
        var hung = lab.Add("hung", "10.0.0.9", script => script.Commands = CommandMode.HangIgnoringToken);
        var healthy = lab.Add("srv", "10.0.1.1");
        var options = new MonitoringCycleOptions { AlertProbeTimeout = TimeSpan.FromSeconds(20), AlertCycleDeadline = TimeSpan.FromMilliseconds(400) };
        var source = AlertSource(lab.Servers, lab.Runner, new AuthLogScanner(lab.Runner, new HangSettings(),
            Options.Create(new SecurityMonitoringOptions()), TimeProvider.System), options);

        var stopwatch = Stopwatch.StartNew();
        var conditions = await source.GetActiveConditionsAsync(CancellationToken.None);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"cycle took {stopwatch.ElapsedMilliseconds} ms");
        var unreachable = Assert.Single(conditions, condition => condition.Rule == "server-unreachable");
        Assert.Equal(hung.Id, unreachable.ServerId);
        Assert.Contains("probe failed", unreachable.Details, StringComparison.Ordinal);
        Assert.DoesNotContain("could not reach", unreachable.Details, StringComparison.Ordinal);
        Assert.DoesNotContain(conditions, condition => condition.ServerId == healthy.Id && condition.Rule == "server-unreachable");
    }

    [Fact]
    public async Task TheAuthLogScanDoesNotRunWhenTheStatusProbeFailedAndRunsWhenItSucceeded()
    {
        var failing = new Server("down", "10.0.3.1", 22, "dva", "key", "staging");
        var healthy = new Server("up", "10.0.3.2", 22, "dva", "key", "staging");
        var runner = new ProbeRunner(failing.Id);
        var scanner = new AuthLogScanner(runner, new HangSettings(), Options.Create(new SecurityMonitoringOptions()), TimeProvider.System);
        var source = AlertSource(new HangServers([failing, healthy]), runner, scanner, new MonitoringCycleOptions());

        var conditions = await source.GetActiveConditionsAsync(CancellationToken.None);

        Assert.Equal(1, runner.Probes(failing.Id));
        Assert.Equal(0, runner.AuthScans(failing.Id));
        Assert.Equal(1, runner.Probes(healthy.Id));
        Assert.Equal(1, runner.AuthScans(healthy.Id));
        var unreachable = Assert.Single(conditions, condition => condition.Rule == "server-unreachable");
        Assert.Equal(failing.Id, unreachable.ServerId);
        Assert.Contains("status probe failed", unreachable.Details, StringComparison.Ordinal);
        Assert.DoesNotContain("could not reach the server", unreachable.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATimedOutStatusProbeAlsoSkipsTheScanAndIsReportedAsAFailedProbe()
    {
        var server = new Server("slow", "10.0.3.3", 22, "dva", "key", "staging");
        var runner = new ProbeRunner(server.Id, probeThrows: new SshOperationTimeoutException("The command did not finish within 20 s."));
        var scanner = new AuthLogScanner(runner, new HangSettings(), Options.Create(new SecurityMonitoringOptions()), TimeProvider.System);
        var source = AlertSource(new HangServers([server]), runner, scanner, new MonitoringCycleOptions());

        var conditions = await source.GetActiveConditionsAsync(CancellationToken.None);

        Assert.Equal(0, runner.AuthScans(server.Id));
        var unreachable = Assert.Single(conditions, condition => condition.Rule == "server-unreachable");
        Assert.Contains("status probe failed: The command did not finish within 20 s.", unreachable.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCachedScanStateIsReadWithoutAnyRemoteCommand()
    {
        var server = new Server("down", "10.0.3.1", 22, "dva", "key", "staging");
        var scanner = new AuthLogScanner(new ThrowingRunner(), new HangSettings(), Options.Create(new SecurityMonitoringOptions()), TimeProvider.System);

        var cached = await ((IAuthLogScanSource)scanner).GetCachedAsync(server, CancellationToken.None);

        Assert.Empty(cached);
    }

    [Fact]
    public async Task ConnectTimeCountsAgainstTheCallDeadline()
    {
        using var lab = new HangLab();
        // A connect that outlasts the deadline and ignores cancellation, as SSH.NET can on a blackholed route.
        var dead = lab.Add("dead", "192.0.2.1", script => { script.ConnectDelay = TimeSpan.FromSeconds(3); script.ConnectHonoursToken = false; });

        var stopwatch = Stopwatch.StartNew();
        var exception = await Assert.ThrowsAsync<HostUnreachableException>(() => lab.Run(dead, TimeSpan.FromMilliseconds(300)));

        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(250), $"{stopwatch.ElapsedMilliseconds} ms");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(1500), $"timed out after {stopwatch.ElapsedMilliseconds} ms, not at the deadline");
        // The host that did not connect in time is a connection failure like any other: it is backed off.
        var state = Assert.IsType<HostReachabilityState>(lab.Reachability.Get(dead.Id));
        Assert.Equal(HostFailureKind.Connect, state.Kind);
        Assert.IsType<SshOperationTimeoutException>(state.LastFailure);
        Assert.Contains("could not connect", exception.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<HostUnreachableException>(() => lab.Run(dead, TimeSpan.FromSeconds(5)));
        Assert.Equal(1, lab.Transports.Attempts(dead));
    }

    [Fact]
    public async Task TheCommandOnlyGetsWhatConnectingLeftOfTheDeadline()
    {
        using var lab = new HangLab();
        var host = lab.Add("slow-connect", "10.0.4.1", script =>
        {
            script.ConnectDelay = TimeSpan.FromMilliseconds(300);
            script.Commands = CommandMode.HangIgnoringToken;
        });

        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<SshOperationTimeoutException>(() => lab.Run(host, TimeSpan.FromMilliseconds(700)));

        // Connect (300 ms) + command (the remaining 400 ms) = the call's 700 ms, not 300 + 700.
        Assert.True(stopwatch.Elapsed >= TimeSpan.FromMilliseconds(600), $"{stopwatch.ElapsedMilliseconds} ms");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(950), $"took {stopwatch.ElapsedMilliseconds} ms: connect time was not counted");
    }

    [Fact]
    public async Task ACallerWaitingBehindAnotherCallersConnectStopsAtItsOwnDeadline()
    {
        using var lab = new HangLab();
        var host = lab.Add("busy", "10.0.4.2", script => { script.ConnectDelay = TimeSpan.FromSeconds(1); script.ConnectHonoursToken = false; });
        var owner = lab.Run(host, TimeSpan.FromSeconds(10));
        await Task.Delay(100);

        // The second caller is refused at once while a retry is in flight; with bypass it queues behind the first connect.
        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<Exception>(() => lab.Run(host, TimeSpan.FromMilliseconds(300), bypass: true));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(1500), $"took {stopwatch.ElapsedMilliseconds} ms");
        lab.Dispose();
        await Assert.ThrowsAnyAsync<Exception>(() => owner);
    }

    private static AlertSignalSource AlertSource(IServerRepository servers, ISshRunner runner, IAuthLogScanSource scanner, MonitoringCycleOptions options) =>
        new(servers, new AlertMetrics(), new AlertChecks(), new AlertSnapshots(), runner, scanner, new DriftService(new AlertSnapshots(), null!),
            new ContainerRestartMonitor(new HangSettings(), TimeProvider.System, NullLogger<ContainerRestartMonitor>.Instance), TimeProvider.System, options);

    private enum Behaviour { Answer, HangUntilCancelled }

    private sealed class SelectiveRunner(Func<string, Behaviour> behaviour) : ISshRunner
    {
        private static readonly string Sample = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "metrics-sample-1.txt"));

        public async Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options, CancellationToken cancellationToken)
        {
            if (behaviour(command) == Behaviour.HangUntilCancelled) await Task.Delay(Timeout.Infinite, cancellationToken);
            return new RemoteCommandResult(0, command == MetricsParser.CollectionCommand ? Sample : "", string.Empty, TimeSpan.Zero);
        }
    }

    /// <summary>Answers the status probe with success, an error exit or an exception, and counts auth-log scans per server.</summary>
    private sealed class ProbeRunner(Guid failingServer, Exception? probeThrows = null) : ISshRunner
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<(Guid, string), int> _calls = new();
        public int Probes(Guid id) => _calls.GetValueOrDefault((id, "probe"));
        public int AuthScans(Guid id) => _calls.GetValueOrDefault((id, "auth"));

        public Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options, CancellationToken cancellationToken)
        {
            var isAuthScan = command.Contains("journalctl", StringComparison.Ordinal);
            _calls.AddOrUpdate((serverId, isAuthScan ? "auth" : "probe"), 1, static (_, count) => count + 1);
            if (isAuthScan) return Task.FromResult(new RemoteCommandResult(0, "\n__DVA_BAN_TOOLS__\n__DVA_BAN_STATE__\n", string.Empty, TimeSpan.Zero));
            if (serverId == failingServer)
            {
                if (probeThrows is not null) throw probeThrows;
                return Task.FromResult(new RemoteCommandResult(1, string.Empty, "boom", TimeSpan.Zero));
            }

            return Task.FromResult(new RemoteCommandResult(0, "__DVA_DOCKER__\n__DVA_SYSTEMD_FAILED__\n", string.Empty, TimeSpan.Zero));
        }
    }

    private sealed class ThrowingRunner : ISshRunner
    {
        public Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No remote command may run.");
    }

    private sealed class AlertMetrics : IMetricRepository
    {
        public Task AddSamplesAsync(IReadOnlyCollection<MetricSample> samples, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<MetricSample>> GetRecentAsync(Guid serverId, DateTimeOffset sinceUtc, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MetricSample>>([]);
        public Task<IReadOnlyList<MetricSample>> GetLatestAsync(Guid serverId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MetricSample>>([]);
        public Task<IReadOnlyList<MetricSample>> GetUnrolledAsync(DateTimeOffset beforeUtc, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MetricSample>>([]);
        public Task SaveRollupsAsync(IReadOnlyCollection<MetricRollup> rollups, DateTimeOffset rawPruneBeforeUtc, DateTimeOffset rollupPruneBeforeUtc, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class AlertChecks : IHealthCheckRepository
    {
        public Task<IReadOnlyList<HealthCheck>> ListAsync(Guid serverId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<HealthCheck>>([]);
        public Task<IReadOnlyList<HealthCheck>> ListEnabledAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<HealthCheck>>([]);
        public Task<HealthCheck?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<HealthCheck?>(null);
        public Task AddAsync(HealthCheck healthCheck, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SaveAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteAsync(HealthCheck healthCheck, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class AlertSnapshots : ISnapshotRepository
    {
        public Task<Snapshot?> GetLatestAsync(CancellationToken cancellationToken) => Task.FromResult<Snapshot?>(null);
        public Task<Snapshot?> GetLatestRunAsync(CancellationToken cancellationToken) => Task.FromResult<Snapshot?>(null);
        public Task<IReadOnlyList<Snapshot>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Snapshot>>([]);
        public Task<Snapshot> CreateAsync(IReadOnlyCollection<Guid> serverIds, DateTimeOffset startedAtUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CompleteAsync(Snapshot snapshot, IReadOnlyCollection<CollectorRun> collectorRuns, DateTimeOffset finishedAtUtc, int succeededServerCount, int failedServerCount, bool cancelled, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task FailAsync(Snapshot snapshot, IReadOnlyCollection<CollectorRun> collectorRuns, DateTimeOffset finishedAtUtc, int succeededServerCount, int failedServerCount, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
