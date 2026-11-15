using System.Diagnostics;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Application.Monitoring;
using DevOpsAgent.Domain.Monitoring;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Infrastructure.Monitoring;
using Microsoft.Extensions.Options;

namespace DevOpsAgent.Tests;

/// <summary>
/// T7.R17: the host's stopping token (SIGTERM) ends every connect and command promptly, nothing new starts afterwards, and a
/// shutdown is a clean cancellation, never an ObjectDisposedException, a server failure or an alert.
/// </summary>
public sealed class SshShutdownTests
{
    private static readonly TimeSpan Prompt = TimeSpan.FromMilliseconds(1000);

    [Fact]
    public async Task StoppingAbortsAConnectInFlightEvenWhenTheTransportIgnoresItsToken()
    {
        using var lab = new HangLab();
        var blackhole = lab.Add("blackhole", "192.0.2.1", script => { script.ConnectNeverEnds = true; script.ConnectHonoursToken = false; });
        // The caller passes no token at all, like a UI-started discovery run (CancellationToken.None).
        var running = lab.Run(blackhole, TimeSpan.FromMinutes(5));
        await WaitUntil(() => lab.Transports.Attempts(blackhole) == 1);

        var stopwatch = Stopwatch.StartNew();
        lab.Lifetime.StopApplication();
        await Assert.ThrowsAsync<SshShutdownException>(() => running.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.True(stopwatch.Elapsed < Prompt, $"took {stopwatch.ElapsedMilliseconds} ms");
        // A shutdown is not a failure of the server: no backoff, no lease left behind.
        Assert.Null(lab.Reachability.Get(blackhole.Id));
        Assert.Equal(0, lab.Pool.ActiveLeases);
        Assert.Equal(0, lab.Pool.LiveClients);
    }

    [Fact]
    public async Task StoppingAbandonsACommandInFlightEvenWhenTheTransportIgnoresItsToken()
    {
        using var lab = new HangLab();
        var host = lab.Add("busy", "10.0.0.5", script => script.Commands = CommandMode.HangIgnoringToken);
        var running = lab.Run(host, TimeSpan.FromMinutes(5));
        await WaitUntil(() => lab.Transports.Commands(host) == 1);

        var stopwatch = Stopwatch.StartNew();
        lab.Lifetime.StopApplication();
        await Assert.ThrowsAsync<SshShutdownException>(() => running.WaitAsync(TimeSpan.FromSeconds(10)));

        // The command would have waited for its own five-minute timeout; shutdown does not.
        Assert.True(stopwatch.Elapsed < Prompt, $"took {stopwatch.ElapsedMilliseconds} ms");
        Assert.Equal(0, lab.Pool.ActiveLeases);
        // Not counted as a command timeout either.
        Assert.Null(lab.Reachability.Get(host.Id));
    }

    [Fact]
    public async Task StoppingAbortsAConnectBehindAJumpHost()
    {
        using var lab = new HangLab();
        var edge = lab.Add("edge", "10.0.0.1");
        var blackhole = lab.Add("blackhole", "192.0.2.1", script => { script.ConnectNeverEnds = true; script.ConnectHonoursToken = false; }, edge);
        var running = lab.Run(blackhole, TimeSpan.FromMinutes(5));
        await WaitUntil(() => lab.Transports.Attempts(blackhole) == 1);

        var stopwatch = Stopwatch.StartNew();
        lab.Lifetime.StopApplication();
        await Assert.ThrowsAsync<SshShutdownException>(() => running.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.True(stopwatch.Elapsed < Prompt, $"took {stopwatch.ElapsedMilliseconds} ms");
        Assert.Null(lab.Reachability.Get(blackhole.Id));
        Assert.Null(lab.Reachability.Get(edge.Id));
    }

    [Fact]
    public async Task OnceStoppingNoNewConnectionOrCommandStartsAndTheTransportIsNeverCalled()
    {
        using var lab = new HangLab();
        var host = lab.Add("web", "10.0.0.6");
        // A client leased before shutdown began.
        using var session = await lab.Pool.GetSessionAsync(host.Id, false, CancellationToken.None);
        var attempts = lab.Transports.Attempts(host);
        var commands = lab.Transports.Commands(host);
        var other = lab.Add("other", "10.0.0.7");

        lab.Lifetime.StopApplication();

        await Assert.ThrowsAsync<SshShutdownException>(() => lab.Run(other, TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAsync<SshShutdownException>(() => lab.Run(host, TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAsync<SshShutdownException>(() => session.ExecuteAsync("true", TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Equal(0, lab.Transports.Attempts(other));
        Assert.Equal(attempts, lab.Transports.Attempts(host));
        Assert.Equal(commands, lab.Transports.Commands(host));
        // The refusal is a cancellation, which every hosted loop already treats as "stop quietly".
        Assert.IsAssignableFrom<OperationCanceledException>(new SshShutdownException());
        Assert.Null(lab.Reachability.Get(other.Id));
    }

    [Fact]
    public async Task ADisposedPoolRefusesWithoutObjectDisposedAndDisposeDoesNotWaitForAConnectInFlight()
    {
        var lab = new HangLab();
        var slow = lab.Add("slow", "10.0.0.8", script => { script.ConnectDelay = TimeSpan.FromMilliseconds(600); script.ConnectHonoursToken = false; });
        var running = lab.Run(slow, TimeSpan.FromMinutes(5));
        await WaitUntil(() => lab.Transports.Attempts(slow) == 1);

        var stopwatch = Stopwatch.StartNew();
        lab.Pool.Dispose();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(300), $"Dispose took {stopwatch.ElapsedMilliseconds} ms");

        // The attempt finishes after the pool is gone: its caller gets the shutdown cancellation (not ObjectDisposedException)
        // and the late connection is closed, not leaked.
        await Assert.ThrowsAsync<SshShutdownException>(() => running.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.All(lab.Transports.Created, transport => Assert.True(transport.Disposed));
        await Assert.ThrowsAsync<SshShutdownException>(() => lab.Run(slow, TimeSpan.FromSeconds(1)));
        Assert.Null(lab.Reachability.Get(slow.Id));
    }

    [Fact]
    public async Task AConnectThatFinishesAfterStoppingIsClosedNotKept()
    {
        using var lab = new HangLab();
        var host = lab.Add("late", "10.0.0.4", script => { script.ConnectDelay = TimeSpan.FromMilliseconds(300); script.ConnectHonoursToken = false; });
        var running = lab.Run(host, TimeSpan.FromMinutes(5));
        await WaitUntil(() => lab.Transports.Attempts(host) == 1);

        lab.Lifetime.StopApplication();
        await Assert.ThrowsAsync<SshShutdownException>(() => running.WaitAsync(TimeSpan.FromSeconds(10)));

        // The abandoned attempt completes in the background and closes what it opened.
        await WaitUntil(() => lab.Transports.Created.Count == 1 && lab.Transports.Created[0].Disposed);
        Assert.Equal(0, lab.Pool.LiveClients);
    }

    [Fact]
    public async Task ACallersOwnCancellationStillReadsAsTheirCancellation()
    {
        using var lab = new HangLab();
        var host = lab.Add("web", "10.0.0.3", script => script.Commands = CommandMode.HangIgnoringToken);
        using var cancellation = new CancellationTokenSource();
        var running = lab.Run(host, TimeSpan.FromMinutes(5), cancellation.Token);
        await WaitUntil(() => lab.Transports.Commands(host) == 1);

        await cancellation.CancelAsync();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.IsNotType<SshShutdownException>(exception);
    }

    [Fact]
    public async Task ShutdownDuringAnAlertEvaluationOpensNoAlertAndEndsTheEvaluation()
    {
        var server = new Server("web", "10.0.0.2", 22, "dva", "key", "staging");
        var runner = new ShutdownRunner();
        var source = new AlertSignalSource(new HangServers([server]), new NoMetrics(), new NoChecks(), new NoSnapshots(), runner,
            new AuthLogScanner(runner, new HangSettings(), Options.Create(new SecurityMonitoringOptions()), TimeProvider.System),
            new DriftService(new NoSnapshots(), null!), new ContainerRestartMonitor(new HangSettings(), TimeProvider.System), TimeProvider.System);

        // Not a "server-unreachable" condition (which would open an alert while the app is going down): the evaluation just ends.
        await Assert.ThrowsAsync<SshShutdownException>(() => source.GetActiveConditionsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ShutdownDuringAMetricCycleEndsItWithoutRecordingAFailure()
    {
        using var lab = new HangLab();
        var hung = lab.Add("hung", "10.0.0.9", script => script.Commands = CommandMode.HangIgnoringToken);
        var metrics = new HangMetrics();
        var service = new MetricService(lab.Servers, metrics, lab.Runner, new ContainerRestartMonitor(new HangSettings(), lab.Time), lab.Time);
        var polling = service.PollAsync(CancellationToken.None);
        await WaitUntil(() => lab.Transports.Commands(hung) == 1);

        var stopwatch = Stopwatch.StartNew();
        lab.Lifetime.StopApplication();

        await Assert.ThrowsAsync<SshShutdownException>(() => polling.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(stopwatch.Elapsed < Prompt, $"took {stopwatch.ElapsedMilliseconds} ms");
        Assert.Null(lab.Reachability.Get(hung.Id));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("The condition was never met.");
            await Task.Delay(10);
        }
    }

    private sealed class ShutdownRunner : ISshRunner
    {
        public Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options, CancellationToken cancellationToken) =>
            throw new SshShutdownException();
    }

    private sealed class NoMetrics : IMetricRepository
    {
        public Task AddSamplesAsync(IReadOnlyCollection<MetricSample> samples, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<MetricSample>> GetRecentAsync(Guid serverId, DateTimeOffset sinceUtc, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MetricSample>>([]);
        public Task<IReadOnlyList<MetricSample>> GetLatestAsync(Guid serverId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MetricSample>>([]);
        public Task<IReadOnlyList<MetricSample>> GetUnrolledAsync(DateTimeOffset beforeUtc, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MetricSample>>([]);
        public Task SaveRollupsAsync(IReadOnlyCollection<MetricRollup> rollups, DateTimeOffset rawPruneBeforeUtc, DateTimeOffset rollupPruneBeforeUtc, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoChecks : IHealthCheckRepository
    {
        public Task<IReadOnlyList<HealthCheck>> ListAsync(Guid serverId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<HealthCheck>>([]);
        public Task<IReadOnlyList<HealthCheck>> ListEnabledAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<HealthCheck>>([]);
        public Task<HealthCheck?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<HealthCheck?>(null);
        public Task AddAsync(HealthCheck healthCheck, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SaveAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteAsync(HealthCheck healthCheck, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoSnapshots : ISnapshotRepository
    {
        public Task<DevOpsAgent.Domain.Discovery.Snapshot?> GetLatestAsync(CancellationToken cancellationToken) => Task.FromResult<DevOpsAgent.Domain.Discovery.Snapshot?>(null);
        public Task<DevOpsAgent.Domain.Discovery.Snapshot?> GetLatestRunAsync(CancellationToken cancellationToken) => Task.FromResult<DevOpsAgent.Domain.Discovery.Snapshot?>(null);
        public Task<IReadOnlyList<DevOpsAgent.Domain.Discovery.Snapshot>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DevOpsAgent.Domain.Discovery.Snapshot>>([]);
        public Task<DevOpsAgent.Domain.Discovery.Snapshot> CreateAsync(IReadOnlyCollection<Guid> serverIds, DateTimeOffset startedAtUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CompleteAsync(DevOpsAgent.Domain.Discovery.Snapshot snapshot, IReadOnlyCollection<DevOpsAgent.Domain.Discovery.CollectorRun> collectorRuns, DateTimeOffset finishedAtUtc, int succeededServerCount, int failedServerCount, bool cancelled, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task FailAsync(DevOpsAgent.Domain.Discovery.Snapshot snapshot, IReadOnlyCollection<DevOpsAgent.Domain.Discovery.CollectorRun> collectorRuns, DateTimeOffset finishedAtUtc, int succeededServerCount, int failedServerCount, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
