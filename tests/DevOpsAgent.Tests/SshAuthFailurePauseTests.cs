using System.Net.Sockets;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Application.Monitoring;
using DevOpsAgent.Application.Servers;
using DevOpsAgent.Domain.Discovery;
using DevOpsAgent.Domain.Servers;
using Microsoft.Extensions.Logging.Abstractions;
using Renci.SshNet.Common;

namespace DevOpsAgent.Tests;

/// <summary>
/// D-039 amendment (fail2ban safety): after <see cref="IHostReachability.AuthFailedAttemptLimit"/> consecutive failed logins a
/// host's automatic connection attempts stop altogether, whatever the caller (cycles, discovery, health checks, jump
/// dependents). Only "Retry now", "Test connection" (one attempt per press) or an edit of the connection settings (a full
/// reset) end it; a success or a failure of another kind breaks the run of consecutive failures.
/// </summary>
public sealed class SshAuthFailurePauseTests
{
    private const int Limit = IHostReachability.AuthFailedAttemptLimit;
    private static readonly TimeSpan Short = TimeSpan.FromSeconds(5);

    private static SshAuthenticationException Rejected() => new("Permission denied (publickey).");

    // Connect headroom, as in the other pool tests: a loaded machine must not turn an instant fake connect into a deadline.
    private static HangLab NewLab(ManualClock clock) => new(clock, HangLab.ConnectHeadroom);

    // The automatic attempts: one now, one a minute later, one a minute after that. Between them the host is skipped.
    private static async Task PauseAsync(HangLab lab, ManualClock clock, Server host)
    {
        var start = lab.Transports.Attempts(host);
        for (var attempt = 1; attempt <= Limit; attempt++)
        {
            await Assert.ThrowsAsync<HostUnreachableException>(() => lab.Run(host, Short));
            Assert.Equal(start + attempt, lab.Transports.Attempts(host));
            if (attempt == Limit) break;
            clock.Advance(TimeSpan.FromSeconds(58));
            await Assert.ThrowsAsync<HostUnreachableException>(() => lab.Run(host, Short));
            Assert.Equal(start + attempt, lab.Transports.Attempts(host));
            clock.Advance(TimeSpan.FromSeconds(2));
        }

        Assert.True(lab.Reachability.Get(host.Id)!.AutomaticRetriesPaused);
    }

    // Hours of fake time: never an automatic attempt, and every caller is told at once that retries are paused.
    private static async Task AssertNoAutomaticAttemptAsync(HangLab lab, ManualClock clock, Server host, int expectedAttempts)
    {
        foreach (var step in new[] { TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(14), TimeSpan.FromHours(1), TimeSpan.FromHours(6) })
        {
            clock.Advance(step);
            var skipped = await Assert.ThrowsAsync<HostUnreachableException>(() => lab.Run(host, Short));
            Assert.Equal(expectedAttempts, lab.Transports.Attempts(host));
            Assert.True(skipped.AutomaticRetriesPaused);
            Assert.Contains("automatic retries paused", skipped.Message, StringComparison.Ordinal);
        }
    }

    // ---- The cap ----

    [Fact]
    public async Task ThreeAttemptsOneMinuteApartThenNoFourthAutomaticAttemptEvenAfterHours()
    {
        var clock = new ManualClock();
        using var lab = NewLab(clock);
        var times = new List<DateTimeOffset>();
        var host = lab.Add("srv", "10.0.0.1", script => script.ConnectFails = () =>
        {
            times.Add(clock.GetUtcNow());
            return Rejected();
        });

        await PauseAsync(lab, clock, host);
        await AssertNoAutomaticAttemptAsync(lab, clock, host, Limit);

        // The first three kept today's cadence: a minute apart.
        Assert.Equal(Limit, times.Count);
        for (var index = 1; index < times.Count; index++)
            Assert.Equal(TimeSpan.FromMinutes(1), times[index] - times[index - 1]);

        var state = lab.Reachability.Get(host.Id)!;
        Assert.Equal(HostFailureKind.AuthFailed, state.Kind);
        Assert.Equal(Limit, state.ConsecutiveFailures);
        Assert.True(lab.Reachability.IsBlocked(host.Id, out _));
        Assert.False(lab.Reachability.TryBeginAttempt(host.Id, bypassBackoff: false, out var blockedBy));
        Assert.True(blockedBy!.AutomaticRetriesPaused);
    }

    [Fact]
    public async Task AServerThatHangsUpDuringLoginIsPausedLikeOneThatRefusesTheKey()
    {
        // sshd closes the socket at MaxAuthTries or under a login penalty instead of answering "denied": the transport
        // reports that as a login failure (SshNetTransportFactory, KeyAuthentication), so the cap applies to it as well.
        var clock = new ManualClock();
        using var lab = NewLab(clock);
        var host = lab.Add("limited", "10.0.0.4", script => script.ConnectFails = () => new SshAuthFailedException(SshAuthFailure.ServerClosedDuringLogin));

        await PauseAsync(lab, clock, host);
        await AssertNoAutomaticAttemptAsync(lab, clock, host, Limit);

        var state = lab.Reachability.Get(host.Id)!;
        Assert.Equal(HostFailureKind.AuthFailed, state.Kind);
        Assert.Equal("the server closed the connection during login", state.Cause);
    }

    [Fact]
    public void ThePauseIsAnOptionWithADefaultOfThreeNotAMagicNumber() =>
        Assert.Equal(3, IHostReachability.AuthFailedAttemptLimit);

    // ---- Every automatic caller is skipped ----

    [Fact]
    public async Task MetricCyclesMakeNoAttemptOnAPausedHostButKeepPollingTheOthers()
    {
        var clock = new ManualClock();
        using var lab = NewLab(clock);
        var host = lab.Add("srv", "10.0.0.1", script => script.ConnectFails = Rejected);
        var healthy = lab.Add("ok", "10.0.0.2");
        var metrics = new HangMetrics();
        var service = new MetricService(lab.Servers, metrics, lab.Runner,
            new ContainerRestartMonitor(new HangSettings(), clock, NullLogger<ContainerRestartMonitor>.Instance), clock);

        // The 60 s metric cycles are the automatic attempts: three, and then none for hours.
        for (var cycle = 0; cycle < Limit + 4; cycle++)
        {
            await service.PollAsync(CancellationToken.None);
            clock.Advance(TimeSpan.FromMinutes(1));
        }

        for (var hours = 0; hours < 5; hours++)
        {
            clock.Advance(TimeSpan.FromHours(1));
            await service.PollAsync(CancellationToken.None);
        }

        Assert.Equal(Limit, lab.Transports.Attempts(host));
        Assert.Equal(0, metrics.CyclesStored(host.Id));
        Assert.Equal(Limit + 4 + 5, metrics.CyclesStored(healthy.Id));
        Assert.True(lab.Reachability.Get(host.Id)!.AutomaticRetriesPaused);
    }

    [Fact]
    public async Task AlertEvaluationsMakeNoAttemptOnAPausedHostAndKeepItsAlertOpenWithPausedText()
    {
        var clock = new ManualClock();
        using var lab = NewLab(clock);
        var host = lab.Add("srv", "10.0.0.1", script => script.ConnectFails = Rejected);
        var source = SshAuthFailureConsumerTests.AlertSource(lab);

        // The 30 s alert cycles: one attempt per minute for three minutes, then none.
        var all = new List<IReadOnlyList<AlertCondition>>();
        for (var cycle = 0; cycle < 2 * (Limit + 2); cycle++)
        {
            all.Add(await source.GetActiveConditionsAsync(CancellationToken.None));
            clock.Advance(TimeSpan.FromSeconds(30));
        }

        clock.Advance(TimeSpan.FromHours(3));
        all.Add(await source.GetActiveConditionsAsync(CancellationToken.None));

        Assert.Equal(Limit, lab.Transports.Attempts(host));
        // The alert never goes away because of the pause: every evaluation reports it, and once paused it says so.
        Assert.All(all, conditions => Assert.Single(conditions, condition => condition.ServerId == host.Id));
        var pausedAlert = Assert.Single(all[^1]);
        Assert.Equal("ssh-auth-failed", pausedAlert.Rule);
        Assert.Contains("SSH authentication failed", pausedAlert.Title, StringComparison.Ordinal);
        Assert.Contains("authentication failed — automatic retries paused", pausedAlert.Details, StringComparison.Ordinal);
        Assert.Equal(all[0].Single().Key, pausedAlert.Key);
        Assert.Equal(all[0].Single().Rule, pausedAlert.Rule);
    }

    [Fact]
    public async Task DiscoveryMakesNoAttemptOnAPausedHostAndNamesTheReasonInTheRun()
    {
        var clock = new ManualClock();
        using var lab = NewLab(clock);
        var host = lab.Add("srv", "10.0.0.1", script => script.ConnectFails = Rejected);
        var healthy = lab.Add("ok", "10.0.0.2");
        var snapshots = new RecordingSnapshots();
        var discovery = new DiscoveryService(lab.Servers, snapshots, new NoGraphs(), new GraphBuilder(), lab.Runner, [new ProbeCollector()], clock);

        for (var run = 0; run < Limit + 3; run++)
        {
            await discovery.RunAsync(null, CancellationToken.None);
            clock.Advance(TimeSpan.FromMinutes(1));
        }

        clock.Advance(TimeSpan.FromHours(4));
        await discovery.RunAsync(null, CancellationToken.None);

        Assert.Equal(Limit, lab.Transports.Attempts(host));
        Assert.Equal(Limit + 3 + 1, lab.Transports.Commands(healthy));
        var last = snapshots.Runs[^1].Single(item => item.ServerId == host.Id);
        Assert.Equal(CollectorRunStatus.Failed, last.Status);
        Assert.StartsWith("srv: authentication failed — automatic retries paused (the server rejected the SSH key).", last.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DependentsBehindAPausedJumpHostMakeNoAttemptAndSayWhy()
    {
        var clock = new ManualClock();
        using var lab = NewLab(clock);
        var edge = lab.Add("edge", "10.0.0.1", script => script.ConnectFails = Rejected);
        var app = lab.Add("app", "10.0.0.2", jump: edge);
        var db = lab.Add("db", "10.0.0.3", jump: edge);
        await PauseAsync(lab, clock, edge);

        foreach (var step in new[] { TimeSpan.FromMinutes(2), TimeSpan.FromHours(1), TimeSpan.FromHours(8) })
        {
            clock.Advance(step);
            var viaApp = await Assert.ThrowsAsync<HostUnreachableException>(() => lab.Run(app, Short));
            var viaDb = await Assert.ThrowsAsync<HostUnreachableException>(() => lab.Run(db, Short));
            Assert.StartsWith("app via jump host edge: authentication failed — automatic retries paused", viaApp.Message, StringComparison.Ordinal);
            Assert.StartsWith("db via jump host edge: authentication failed — automatic retries paused", viaDb.Message, StringComparison.Ordinal);
            Assert.All(new[] { viaApp, viaDb }, exception =>
            {
                Assert.True(exception.IsAuthFailure);
                Assert.True(exception.AutomaticRetriesPaused);
                Assert.DoesNotContain("unreachable", exception.Message, StringComparison.OrdinalIgnoreCase);
            });
        }

        Assert.Equal(Limit, lab.Transports.Attempts(edge));
        Assert.Equal(0, lab.Transports.Attempts(app));
        Assert.Equal(0, lab.Transports.Attempts(db));
        Assert.Null(lab.Reachability.Get(app.Id));
    }

    [Fact]
    public void HealthChecksAndEveryOtherCallerSeeAPausedHostAsBlockedForever()
    {
        var clock = new ManualClock();
        var tracker = new HostReachabilityTracker(clock);
        var id = Guid.NewGuid();
        for (var failure = 0; failure < Limit; failure++)
        {
            tracker.RecordFailure(id, new SshAuthFailedException(SshAuthFailure.ServerRejectedKey));
            clock.Advance(TimeSpan.FromMinutes(1));
        }

        // Health checks ask IsBlocked (D-032): paused is blocked however long it has been.
        foreach (var step in new[] { TimeSpan.FromHours(1), TimeSpan.FromDays(2), TimeSpan.FromDays(400) })
        {
            clock.Advance(step);
            Assert.True(tracker.IsBlocked(id, out var state));
            Assert.True(state!.AutomaticRetriesPaused);
            Assert.False(tracker.TryBeginAttempt(id, bypassBackoff: false, out _));
        }
    }

    // ---- The owner's resets ----

    [Fact]
    public async Task RetryNowMakesExactlyOneAttemptPerPressAndStaysPausedWhenItFailsAgain()
    {
        var clock = new ManualClock();
        using var lab = NewLab(clock);
        var host = lab.Add("srv", "10.0.0.1", script => script.ConnectFails = Rejected);
        await PauseAsync(lab, clock, host);
        var paused = lab.Reachability.Get(host.Id)!;

        for (var press = 1; press <= 3; press++)
        {
            clock.Advance(TimeSpan.FromMinutes(5));
            await Assert.ThrowsAsync<HostUnreachableException>(() => lab.Run(host, Short, bypass: true));
            Assert.Equal(Limit + press, lab.Transports.Attempts(host));

            var state = lab.Reachability.Get(host.Id)!;
            Assert.True(state.AutomaticRetriesPaused);
            Assert.Equal(Limit, state.ConsecutiveFailures);
            Assert.Equal(paused.UnreachableSinceUtc, state.UnreachableSinceUtc);
        }

        // The press did not re-arm anything automatic.
        await AssertNoAutomaticAttemptAsync(lab, clock, host, Limit + 3);
    }

    [Fact]
    public async Task RetryNowThatSucceedsClearsThePauseAndGivesThreeAttemptsAgain()
    {
        var clock = new ManualClock();
        using var lab = NewLab(clock);
        var host = lab.Add("srv", "10.0.0.1", script => script.ConnectFails = Rejected);
        await PauseAsync(lab, clock, host);

        lab.Transports.Script(host.Host).ConnectFails = null;
        await lab.Run(host, Short, bypass: true);

        Assert.Equal(Limit + 1, lab.Transports.Attempts(host));
        Assert.Null(lab.Reachability.Get(host.Id));
        await lab.Run(host, Short);
        Assert.Equal(Limit + 1, lab.Transports.Attempts(host)); // the pooled client is used

        // A later failure starts the count from one, not from the cap.
        lab.Pool.Evict(host.Id);
        lab.Transports.Script(host.Host).ConnectFails = Rejected;
        await PauseAsync(lab, clock, host);
    }

    [Fact]
    public async Task TestConnectionMakesExactlyOneAttemptAndStaysPausedWhenItFailsAgain()
    {
        var clock = new ManualClock();
        using var lab = NewLab(clock);
        var host = lab.Add("srv", "10.0.0.1", script => script.ConnectFails = Rejected);
        var service = new ServerCapabilityService(lab.Runner, lab.Servers, lab.Time);
        await PauseAsync(lab, clock, host);

        for (var press = 1; press <= 2; press++)
        {
            clock.Advance(TimeSpan.FromMinutes(10));
            var result = await service.TestAsync(host.Id, CancellationToken.None);

            Assert.False(result.Reachable);
            // The test runs four commands, but the first failure ends it: one login attempt per press.
            Assert.Equal(Limit + press, lab.Transports.Attempts(host));
            Assert.Contains("authentication failed — automatic retries paused", result.Error, StringComparison.Ordinal);
            Assert.True(lab.Reachability.Get(host.Id)!.AutomaticRetriesPaused);
        }

        await AssertNoAutomaticAttemptAsync(lab, clock, host, Limit + 2);
    }

    [Fact]
    public async Task TestConnectionThatSucceedsClearsThePause()
    {
        var clock = new ManualClock();
        using var lab = NewLab(clock);
        var host = lab.Add("srv", "10.0.0.1", script => script.ConnectFails = Rejected);
        await PauseAsync(lab, clock, host);

        lab.Transports.Script(host.Host).ConnectFails = null;
        var result = await new ServerCapabilityService(lab.Runner, lab.Servers, lab.Time).TestAsync(host.Id, CancellationToken.None);

        Assert.True(result.Reachable, result.Error);
        Assert.Equal(Limit + 1, lab.Transports.Attempts(host));
        Assert.Null(lab.Reachability.Get(host.Id));
    }

    [Theory]
    [InlineData("host")]
    [InlineData("port")]
    [InlineData("user")]
    [InlineData("key file")]
    [InlineData("passphrase")]
    [InlineData("jump server")]
    public async Task EditingAConnectionFieldClearsThePauseFullyAndAllowsThreeAttemptsAgain(string field)
    {
        var clock = new ManualClock();
        using var lab = NewLab(clock);
        var host = lab.Add("srv", "10.0.0.1", script => script.ConnectFails = Rejected);
        var other = lab.Add("other", "10.0.0.9");
        var service = new ServerService(lab.Servers, new Protector(), new Keys("key", "key2"), lab.Pool);
        await PauseAsync(lab, clock, host);

        await service.UpdateAsync(host.Id, Input(host, field, other.Id), CancellationToken.None);

        Assert.Null(lab.Reachability.Get(host.Id));
        // The edited server is attempted at once, and three times a minute apart, before it is paused again.
        lab.Transports.Script(host.Host).ConnectFails = Rejected;
        lab.Transports.Script("10.0.0.2").ConnectFails = Rejected;
        await PauseAsync(lab, clock, host);
    }

    [Fact]
    public async Task RenamingAServerDoesNotClearThePause()
    {
        var clock = new ManualClock();
        using var lab = NewLab(clock);
        var host = lab.Add("srv", "10.0.0.1", script => script.ConnectFails = Rejected);
        var service = new ServerService(lab.Servers, new Protector(), new Keys("key", "key2"), lab.Pool);
        await PauseAsync(lab, clock, host);

        await service.UpdateAsync(host.Id, Input(host, "name"), CancellationToken.None);
        await service.UpdateAsync(host.Id, Input(host, "group"), CancellationToken.None);

        Assert.True(lab.Reachability.Get(host.Id)!.AutomaticRetriesPaused);
        await AssertNoAutomaticAttemptAsync(lab, clock, host, Limit);
    }

    [Fact]
    public async Task EditingAJumpHostClearsThePauseOfItsDependentsToo()
    {
        var clock = new ManualClock();
        using var lab = NewLab(clock);
        var edge = lab.Add("edge", "10.0.0.1", script => script.ConnectFails = Rejected);
        var app = lab.Add("app", "10.0.0.2", jump: edge);
        var service = new ServerService(lab.Servers, new Protector(), new Keys("key", "key2"), lab.Pool);
        await PauseAsync(lab, clock, edge);

        await service.UpdateAsync(edge.Id, Input(edge, "key file"), CancellationToken.None);

        Assert.Null(lab.Reachability.Get(edge.Id));
        Assert.Null(ServerReachability.Status(app.Id, lab.Map(), lab.Reachability));
    }

    // ---- What breaks "consecutive" ----

    [Fact]
    public async Task ASuccessClearsTheCountSoThreeMoreAutomaticAttemptsAreAvailable()
    {
        var clock = new ManualClock();
        using var lab = NewLab(clock);
        var host = lab.Add("srv", "10.0.0.1", script => script.ConnectFails = Rejected);

        // Two failures, then the key works for a moment (a success of any kind), then it fails again.
        await Assert.ThrowsAsync<HostUnreachableException>(() => lab.Run(host, Short));
        clock.Advance(TimeSpan.FromMinutes(1));
        await Assert.ThrowsAsync<HostUnreachableException>(() => lab.Run(host, Short));
        lab.Transports.Script(host.Host).ConnectFails = null;
        clock.Advance(TimeSpan.FromMinutes(1));
        await lab.Run(host, Short);
        Assert.Null(lab.Reachability.Get(host.Id));

        lab.Pool.Evict(host.Id); // drop the pooled client, so the next call has to log in again
        lab.Transports.Script(host.Host).ConnectFails = Rejected;
        await PauseAsync(lab, clock, host);
    }

    [Fact]
    public void ANonAuthFailureInBetweenBreaksTheRunOfConsecutiveAuthFailures()
    {
        var clock = new ManualClock();
        var tracker = new HostReachabilityTracker(clock);
        var id = Guid.NewGuid();

        tracker.RecordFailure(id, new SshAuthFailedException(SshAuthFailure.ServerRejectedKey));
        tracker.RecordFailure(id, new SshAuthFailedException(SshAuthFailure.ServerRejectedKey));
        // A connect failure (the host is down) in between: the login failures were not consecutive.
        var down = tracker.RecordFailure(id, new SocketException((int)SocketError.TimedOut));
        Assert.Equal(HostFailureKind.Connect, down.Kind);
        var again = tracker.RecordFailure(id, new SshAuthFailedException(SshAuthFailure.ServerRejectedKey));
        Assert.Equal(1, again.ConsecutiveFailures);
        Assert.False(again.AutomaticRetriesPaused);
        tracker.RecordFailure(id, new SshAuthFailedException(SshAuthFailure.ServerRejectedKey));
        Assert.False(tracker.Get(id)!.AutomaticRetriesPaused);
        Assert.True(tracker.RecordFailure(id, new SshAuthFailedException(SshAuthFailure.ServerRejectedKey)).AutomaticRetriesPaused);

        // The same for a command-timeout backoff: the count starts again.
        var timeouts = Guid.NewGuid();
        tracker.RecordFailure(timeouts, new SshAuthFailedException(SshAuthFailure.ServerRejectedKey));
        tracker.RecordFailure(timeouts, new SshAuthFailedException(SshAuthFailure.ServerRejectedKey));
        tracker.Reset(timeouts);
        for (var count = 0; count < IHostReachability.CommandTimeoutThreshold; count++)
            tracker.RecordCommandTimeout(timeouts, new TimeoutException());
        Assert.Equal(HostFailureKind.CommandTimeout, tracker.Get(timeouts)!.Kind);
        var login = tracker.RecordFailure(timeouts, new SshAuthFailedException(SshAuthFailure.ServerRejectedKey));
        Assert.Equal(1, login.ConsecutiveFailures);
        Assert.False(login.AutomaticRetriesPaused);
    }

    [Fact]
    public void ASuccessfulConnectionClearsTheCountAtTheTrackerToo()
    {
        var tracker = new HostReachabilityTracker(new ManualClock());
        var id = Guid.NewGuid();
        tracker.RecordFailure(id, new SshAuthFailedException(SshAuthFailure.ServerRejectedKey));
        tracker.RecordFailure(id, new SshAuthFailedException(SshAuthFailure.ServerRejectedKey));

        tracker.RecordSuccess(id);

        Assert.Null(tracker.Get(id));
        Assert.Equal(1, tracker.RecordFailure(id, new SshAuthFailedException(SshAuthFailure.ServerRejectedKey)).ConsecutiveFailures);
    }

    [Fact]
    public async Task CountsArePerHostAndOnePausedHostDoesNotHoldBackAnother()
    {
        var clock = new ManualClock();
        using var lab = NewLab(clock);
        var first = lab.Add("one", "10.0.0.1", script => script.ConnectFails = Rejected);
        var second = lab.Add("two", "10.0.0.2", script => script.ConnectFails = Rejected);

        await PauseAsync(lab, clock, first);
        // The second host has not failed yet: it is not paused, and gets its own three attempts.
        Assert.Null(lab.Reachability.Get(second.Id));
        await PauseAsync(lab, clock, second);

        Assert.Equal(Limit, lab.Transports.Attempts(first));
        Assert.Equal(Limit, lab.Transports.Attempts(second));
        await AssertNoAutomaticAttemptAsync(lab, clock, first, Limit);
        await AssertNoAutomaticAttemptAsync(lab, clock, second, Limit);
    }

    [Fact]
    public void ARestartForgetsThePauseBecauseTheStateIsInMemory()
    {
        var clock = new ManualClock();
        var before = new HostReachabilityTracker(clock);
        var id = Guid.NewGuid();
        for (var failure = 0; failure < Limit; failure++)
            before.RecordFailure(id, new SshAuthFailedException(SshAuthFailure.ServerRejectedKey));
        Assert.True(before.Get(id)!.AutomaticRetriesPaused);

        var after = new HostReachabilityTracker(clock);

        Assert.Null(after.Get(id));
        Assert.True(after.TryBeginAttempt(id, bypassBackoff: false, out _));
    }

    // ---- The Servers page ----

    [Fact]
    public async Task TheServersPageSaysAuthFailedAutomaticRetriesPausedAndKeepsTheCause()
    {
        var clock = new ManualClock();
        using var lab = NewLab(clock);
        var edge = lab.Add("edge", "10.0.0.1", script => script.ConnectFails = () => new FileNotFoundException("no such key"));
        var app = lab.Add("app", "10.0.0.2", jump: edge);

        // Before the limit: the old badge and a next retry.
        await Assert.ThrowsAsync<HostUnreachableException>(() => lab.Run(edge, Short));
        var early = ServerReachability.Status(edge.Id, lab.Map(), lab.Reachability)!;
        Assert.Equal("Auth failed", early.Badge);
        Assert.False(early.AutomaticRetriesPaused);
        Assert.Contains("next retry", early.Text, StringComparison.Ordinal);

        clock.Advance(TimeSpan.FromMinutes(1));
        await Assert.ThrowsAsync<HostUnreachableException>(() => lab.Run(edge, Short));
        clock.Advance(TimeSpan.FromMinutes(1));
        await Assert.ThrowsAsync<HostUnreachableException>(() => lab.Run(edge, Short));

        var own = ServerReachability.Status(edge.Id, lab.Map(), lab.Reachability)!;
        Assert.Equal(HostFailureKind.AuthFailed, own.Kind);
        Assert.True(own.AutomaticRetriesPaused);
        Assert.Equal("Auth failed — automatic retries paused", own.Badge);
        Assert.StartsWith("edge: authentication failed — automatic retries paused (key file not found). Since ", own.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("next retry", own.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("unreachable", own.Text, StringComparison.OrdinalIgnoreCase);

        var behind = ServerReachability.Status(app.Id, lab.Map(), lab.Reachability)!;
        Assert.Equal("Auth failed — automatic retries paused", behind.Badge);
        Assert.StartsWith("app via jump host edge: authentication failed — automatic retries paused (key file not found).", behind.Text, StringComparison.Ordinal);

        // A host that is merely down keeps its own badge.
        var down = Guid.NewGuid();
        var tracker = new HostReachabilityTracker(clock);
        tracker.RecordFailure(down, new SocketException((int)SocketError.TimedOut));
        var inventory = new Dictionary<Guid, (string Name, Guid? JumpServerId)> { [down] = ("down", null) };
        Assert.Equal("Unreachable", ServerReachability.Status(down, inventory, tracker)!.Badge);
    }

    // ---- Helpers ----

    private static ServerInput Input(Server server, string change, Guid? jumpTarget = null) => new(
        change == "name" ? "renamed" : server.Name,
        change == "host" ? "10.0.0.2" : server.Host,
        change == "port" ? 2222 : server.Port,
        change == "user" ? "other-user" : server.User,
        change == "key file" ? "key2" : server.KeyFile,
        change == "passphrase" ? "a-new-passphrase" : null,
        change == "jump server" ? jumpTarget : server.JumpServerId,
        change == "group" ? "production-like" : server.Group,
        []);

    private sealed class Protector : ISecretProtector
    {
        public string Protect(string plaintext) => $"protected:{plaintext}";
        public string Unprotect(string protectedValue) => protectedValue["protected:".Length..];
    }

    private sealed class Keys(params string[] files) : IServerKeyStore
    {
        public IReadOnlyList<string> ListFileNames() => files;
        public bool Contains(string fileName) => files.Contains(fileName, StringComparer.Ordinal);
        public string GetFullPath(string fileName) => Path.Combine(Path.GetTempPath(), fileName);
    }

    private sealed class ProbeCollector : ICollector
    {
        public string Name => "probe";
        public Task<bool> IsApplicableAsync(ServerContext context, CancellationToken cancellationToken) => Task.FromResult(true);

        public async Task<CollectorOutput> CollectAsync(ServerContext context, CancellationToken cancellationToken)
        {
            var result = await context.SshRunner.RunAsync(context.Server.Id, "true", new SshRunOptions(Short), cancellationToken);
            return new CollectorOutput(result.StandardOutput, string.Empty, 0, TimeSpan.Zero);
        }

        public CollectorResult Parse(CollectorOutput output) => new();
    }

    private sealed class RecordingSnapshots : ISnapshotRepository
    {
        private readonly List<IReadOnlyList<CollectorRun>> _runs = [];
        public IReadOnlyList<IReadOnlyList<CollectorRun>> Runs => _runs;
        public Task<Snapshot?> GetLatestAsync(CancellationToken cancellationToken) => Task.FromResult<Snapshot?>(null);
        public Task<Snapshot?> GetLatestRunAsync(CancellationToken cancellationToken) => Task.FromResult<Snapshot?>(null);
        public Task<IReadOnlyList<Snapshot>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Snapshot>>([]);
        public Task<Snapshot> CreateAsync(IReadOnlyCollection<Guid> serverIds, DateTimeOffset startedAtUtc, CancellationToken cancellationToken) =>
            Task.FromResult(new Snapshot(serverIds, startedAtUtc));
        public Task CompleteAsync(Snapshot snapshot, IReadOnlyCollection<CollectorRun> collectorRuns, DateTimeOffset finishedAtUtc,
            int succeededServerCount, int failedServerCount, bool cancelled, CancellationToken cancellationToken)
        {
            _runs.Add(collectorRuns.ToArray());
            return Task.CompletedTask;
        }

        public Task FailAsync(Snapshot snapshot, IReadOnlyCollection<CollectorRun> collectorRuns, DateTimeOffset finishedAtUtc,
            int succeededServerCount, int failedServerCount, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class NoGraphs : IGraphRepository
    {
        public Task SaveAsync(Guid snapshotId, GraphBuildResult graph, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task MergeAsync(Guid snapshotId, Guid previousSnapshotId, GraphBuildResult graph, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<GraphSnapshotDto?> GetAsync(Guid snapshotId, GraphView view, CancellationToken cancellationToken) => Task.FromResult<GraphSnapshotDto?>(null);
    }
}
