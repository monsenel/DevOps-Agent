using System.Net.Sockets;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Application.Monitoring;
using DevOpsAgent.Application.Servers;
using DevOpsAgent.Domain.Discovery;
using DevOpsAgent.Domain.Monitoring;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Infrastructure.Monitoring;
using DevOpsAgent.Infrastructure.Ssh;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Renci.SshNet.Common;

namespace DevOpsAgent.Tests;

/// <summary>
/// T7.R19: everything that shows or handles a host that cannot be used treats a failed login as its own state: Test
/// connection names the real cause, the monitoring alert says "authentication failed", the metric and alert cycles skip the
/// host for the backoff interval like an unreachable one, and nothing prints a secret.
/// </summary>
public sealed class SshAuthFailureConsumerTests
{
    private const string Canary = "-----BEGIN OPENSSH PRIVATE KEY----- hunter2-passphrase-canary";
    private static readonly TimeSpan Short = TimeSpan.FromSeconds(5);

    // ---- Test connection ----

    [Theory]
    [InlineData("rejected", "the server rejected the SSH key")]
    [InlineData("missing", "key file not found")]
    [InlineData("unreadable", "key file cannot be read")]
    [InlineData("passphrase", "the key needs a passphrase and none is stored")]
    [InlineData("invalid", "wrong passphrase or invalid key file")]
    public async Task TestConnectionShowsTheRealAuthenticationCause(string error, string reason)
    {
        using var lab = new HangLab();
        var server = lab.Add("srv", "10.0.0.1", script => script.ConnectFails = () => Login(error));

        var result = await new ServerCapabilityService(lab.Runner, lab.Servers, lab.Time).TestAsync(server.Id, CancellationToken.None);

        Assert.False(result.Reachable);
        Assert.StartsWith($"srv: authentication failed ({reason}). Since ", result.Error, StringComparison.Ordinal);
        Assert.Contains("Check the identity key file, its passphrase", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("unreachable", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("could not connect", result.Error, StringComparison.OrdinalIgnoreCase);
        AssertNoSecret(result.Error!);
    }

    [Theory]
    [InlineData("timeout", "Cause: connection timed out.")]
    [InlineData("refused", "Cause: connection refused.")]
    [InlineData("dns", "Cause: host name could not be resolved.")]
    public async Task TestConnectionShowsTheRealConnectionCause(string failure, string expected)
    {
        using var lab = new HangLab();
        var server = lab.Add("srv", "10.0.0.1", script => script.ConnectFails = () => failure switch
        {
            "timeout" => new SshOperationTimeoutException($"timed out {Canary}"),
            "refused" => new SocketException((int)SocketError.ConnectionRefused),
            _ => new SocketException((int)SocketError.HostNotFound)
        });

        var result = await new ServerCapabilityService(lab.Runner, lab.Servers, lab.Time).TestAsync(server.Id, CancellationToken.None);

        Assert.False(result.Reachable);
        Assert.StartsWith("srv is unreachable (since ", result.Error, StringComparison.Ordinal);
        Assert.Contains(expected, result.Error, StringComparison.Ordinal);
        AssertNoSecret(result.Error!);
    }

    [Fact]
    public async Task TestConnectionStillReportsAHostKeyMismatchAsSuch()
    {
        using var lab = new HangLab();
        var server = lab.Add("srv", "10.0.0.1", script => script.ConnectFails = () => new SshHostKeyMismatchException("10.0.0.1", 22, "abc"));

        var result = await new ServerCapabilityService(lab.Runner, lab.Servers, lab.Time).TestAsync(server.Id, CancellationToken.None);

        Assert.False(result.Reachable);
        Assert.Contains("is not pinned (SHA256:abc)", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TestConnectionBehindAJumpHostWhoseLoginFailedNamesTheJumpHost()
    {
        using var lab = new HangLab();
        var edge = lab.Add("edge", "10.0.0.1", script => script.ConnectFails = () => new SshAuthenticationException(Canary));
        var app = lab.Add("app", "10.0.0.2", jump: edge);

        var result = await new ServerCapabilityService(lab.Runner, lab.Servers, lab.Time).TestAsync(app.Id, CancellationToken.None);

        Assert.False(result.Reachable);
        Assert.StartsWith("app via jump host edge: authentication failed (the server rejected the SSH key).", result.Error, StringComparison.Ordinal);
        AssertNoSecret(result.Error!);
    }

    [Fact]
    public async Task TestConnectionIsRetryNowAndFixingTheKeyMakesItSucceedAtOnce()
    {
        var clock = new ManualClock();
        using var lab = new HangLab(clock);
        var server = lab.Add("srv", "10.0.0.1", script => script.ConnectFails = () => new FileNotFoundException(Canary));
        var service = new ServerCapabilityService(lab.Runner, lab.Servers, lab.Time);
        Assert.False((await service.TestAsync(server.Id, CancellationToken.None)).Reachable);

        // Still inside the first minute: a Test connection is not held back by the backoff.
        lab.Transports.Script(server.Host).ConnectFails = null;
        clock.Advance(TimeSpan.FromSeconds(5));
        var result = await service.TestAsync(server.Id, CancellationToken.None);

        Assert.True(result.Reachable, result.Error);
        Assert.Null(lab.Reachability.Get(server.Id));
    }

    [Fact]
    public async Task AnAuthFailureThrownByAnotherPoolIsStillReportedAsOne()
    {
        var servers = new Server("srv", "10.0.0.1", 22, "dva", "key", "staging");
        var service = new ServerCapabilityService(new ThrowingRunner(new SshAuthFailedException(SshAuthFailure.KeyFileNotFound)),
            new HangServers([servers]), TimeProvider.System);

        var result = await service.TestAsync(servers.Id, CancellationToken.None);

        Assert.False(result.Reachable);
        Assert.StartsWith("Authentication failed (key file not found).", result.Error, StringComparison.Ordinal);
    }

    // ---- Monitoring alert ----

    [Fact]
    public async Task TheMonitoringAlertSaysAuthenticationFailedAndReusesTheConnectivityRule()
    {
        using var lab = new HangLab();
        var edge = lab.Add("edge", "10.0.0.1", script => script.ConnectFails = () => new FileNotFoundException(Canary));
        var app = lab.Add("app", "10.0.0.2", jump: edge);
        var down = lab.Add("down", "10.0.0.3", script => script.ConnectFails = () => new SocketException((int)SocketError.TimedOut));
        var source = AlertSource(lab);

        var conditions = await source.GetActiveConditionsAsync(CancellationToken.None);

        var unreachable = conditions.Where(condition => condition.Rule == "server-unreachable").ToDictionary(condition => condition.ServerId!.Value);
        Assert.Equal(3, unreachable.Count);

        // The same rule, key and severity (so lifecycle, de-duplication and notifications are unchanged) ...
        var edgeAlert = unreachable[edge.Id];
        Assert.Equal($"server-unreachable:{edge.Id:N}", edgeAlert.Key);
        Assert.Equal(AlertSeverity.High, edgeAlert.Severity);
        // ... with a title and details that say what happened.
        Assert.Equal("edge: SSH authentication failed", edgeAlert.Title);
        Assert.StartsWith("edge: authentication failed (key file not found).", edgeAlert.Details, StringComparison.Ordinal);
        var appAlert = unreachable[app.Id];
        Assert.Equal("app: SSH authentication failed on jump host edge", appAlert.Title);
        Assert.StartsWith("app via jump host edge: authentication failed (key file not found).", appAlert.Details, StringComparison.Ordinal);
        Assert.Equal(AlertSeverity.High, appAlert.Severity);
        // No user name, key path or secret in either.
        foreach (var alert in new[] { edgeAlert, appAlert })
        {
            AssertNoSecret(alert.Title);
            AssertNoSecret(alert.Details);
            Assert.DoesNotContain("dva", alert.Title + alert.Details, StringComparison.Ordinal);
        }

        // A host that is really unreachable keeps the old wording.
        var downAlert = unreachable[down.Id];
        Assert.Equal("down is unreachable", downAlert.Title);
        Assert.Contains("is unreachable (since", downAlert.Details, StringComparison.Ordinal);
        Assert.DoesNotContain("authentication", downAlert.Title + downAlert.Details, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnAuthFailedHostIsSkippedByTheAlertCycleForTheBackoffIntervalAndTheAlertEndsWhenItWorks()
    {
        var clock = new ManualClock();
        using var lab = new HangLab(clock);
        var host = lab.Add("srv", "10.0.0.1", script => script.ConnectFails = () => new SshAuthenticationException(Canary));
        var healthy = lab.Add("ok", "10.0.0.2");
        var source = AlertSource(lab);

        var first = await source.GetActiveConditionsAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(30));
        var second = await source.GetActiveConditionsAsync(CancellationToken.None);

        // One attempt for the minute: the second 30 s evaluation did not touch the host.
        Assert.Equal(1, lab.Transports.Attempts(host));
        Assert.Contains(first, condition => condition.Rule == "server-unreachable" && condition.ServerId == host.Id);
        Assert.Contains(second, condition => condition.Rule == "server-unreachable" && condition.ServerId == host.Id);
        Assert.DoesNotContain(second, condition => condition.Rule == "server-unreachable" && condition.ServerId == healthy.Id);

        // The key works again: the next evaluation after the minute connects and the condition is gone.
        lab.Transports.Script(host.Host).ConnectFails = null;
        clock.Advance(TimeSpan.FromSeconds(31));
        var third = await source.GetActiveConditionsAsync(CancellationToken.None);
        Assert.Equal(2, lab.Transports.Attempts(host));
        Assert.DoesNotContain(third, condition => condition.Rule == "server-unreachable");
    }

    [Fact]
    public async Task AnAuthFailedHostIsNotAPollingFailureAndIsSkippedByTheMetricCycleLikeAnUnreachableOne()
    {
        var clock = new ManualClock();
        using var lab = new HangLab(clock);
        var host = lab.Add("srv", "10.0.0.1", script => script.ConnectFails = () => new SshAuthenticationException(Canary));
        var healthy = lab.Add("ok", "10.0.0.2");
        var metrics = new HangMetrics();
        var service = new MetricService(lab.Servers, metrics, lab.Runner,
            new ContainerRestartMonitor(new HangSettings(), clock, NullLogger<ContainerRestartMonitor>.Instance), clock);

        // Expected state, not an error of the poller (the alert reports it).
        await service.PollAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(30));
        await service.PollAsync(CancellationToken.None);

        Assert.Equal(1, lab.Transports.Attempts(host));
        Assert.Equal(2, metrics.CyclesStored(healthy.Id));
        Assert.Equal(0, metrics.CyclesStored(host.Id));
    }

    private static Exception Login(string error) => error switch
    {
        "rejected" => new SshAuthenticationException(Canary),
        "missing" => new FileNotFoundException(Canary, "/keys/" + Canary),
        "unreadable" => new UnauthorizedAccessException(Canary),
        "passphrase" => new SshPassPhraseNullOrEmptyException(Canary),
        _ => new SshAuthFailedException(SshAuthFailure.KeyInvalidOrWrongPassphrase)
    };

    private static void AssertNoSecret(string text)
    {
        Assert.DoesNotContain("PRIVATE KEY", text, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/keys/", text, StringComparison.Ordinal);
    }

    internal static AlertSignalSource AlertSource(HangLab lab) =>
        new(lab.Servers, new HangMetrics(), new EmptyChecks(), new EmptySnapshots(), lab.Runner,
            new AuthLogScanner(lab.Runner, new HangSettings(), Options.Create(new SecurityMonitoringOptions()), lab.Time),
            new DriftService(new EmptySnapshots(), null!),
            new ContainerRestartMonitor(new HangSettings(), lab.Time, NullLogger<ContainerRestartMonitor>.Instance), lab.Time);

    private sealed class ThrowingRunner(Exception exception) : ISshRunner
    {
        public Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options, CancellationToken cancellationToken) =>
            throw exception;
    }

    private sealed class EmptyChecks : IHealthCheckRepository
    {
        public Task<IReadOnlyList<HealthCheck>> ListAsync(Guid serverId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<HealthCheck>>([]);
        public Task<IReadOnlyList<HealthCheck>> ListEnabledAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<HealthCheck>>([]);
        public Task<HealthCheck?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<HealthCheck?>(null);
        public Task AddAsync(HealthCheck healthCheck, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SaveAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteAsync(HealthCheck healthCheck, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class EmptySnapshots : ISnapshotRepository
    {
        public Task<Snapshot?> GetLatestAsync(CancellationToken cancellationToken) => Task.FromResult<Snapshot?>(null);
        public Task<Snapshot?> GetLatestRunAsync(CancellationToken cancellationToken) => Task.FromResult<Snapshot?>(null);
        public Task<IReadOnlyList<Snapshot>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Snapshot>>([]);
        public Task<Snapshot> CreateAsync(IReadOnlyCollection<Guid> serverIds, DateTimeOffset startedAtUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CompleteAsync(Snapshot snapshot, IReadOnlyCollection<CollectorRun> collectorRuns, DateTimeOffset finishedAtUtc, int succeededServerCount, int failedServerCount, bool cancelled, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task FailAsync(Snapshot snapshot, IReadOnlyCollection<CollectorRun> collectorRuns, DateTimeOffset finishedAtUtc, int succeededServerCount, int failedServerCount, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
