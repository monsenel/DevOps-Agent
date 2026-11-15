using System.Diagnostics;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Monitoring;
using DevOpsAgent.Application.Servers;
using DevOpsAgent.Infrastructure.Ssh;
using Renci.SshNet.Common;

namespace DevOpsAgent.Tests;

/// <summary>
/// T7.R17: a host that connects but whose commands hang enters the same backoff as an unreachable host (1 minute doubling
/// to 15), after <see cref="IHostReachability.CommandTimeoutThreshold"/> consecutive command timeouts.
/// </summary>
public sealed class CommandTimeoutBackoffTests
{
    private static readonly TimeSpan Probe = TimeSpan.FromMilliseconds(120);

    [Fact]
    public void TheThresholdIsOneMoreThanTheTimeoutsThatRecycleAClient()
    {
        Assert.Equal(3, SshTimeoutTracker.DefaultThreshold);
        Assert.Equal(SshTimeoutTracker.DefaultThreshold + 1, IHostReachability.CommandTimeoutThreshold);
    }

    [Fact]
    public async Task FourConsecutiveCommandTimeoutsBackTheHostOffAndLaterCallersAreSkippedAtOnce()
    {
        using var lab = new HangLab(connectHeadroom: HangLab.ConnectHeadroom);
        var hung = lab.Add("hung", "10.0.0.9", script => script.Commands = CommandMode.HangHonouringToken);

        // 1-3: three timeouts in a row recycle the client (R15) but do not yet back the host off, so the command that follows
        // still gets a fresh connection and a chance to answer.
        for (var timeout = 1; timeout <= SshTimeoutTracker.DefaultThreshold; timeout++)
        {
            await Assert.ThrowsAsync<SshOperationTimeoutException>(() => lab.Run(hung, Probe));
        }

        Assert.Null(lab.Reachability.Get(hung.Id));
        var first = Assert.Single(lab.Transports.CreatedFor(hung));
        Assert.True(first.Disposed);

        // 4: the fresh connection does not answer either: now the host itself is the problem.
        await Assert.ThrowsAsync<SshOperationTimeoutException>(() => lab.Run(hung, Probe));
        var state = lab.Reachability.Get(hung.Id)!;
        Assert.Equal(HostFailureKind.CommandTimeout, state.Kind);
        Assert.Equal(1, state.ConsecutiveFailures);
        Assert.Equal(HostReachabilityTracker.InitialBackoff, state.NextRetryUtc - lab.Time.GetUtcNow());
        Assert.Equal(2, lab.Transports.CreatedFor(hung).Count);
        Assert.True(lab.Transports.CreatedFor(hung)[1].Disposed, "the client that proved the host unresponsive is retired too");

        // 5: skipped like an unreachable host: no connection, no command, immediately.
        var commands = lab.Transports.Commands(hung);
        var attempts = lab.Transports.Attempts(hung);
        var stopwatch = Stopwatch.StartNew();
        var skipped = await Assert.ThrowsAsync<HostUnreachableException>(() => lab.Run(hung, TimeSpan.FromSeconds(5)));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(300), $"took {stopwatch.ElapsedMilliseconds} ms");
        Assert.Equal(commands, lab.Transports.Commands(hung));
        Assert.Equal(attempts, lab.Transports.Attempts(hung));
        Assert.Equal(HostFailureKind.CommandTimeout, skipped.Kind);
        // The message names the cause: commands timed out, not "could not connect".
        Assert.Contains("hung is unreachable (since", skipped.Message, StringComparison.Ordinal);
        Assert.Contains("commands timed out", skipped.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("could not connect", skipped.Message, StringComparison.Ordinal);
        Assert.Equal(0, lab.Pool.ActiveLeases);
    }

    [Fact]
    public async Task ASuccessfulCommandResetsTheCount()
    {
        using var lab = new HangLab(connectHeadroom: HangLab.ConnectHeadroom);
        var host = lab.Add("flaky", "10.0.0.8", script => script.Commands = CommandMode.HangHonouringToken);
        var script = lab.Transports.Script(host.Host);

        for (var timeout = 1; timeout <= 3; timeout++) await Assert.ThrowsAsync<SshOperationTimeoutException>(() => lab.Run(host, Probe));
        script.Commands = CommandMode.Ok;
        await lab.Run(host, TimeSpan.FromSeconds(5));
        script.Commands = CommandMode.HangHonouringToken;

        // Three more in a row are still only three.
        for (var timeout = 1; timeout <= 3; timeout++) await Assert.ThrowsAsync<SshOperationTimeoutException>(() => lab.Run(host, Probe));
        Assert.Null(lab.Reachability.Get(host.Id));

        // The fourth consecutive timeout since the last success is what trips it.
        await Assert.ThrowsAsync<SshOperationTimeoutException>(() => lab.Run(host, Probe));
        Assert.Equal(HostFailureKind.CommandTimeout, lab.Reachability.Get(host.Id)!.Kind);
    }

    [Fact]
    public async Task TheBackoffDoublesLikeAConnectFailureAndTheFirstAnsweredCommandEndsIt()
    {
        var clock = new ManualClock();
        using var lab = new HangLab(clock, HangLab.ConnectHeadroom);
        var host = lab.Add("slow", "10.0.0.7", script => script.Commands = CommandMode.HangHonouringToken);
        for (var timeout = 1; timeout <= IHostReachability.CommandTimeoutThreshold; timeout++)
            await Assert.ThrowsAsync<SshOperationTimeoutException>(() => lab.Run(host, Probe));
        Assert.Equal(TimeSpan.FromMinutes(1), lab.Reachability.Get(host.Id)!.NextRetryUtc - clock.GetUtcNow());

        // Still inside the backoff window: skipped.
        clock.Advance(TimeSpan.FromSeconds(30));
        await Assert.ThrowsAsync<HostUnreachableException>(() => lab.Run(host, Probe));

        // Window over: the retry (one command is enough this time) fails, so the next window is twice as long.
        clock.Advance(TimeSpan.FromSeconds(31));
        await Assert.ThrowsAsync<SshOperationTimeoutException>(() => lab.Run(host, Probe));
        var second = lab.Reachability.Get(host.Id)!;
        Assert.Equal(2, second.ConsecutiveFailures);
        Assert.Equal(TimeSpan.FromMinutes(2), second.NextRetryUtc - clock.GetUtcNow());

        // The host recovers: the retry's command succeeds and the backoff is gone.
        lab.Transports.Script(host.Host).Commands = CommandMode.Ok;
        clock.Advance(TimeSpan.FromMinutes(2));
        await lab.Run(host, TimeSpan.FromSeconds(5));
        Assert.Null(lab.Reachability.Get(host.Id));
        await lab.Run(host, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RetryNowAttemptsDuringTheBackoffAndClearsItOnceTheHostAnswers()
    {
        using var lab = new HangLab(connectHeadroom: HangLab.ConnectHeadroom);
        var host = lab.Add("web", "10.0.0.6", script => script.Commands = CommandMode.HangHonouringToken);
        for (var timeout = 1; timeout <= IHostReachability.CommandTimeoutThreshold; timeout++)
            await Assert.ThrowsAsync<SshOperationTimeoutException>(() => lab.Run(host, Probe));
        await Assert.ThrowsAsync<HostUnreachableException>(() => lab.Run(host, Probe));

        lab.Transports.Script(host.Host).Commands = CommandMode.Ok;
        // "Retry now" is the capability test, which bypasses the backoff for its whole route.
        var result = await new ServerCapabilityService(lab.Runner, lab.Servers, lab.Time).TestAsync(host.Id, CancellationToken.None);

        Assert.True(result.Reachable, result.Error);
        Assert.Null(lab.Reachability.Get(host.Id));
        await lab.Run(host, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ARetryNowThatStillTimesOutExtendsTheBackoff()
    {
        using var lab = new HangLab(connectHeadroom: HangLab.ConnectHeadroom);
        var host = lab.Add("web", "10.0.0.6", script => script.Commands = CommandMode.HangHonouringToken);
        for (var timeout = 1; timeout <= IHostReachability.CommandTimeoutThreshold; timeout++)
            await Assert.ThrowsAsync<SshOperationTimeoutException>(() => lab.Run(host, Probe));

        await Assert.ThrowsAsync<SshOperationTimeoutException>(() => lab.Run(host, Probe, bypass: true));

        Assert.Equal(2, lab.Reachability.Get(host.Id)!.ConsecutiveFailures);
    }

    [Fact]
    public async Task EditingTheServerResetsTheBackoffAndTheCount()
    {
        using var lab = new HangLab(connectHeadroom: HangLab.ConnectHeadroom);
        var host = lab.Add("web", "10.0.0.6", script => script.Commands = CommandMode.HangHonouringToken);
        for (var timeout = 1; timeout <= IHostReachability.CommandTimeoutThreshold; timeout++)
            await Assert.ThrowsAsync<SshOperationTimeoutException>(() => lab.Run(host, Probe));
        Assert.NotNull(lab.Reachability.Get(host.Id));

        // The host edit path (ServerService.UpdateAsync) evicts the pooled connection.
        lab.Pool.Evict(host.Id);

        Assert.Null(lab.Reachability.Get(host.Id));
        // The count starts again: three timeouts after the edit do not back it off.
        for (var timeout = 1; timeout <= 3; timeout++) await Assert.ThrowsAsync<SshOperationTimeoutException>(() => lab.Run(host, Probe));
        Assert.Null(lab.Reachability.Get(host.Id));
    }

    [Fact]
    public void ConnectingToAHostDoesNotEndACommandTimeoutBackoffButAnAnsweredCommandDoes()
    {
        var clock = new ManualClock();
        var tracker = new HostReachabilityTracker(clock);
        var id = Guid.NewGuid();
        var timeout = new SshOperationTimeoutException("timed out");
        for (var count = 1; count < IHostReachability.CommandTimeoutThreshold; count++) Assert.Null(tracker.RecordCommandTimeout(id, timeout));
        Assert.NotNull(tracker.RecordCommandTimeout(id, timeout));

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(tracker.TryBeginAttempt(id, bypassBackoff: false, out _));
        Assert.True(tracker.Get(id)!.RetryInProgress);
        tracker.RecordSuccess(id); // the connect worked

        var afterConnect = tracker.Get(id)!;
        Assert.Equal(HostFailureKind.CommandTimeout, afterConnect.Kind);
        Assert.False(afterConnect.RetryInProgress);

        tracker.RecordCommandSuccess(id);
        Assert.Null(tracker.Get(id));
    }

    [Fact]
    public void ATimeoutThatStartedBeforeTheBackoffDoesNotEscalateIt()
    {
        var clock = new ManualClock();
        var tracker = new HostReachabilityTracker(clock);
        var id = Guid.NewGuid();
        var timeout = new SshOperationTimeoutException("timed out");
        for (var count = 1; count <= IHostReachability.CommandTimeoutThreshold; count++) tracker.RecordCommandTimeout(id, timeout);
        var state = tracker.Get(id)!;

        // Parallel callers whose commands were already running when the host was backed off report in afterwards.
        Assert.Null(tracker.RecordCommandTimeout(id, timeout));
        Assert.Same(state, tracker.Get(id));

        // A connection failure is a stronger statement and replaces it: the cause reads "could not connect".
        var connect = tracker.RecordFailure(id, new System.Net.Sockets.SocketException());
        Assert.Equal(HostFailureKind.Connect, connect.Kind);
        Assert.Contains("could not connect", HostUnreachableException.Describe("web", connect), StringComparison.Ordinal);
        Assert.Null(tracker.RecordCommandTimeout(id, timeout));
    }

    [Fact]
    public void MessagesNameTheCauseAndTheHopThatFailed()
    {
        var now = DateTimeOffset.UtcNow;
        var connect = new HostReachabilityState(Guid.NewGuid(), now, now.AddMinutes(1), 1, "x", false, null);
        var commands = connect with { Kind = HostFailureKind.CommandTimeout };

        Assert.EndsWith(": could not connect.", HostUnreachableException.Describe("app", connect), StringComparison.Ordinal);
        Assert.EndsWith(": commands timed out.", HostUnreachableException.Describe("app", commands), StringComparison.Ordinal);
        Assert.Contains("app is unreachable via jump host edge (since", HostUnreachableException.Describe("app", commands, "edge"), StringComparison.Ordinal);
        Assert.EndsWith(": commands on edge timed out.", HostUnreachableException.Describe("app", commands, "edge"), StringComparison.Ordinal);
        Assert.EndsWith(": could not connect to edge.", HostUnreachableException.Describe("app", connect, "edge"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AServerBehindAJumpHostWhoseCommandsTimeOutIsSkippedAndTheMessageNamesTheJumpHost()
    {
        using var lab = new HangLab(connectHeadroom: HangLab.ConnectHeadroom);
        var edge = lab.Add("edge", "10.0.0.1", script => script.Commands = CommandMode.HangHonouringToken);
        var app = lab.Add("app", "10.0.0.2", jump: edge);
        for (var timeout = 1; timeout <= IHostReachability.CommandTimeoutThreshold; timeout++)
            await Assert.ThrowsAsync<SshOperationTimeoutException>(() => lab.Run(edge, Probe));

        var attempts = lab.Transports.Attempts(app);
        var exception = await Assert.ThrowsAsync<HostUnreachableException>(() => lab.Run(app, TimeSpan.FromSeconds(5)));

        Assert.Contains("app is unreachable via jump host edge", exception.Message, StringComparison.Ordinal);
        Assert.Contains("commands on edge timed out", exception.Message, StringComparison.Ordinal);
        Assert.Equal(attempts, lab.Transports.Attempts(app));
    }
}
