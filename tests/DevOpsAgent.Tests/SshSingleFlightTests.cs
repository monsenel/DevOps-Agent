using System.Diagnostics;
using System.Net.Sockets;
using DevOpsAgent.Application.Abstractions;
using Renci.SshNet.Common;

namespace DevOpsAgent.Tests;

/// <summary>
/// T7.R19: callers that arrive while the first connect to a host is running share that one attempt and its outcome, however
/// many they are; "Retry now" callers keep their own attempt after a failure; a caller's deadline or cancellation only ends
/// its own wait. The connect is held open by a gate the test controls, so nothing depends on timing except where a test says
/// it measures it.
/// </summary>
public sealed class SshSingleFlightTests
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(60);

    private static Func<Exception> TimedOut => () => new SocketException((int)SocketError.TimedOut);

    [Fact]
    public async Task TenConcurrentFirstCallsShareOneFailingConnectAndAllGetTheSameFailureTogether()
    {
        using var lab = new HangLab();
        var gate = new TaskCompletionSource();
        var dead = lab.Add("dead", "192.0.2.1", script => { script.ConnectGate = gate.Task; script.ConnectFails = TimedOut; });

        var calls = Enumerable.Range(0, 10).Select(_ => Start(lab, dead, Long)).ToArray();
        await WaitUntilConnecting(lab, dead, 10);
        Assert.Equal(1, lab.Transports.Attempts(dead));
        Assert.All(calls, call => Assert.False(call.Task.IsCompleted));

        gate.SetResult();
        var results = await Task.WhenAll(calls.Select(call => call.Task));

        var failures = results.Select(result => Assert.IsType<HostUnreachableException>(result.Failure)).ToArray();
        Assert.Equal(1, lab.Transports.Attempts(dead));
        // One outcome, handed to everybody: the very same state, not ten of them.
        Assert.Single(failures.Select(failure => failure.Message).Distinct());
        Assert.Single(failures.Select(failure => failure.NextRetryUtc).Distinct());
        Assert.Equal(1, lab.Reachability.Get(dead.Id)!.ConsecutiveFailures);
        Assert.Equal(0, lab.Pool.ConnectWaiters(dead.Id));
        Assert.Equal(0, lab.Pool.ActiveLeases);
        // Then the backoff applies as always.
        await Assert.ThrowsAsync<HostUnreachableException>(() => lab.Run(dead, Long));
        Assert.Equal(1, lab.Transports.Attempts(dead));
    }

    [Fact]
    public async Task AllCallersOfAFailingConnectFinishWithinOneConnectTimeoutNotOneTimeoutEach()
    {
        using var lab = new HangLab();
        var connectTimeout = TimeSpan.FromMilliseconds(400);
        var dead = lab.Add("dead", "192.0.2.1", script => { script.ConnectDelay = connectTimeout; script.ConnectFails = TimedOut; });

        var stopwatch = Stopwatch.StartNew();
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Start(lab, dead, Long).Task));

        Assert.All(results, result => Assert.IsType<HostUnreachableException>(result.Failure));
        Assert.Equal(1, lab.Transports.Attempts(dead));
        // Ten callers queued one after the other would need ten timeouts (4 s); they all end with the one attempt.
        Assert.True(stopwatch.Elapsed < connectTimeout * 6, $"took {stopwatch.ElapsedMilliseconds} ms");
        Assert.True(results.Max(result => result.Elapsed) - results.Min(result => result.Elapsed) < connectTimeout * 3,
            "the callers did not end together");
    }

    [Fact]
    public async Task TenConcurrentFirstCallsToAHealthyHostOpenOneConnectionAndEachGetsItsOwnLease()
    {
        using var lab = new HangLab();
        var gate = new TaskCompletionSource();
        var host = lab.Add("web", "10.0.0.1", script => script.ConnectGate = gate.Task);

        var calls = Enumerable.Range(0, 10).Select(_ => StartSession(lab, host, Long)).ToArray();
        await WaitUntilConnecting(lab, host, 10);
        gate.SetResult();
        var sessions = await Task.WhenAll(calls);

        Assert.Equal(1, lab.Transports.Attempts(host));
        Assert.Single(lab.Transports.CreatedFor(host));
        Assert.Equal(10, lab.Pool.ActiveLeases);
        Assert.All(sessions, session => Assert.True(session.IsConnected));
        foreach (var session in sessions) session.Dispose();
        Assert.Equal(0, lab.Pool.ActiveLeases);
        Assert.Equal(1, lab.Pool.LiveClients);
    }

    [Fact]
    public async Task ARetryNowCallerAfterTheFailureGetsItsOwnAttemptWhileTheOthersAreStillSkipped()
    {
        using var lab = new HangLab();
        var dead = lab.Add("dead", "192.0.2.1", script => script.ConnectFails = TimedOut);
        await Assert.ThrowsAsync<HostUnreachableException>(() => lab.Run(dead, Long));
        Assert.Equal(1, lab.Transports.Attempts(dead));

        await Assert.ThrowsAsync<HostUnreachableException>(() => lab.Run(dead, Long));
        Assert.Equal(1, lab.Transports.Attempts(dead));

        await Assert.ThrowsAsync<HostUnreachableException>(() => lab.Run(dead, Long, bypass: true));
        Assert.Equal(2, lab.Transports.Attempts(dead));
        Assert.Equal(2, lab.Reachability.Get(dead.Id)!.ConsecutiveFailures);

        // And it does connect when the host is back, backoff or not.
        lab.Transports.Script(dead.Host).ConnectFails = null;
        await lab.Run(dead, Long, bypass: true);
        Assert.Equal(3, lab.Transports.Attempts(dead));
        Assert.Null(lab.Reachability.Get(dead.Id));
    }

    [Fact]
    public async Task RetryNowCallersThatArriveWhileTheFirstConnectRunsShareItBecauseThereIsNoBackoffYetToBypass()
    {
        using var lab = new HangLab();
        var gate = new TaskCompletionSource();
        var dead = lab.Add("dead", "192.0.2.1", script => { script.ConnectGate = gate.Task; script.ConnectFails = TimedOut; });

        var calls = new[] { Start(lab, dead, Long), Start(lab, dead, Long, bypass: true), Start(lab, dead, Long, bypass: true), Start(lab, dead, Long) };
        await WaitUntilConnecting(lab, dead, 4);
        gate.SetResult();
        var results = await Task.WhenAll(calls.Select(call => call.Task));

        Assert.All(results, result => Assert.IsType<HostUnreachableException>(result.Failure));
        Assert.Equal(1, lab.Transports.Attempts(dead));
    }

    [Fact]
    public async Task ConcurrentRetryNowCallersOfAHostInBackoffShareOneAttemptAndKeepItSeparateFromNormalCallers()
    {
        var clock = new ManualClock();
        using var lab = new HangLab(clock);
        var dead = lab.Add("dead", "192.0.2.1", script => script.ConnectFails = TimedOut);
        await Assert.ThrowsAsync<HostUnreachableException>(() => lab.Run(dead, Long));
        var gate = new TaskCompletionSource();
        lab.Transports.Script(dead.Host).ConnectGate = gate.Task;

        var retries = Enumerable.Range(0, 5).Select(_ => Start(lab, dead, Long, bypass: true)).ToArray();
        await WaitUntilConnecting(lab, dead, 5);
        // A normal caller is not part of someone's "Retry now": it is skipped at once, as before.
        var normal = await Assert.ThrowsAsync<HostUnreachableException>(() => lab.Run(dead, Long));
        Assert.Contains("retry in progress", normal.Message, StringComparison.Ordinal);
        gate.SetResult();
        var results = await Task.WhenAll(retries.Select(call => call.Task));

        Assert.All(results, result => Assert.IsType<HostUnreachableException>(result.Failure));
        Assert.Equal(2, lab.Transports.Attempts(dead));
        Assert.Equal(2, lab.Reachability.Get(dead.Id)!.ConsecutiveFailures);
    }

    [Fact]
    public async Task ARetryOfAHostInBackoffIsItsClaimantsAloneAndRetryNowFollowsItWithItsOwnAttempt()
    {
        var clock = new ManualClock();
        using var lab = new HangLab(clock);
        var dead = lab.Add("dead", "192.0.2.1", script => script.ConnectFails = TimedOut);
        await Assert.ThrowsAsync<HostUnreachableException>(() => lab.Run(dead, Long));
        clock.Advance(TimeSpan.FromMinutes(2));
        var gate = new TaskCompletionSource();
        lab.Transports.Script(dead.Host).ConnectGate = gate.Task;

        var claimant = Start(lab, dead, Long);
        await WaitUntilConnecting(lab, dead, 1);
        var stopwatch = Stopwatch.StartNew();
        var skipped = await Assert.ThrowsAsync<HostUnreachableException>(() => lab.Run(dead, Long));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), "a normal caller must not wait for someone else's retry");
        Assert.Contains("retry in progress", skipped.Message, StringComparison.Ordinal);

        // "Retry now" does not share a retry that is not its own: it waits for it to end and then attempts itself.
        var retryNow = Start(lab, dead, Long, bypass: true);
        await Task.Delay(100);
        Assert.False(retryNow.Task.IsCompleted);
        gate.SetResult();
        await claimant.Task;
        var result = await retryNow.Task;

        Assert.IsType<HostUnreachableException>(result.Failure);
        Assert.Equal(3, lab.Transports.Attempts(dead));
    }

    [Fact]
    public async Task ACallerWithAShorterDeadlineReturnsAtItsOwnDeadlineWhileTheSharedAttemptContinues()
    {
        using var lab = new HangLab();
        var gate = new TaskCompletionSource();
        var host = lab.Add("web", "10.0.0.1", script => script.ConnectGate = gate.Task);

        var patient = Enumerable.Range(0, 3).Select(_ => StartSession(lab, host, Long)).ToArray();
        await WaitUntilConnecting(lab, host, 3);
        var impatient = Start(lab, host, TimeSpan.FromMilliseconds(250));

        var result = await impatient.Task.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.IsType<SshOperationTimeoutException>(result.Failure);
        Assert.True(result.Elapsed >= TimeSpan.FromMilliseconds(200) && result.Elapsed < TimeSpan.FromSeconds(10), $"{result.Elapsed.TotalMilliseconds:0} ms");
        // The attempt was not cut with it: the patient callers still wait for it, and get their connection.
        Assert.All(patient, call => Assert.False(call.IsCompleted));
        Assert.Null(lab.Reachability.Get(host.Id));
        gate.SetResult();
        var sessions = await Task.WhenAll(patient);
        Assert.Equal(1, lab.Transports.Attempts(host));
        Assert.All(sessions, session => Assert.True(session.IsConnected));
        foreach (var session in sessions) session.Dispose();
    }

    [Fact]
    public async Task AShortDeadlineCallerThatStartedTheAttemptDoesNotEndItForTheOthers()
    {
        using var lab = new HangLab();
        var gate = new TaskCompletionSource();
        var host = lab.Add("web", "10.0.0.1", script => script.ConnectGate = gate.Task);

        // The impatient caller is the one that starts the connect; the patient ones join it.
        var impatient = Start(lab, host, TimeSpan.FromSeconds(3));
        await WaitUntilConnecting(lab, host, 1);
        var patient = Enumerable.Range(0, 4).Select(_ => StartSession(lab, host, Long)).ToArray();
        await WaitUntilConnecting(lab, host, 5);

        var result = await impatient.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.IsType<SshOperationTimeoutException>(result.Failure);
        // Before: the starter's deadline cut the attempt, backed the host off and failed everybody.
        Assert.Null(lab.Reachability.Get(host.Id));
        gate.SetResult();
        var sessions = await Task.WhenAll(patient);

        Assert.Equal(1, lab.Transports.Attempts(host));
        Assert.Null(lab.Reachability.Get(host.Id));
        Assert.All(sessions, session => Assert.True(session.IsConnected));
        foreach (var session in sessions) session.Dispose();
    }

    [Fact]
    public async Task WhenEveryCallersDeadlinePassesTheAttemptIsCutAndCountsAsAFailedConnectForTheLastOne()
    {
        using var lab = new HangLab();
        var host = lab.Add("web", "10.0.0.1", script => script.ConnectGate = new TaskCompletionSource().Task);

        var stopwatch = Stopwatch.StartNew();
        // The caller with the longest deadline starts the attempt, so it cannot end before the others have joined it.
        var last = Start(lab, host, TimeSpan.FromSeconds(3));
        await WaitUntilConnecting(lab, host, 1);
        var first = Start(lab, host, TimeSpan.FromMilliseconds(600));
        var second = Start(lab, host, TimeSpan.FromMilliseconds(1200));

        var results = await Task.WhenAll(first.Task, second.Task, last.Task);

        // Each stops at its own deadline; only the last one still waiting ends the attempt and sees the failure it caused.
        Assert.IsType<SshOperationTimeoutException>(results[0].Failure);
        Assert.IsType<SshOperationTimeoutException>(results[1].Failure);
        Assert.IsType<HostUnreachableException>(results[2].Failure);
        Assert.True(results[0].Elapsed < results[2].Elapsed);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15), $"{stopwatch.ElapsedMilliseconds} ms");
        var state = lab.Reachability.Get(host.Id)!;
        Assert.Equal(HostFailureKind.Connect, state.Kind);
        Assert.IsType<SshOperationTimeoutException>(state.LastFailure);
        Assert.Equal(1, lab.Transports.Attempts(host));
        Assert.Equal(0, lab.Pool.ConnectWaiters(host.Id));
    }

    [Fact]
    public async Task OneCallerCancellingLeavesTheAttemptRunningAndAllCancellingEndsItWithoutBackingTheHostOff()
    {
        using var lab = new HangLab();
        var gate = new TaskCompletionSource();
        var host = lab.Add("web", "10.0.0.1", script => script.ConnectGate = gate.Task);
        using var cancelFirst = new CancellationTokenSource();
        using var cancelSecond = new CancellationTokenSource();

        var first = Start(lab, host, Long, cancellation: cancelFirst.Token);
        var second = Start(lab, host, Long, cancellation: cancelSecond.Token);
        var survivor = StartSession(lab, host, Long);
        await WaitUntilConnecting(lab, host, 3);

        await cancelFirst.CancelAsync();
        Assert.IsAssignableFrom<OperationCanceledException>((await first.Task).Failure);
        Assert.Equal(1, lab.Transports.Attempts(host));
        Assert.False(survivor.IsCompleted);
        gate.SetResult();
        using var session = await survivor;
        Assert.True(session.IsConnected);
        await cancelSecond.CancelAsync();
        await second.Task; // finished with the shared outcome before or cancelled: either way it ends

        // Everyone cancelling: the attempt ends, nothing is recorded against the host, and the next call starts afresh.
        using var lab2 = new HangLab();
        var held = lab2.Add("held", "10.0.0.2", script => script.ConnectGate = new TaskCompletionSource().Task);
        using var cancelAll = new CancellationTokenSource();
        var calls = Enumerable.Range(0, 3).Select(_ => Start(lab2, held, Long, cancellation: cancelAll.Token)).ToArray();
        await WaitUntilConnecting(lab2, held, 3);
        await cancelAll.CancelAsync();
        var results = await Task.WhenAll(calls.Select(call => call.Task));

        Assert.All(results, result => Assert.IsAssignableFrom<OperationCanceledException>(result.Failure));
        await WaitUntilConnecting(lab2, held, 0);
        Assert.Null(lab2.Reachability.Get(held.Id));
        lab2.Transports.Script(held.Host).ConnectGate = null;
        using var next = await StartSession(lab2, held, Long);
        Assert.True(next.IsConnected);
        Assert.Equal(2, lab2.Transports.Attempts(held));
    }

    [Fact]
    public async Task AnEditDuringASharedAttemptSendsEveryWaitingCallerToTheNewSettings()
    {
        using var lab = new HangLab();
        var host = lab.Add("web", "10.0.0.1", script => script.ConnectGate = new TaskCompletionSource().Task);

        var calls = Enumerable.Range(0, 5).Select(_ => StartSession(lab, host, Long)).ToArray();
        await WaitUntilConnecting(lab, host, 5);

        // The corrected settings connect at once; the old attempt is ended, not waited for.
        lab.Transports.Script(host.Host).ConnectGate = null;
        lab.Pool.Evict(host.Id);
        var sessions = await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.All(sessions, session => Assert.True(session.IsConnected));
        Assert.Equal(2, lab.Transports.Attempts(host));
        Assert.Null(lab.Reachability.Get(host.Id));
        Assert.Single(lab.Transports.CreatedFor(host));
        foreach (var session in sessions) session.Dispose();
    }

    [Fact]
    public async Task EveryServerBehindAJumpHostSharesTheJumpHostsConnect()
    {
        using var lab = new HangLab();
        var gate = new TaskCompletionSource();
        var edge = lab.Add("edge", "10.0.0.1", script => script.ConnectGate = gate.Task);
        var behind = Enumerable.Range(1, 5).Select(index => lab.Add($"app-{index}", $"10.1.0.{index}", jump: edge)).ToArray();

        var calls = behind.Select(server => StartSession(lab, server, Long)).ToArray();
        await WaitUntilConnecting(lab, edge, 5);
        gate.SetResult();
        var sessions = await Task.WhenAll(calls);

        Assert.Equal(1, lab.Transports.Attempts(edge));
        Assert.All(behind, server => Assert.Equal(1, lab.Transports.Attempts(server)));
        foreach (var session in sessions) session.Dispose();
        Assert.Equal(0, lab.Pool.ActiveLeases);
    }

    [Fact]
    public async Task EveryServerBehindADeadJumpHostGetsTheSameCauseFromOneAttempt()
    {
        using var lab = new HangLab();
        var gate = new TaskCompletionSource();
        var edge = lab.Add("edge", "10.0.0.1", script => { script.ConnectGate = gate.Task; script.ConnectFails = () => new FileNotFoundException("no key"); });
        var behind = Enumerable.Range(1, 5).Select(index => lab.Add($"app-{index}", $"10.1.0.{index}", jump: edge)).ToArray();

        var calls = behind.Select(server => Start(lab, server, Long)).ToArray();
        await WaitUntilConnecting(lab, edge, 5);
        gate.SetResult();
        var results = await Task.WhenAll(calls.Select(call => call.Task));

        Assert.Equal(1, lab.Transports.Attempts(edge));
        Assert.All(behind, server => Assert.Equal(0, lab.Transports.Attempts(server)));
        Assert.All(results, result =>
        {
            var failure = Assert.IsType<HostUnreachableException>(result.Failure);
            Assert.Equal(HostFailureKind.AuthFailed, failure.Kind);
            Assert.Contains("via jump host edge: authentication failed (key file not found)", failure.Message, StringComparison.Ordinal);
        });
    }

    // ---- helpers ----

    private sealed record CallResult(Exception? Failure, TimeSpan Elapsed);

    private sealed record Call(Task<CallResult> Task);

    private static Call Start(HangLab lab, DevOpsAgent.Domain.Servers.Server server, TimeSpan timeout, bool bypass = false,
        CancellationToken cancellation = default)
    {
        var stopwatch = Stopwatch.StartNew();
        return new Call(Task.Run(async () =>
        {
            try
            {
                await lab.Run(server, timeout, cancellation, bypass);
                return new CallResult(null, stopwatch.Elapsed);
            }
            catch (Exception exception)
            {
                return new CallResult(exception, stopwatch.Elapsed);
            }
        }));
    }

    private static Task<ISshCommandSession> StartSession(HangLab lab, DevOpsAgent.Domain.Servers.Server server, TimeSpan deadline) =>
        Task.Run(() => lab.Pool.GetSessionAsync(server.Id, false, deadline, CancellationToken.None));

    /// <summary>
    /// Waits until <paramref name="callers"/> callers wait for the connect to the server AND the connect has really begun: the
    /// caller that starts the attempt can be descheduled for a moment between registering it and calling the transport.
    /// </summary>
    private static Task WaitUntilConnecting(HangLab lab, DevOpsAgent.Domain.Servers.Server server, int callers) =>
        WaitUntil(() => lab.Pool.ConnectWaiters(server.Id) == callers && lab.Transports.Attempts(server) >= 1);

    private static async Task WaitUntil(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("The condition was never met.");
            await Task.Delay(5);
        }
    }
}
