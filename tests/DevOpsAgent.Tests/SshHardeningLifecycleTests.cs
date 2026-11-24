using System.Diagnostics;
using System.Net.Sockets;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Agent;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Application.Monitoring;
using DevOpsAgent.Application.Servers;
using DevOpsAgent.Application.Updates;
using DevOpsAgent.Domain.Agent;
using DevOpsAgent.Domain.Discovery;
using DevOpsAgent.Domain.Monitoring;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Infrastructure.Monitoring;
using DevOpsAgent.Infrastructure.Persistence;
using DevOpsAgent.Infrastructure.Ssh;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Renci.SshNet.Common;

namespace DevOpsAgent.Tests;

/// <summary>
/// Hardening tests for T7.R21 (deadline/timeout semantics), T7.R22 (shutdown and lease hardening),
/// T7.R23 (auth-failed follow-ups), and deferred T7.R19 v2-backlog items (claim token, jump order).
/// </summary>
public sealed class SshHardeningLifecycleTests
{
    private static readonly TimeSpan ShortProbe = TimeSpan.FromMilliseconds(120);

    // ---- Deferred T7.R19 v2-backlog ----

    [Fact]
    public void ClaimToken_CancelledCallerCannotClearLaterBypassCallersInFlightFlag()
    {
        var clock = new ManualClock();
        var tracker = new HostReachabilityTracker(clock);
        var serverId = Guid.NewGuid();
        tracker.RecordFailure(serverId, new SocketException((int)SocketError.TimedOut));
        clock.Advance(TimeSpan.FromMinutes(1));

        Assert.True(tracker.TryBeginAttempt(serverId, bypassBackoff: false, out _, out var firstToken));
        Assert.True(tracker.Get(serverId)!.RetryInProgress);

        // A "Retry now" (bypass) caller claims the attempt with a newer token.
        Assert.True(tracker.TryBeginAttempt(serverId, bypassBackoff: true, out _, out var bypassToken));
        Assert.NotEqual(firstToken, bypassToken);
        Assert.True(tracker.Get(serverId)!.RetryInProgress);

        // First caller cancels and releases its old claim token: bypass caller's in-flight flag must stay true.
        tracker.EndAttempt(serverId, firstToken);
        Assert.True(tracker.Get(serverId)!.RetryInProgress);

        // Only the bypass caller's token releases it.
        tracker.EndAttempt(serverId, bypassToken);
        Assert.False(tracker.Get(serverId)!.RetryInProgress);
    }

    [Fact]
    public async Task JumpOrder_TargetAlreadyInBackoff_DoesNotConnectJumpHost()
    {
        var clock = new ManualClock();
        using var lab = new HangLab(clock, HangLab.ConnectHeadroom);
        var edge = lab.Add("edge", "10.0.0.1");
        var app = lab.Add("app", "10.0.0.2", jump: edge);

        // Put target app itself into backoff without ever connecting edge.
        lab.Reachability.RecordFailure(app.Id, new SocketException((int)SocketError.ConnectionRefused), "connection refused");

        var exception = await Assert.ThrowsAsync<HostUnreachableException>(() => lab.Run(app, TimeSpan.FromSeconds(5)));

        Assert.Equal(app.Id, exception.ServerId);
        Assert.Null(exception.ViaJumpServerName);
        Assert.Equal(0, lab.Transports.Attempts(edge));
        Assert.Equal(0, lab.Transports.Attempts(app));
    }

    // ---- T7.R21: Deadline and timeout semantics ----

    [Fact]
    public async Task T7R21a_CommandCutShortBySlowConnect_DoesNotCountTowardRecycleOrBackoff()
    {
        // Connect consumes 180 ms of a 300 ms call deadline, leaving only ~120 ms (< 90% of 300 ms) for the command.
        using var lab = new HangLab(connectTimeout: TimeSpan.FromSeconds(5));
        var host = lab.Add("slow-connect", "10.0.10.1", script =>
        {
            script.ConnectDelay = TimeSpan.FromMilliseconds(180);
            script.Commands = CommandMode.HangHonouringToken;
        });

        // First call connects slowly and its command is cut short by the call deadline.
        await Assert.ThrowsAsync<SshOperationTimeoutException>(() => lab.Run(host, TimeSpan.FromMilliseconds(300)));
        Assert.Null(lab.Reachability.Get(host.Id));

        // Now connect is already cached (fast): 2 full timeouts would reach 3 ONLY if the truncated 1st call had counted.
        await Assert.ThrowsAsync<SshOperationTimeoutException>(() => lab.Run(host, ShortProbe));
        await Assert.ThrowsAsync<SshOperationTimeoutException>(() => lab.Run(host, ShortProbe));

        var client = Assert.Single(lab.Transports.CreatedFor(host));
        Assert.False(client.Disposed, "a command cut short by connect time must not count toward the 3-timeout client recycle");
        Assert.Null(lab.Reachability.Get(host.Id));

        // A 3rd full timeout recycles the client (still not backed off because the initial truncated call did not count as #1).
        await Assert.ThrowsAsync<SshOperationTimeoutException>(() => lab.Run(host, ShortProbe));
        Assert.True(client.Disposed);
        Assert.Null(lab.Reachability.Get(host.Id));
    }

    [Fact]
    public async Task T7R21b_ShortCallerDeadlineBelowConnectTimeout_DoesNotBackOffHostOrJumpHost()
    {
        using var lab = new HangLab(connectTimeout: TimeSpan.FromSeconds(5));
        var edge = lab.Add("edge", "10.0.10.2", script => { script.ConnectNeverEnds = true; script.ConnectHonoursToken = false; });
        var app = lab.Add("app", "10.0.10.3", jump: edge);

        // Direct host: 200 ms deadline < 5 s connect timeout -> SshOperationTimeoutException, NO backoff.
        await Assert.ThrowsAsync<SshOperationTimeoutException>(() => lab.Run(edge, TimeSpan.FromMilliseconds(200)));
        Assert.Null(lab.Reachability.Get(edge.Id));

        // Dependent behind jump host: 200 ms deadline < 5 s connect timeout -> NO backoff on jump host either.
        await Assert.ThrowsAsync<SshOperationTimeoutException>(() => lab.Run(app, TimeSpan.FromMilliseconds(200)));
        Assert.Null(lab.Reachability.Get(edge.Id));
        Assert.Null(lab.Reachability.Get(app.Id));
    }

    [Fact]
    public async Task T7R21c_ShortProbeTimeouts_DoNotEscalateOrRecycle_WhileLongCommandIsLeased()
    {
        using var lab = new HangLab(connectHeadroom: HangLab.ConnectHeadroom);
        var longStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLong = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = lab.Add("busy-apt", "10.0.10.4", script =>
        {
            script.OnCommand = async (cmd, _, ct) =>
            {
                if (cmd == "apt-upgrade")
                {
                    longStarted.TrySetResult();
                    await releaseLong.Task.WaitAsync(ct);
                    return new RemoteCommandResult(0, "upgraded\n", string.Empty, TimeSpan.FromMilliseconds(10));
                }

                await Task.Delay(Timeout.Infinite, ct);
                return new RemoteCommandResult(0, string.Empty, string.Empty, TimeSpan.Zero);
            };
        });

        // Start a legitimate 10-minute command (like apt upgrade).
        var longTask = lab.Runner.RunAsync(host.Id, "apt-upgrade", new SshRunOptions(TimeSpan.FromMinutes(10)), CancellationToken.None);
        await longStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Five short probe timeouts while the 10-minute command is leased must NOT recycle the client or back off the host.
        for (var i = 0; i < 5; i++)
        {
            await Assert.ThrowsAsync<SshOperationTimeoutException>(() => lab.Run(host, ShortProbe));
        }

        Assert.Null(lab.Reachability.Get(host.Id));
        var client = Assert.Single(lab.Transports.CreatedFor(host));
        Assert.False(client.Disposed);

        releaseLong.SetResult();
        var completed = await longTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, completed.ExitCode);
    }

    [Fact]
    public async Task T7R21d_CommandStartedBeforeEdit_DoesNotCountAgainstNewSettings()
    {
        using var lab = new HangLab(connectHeadroom: HangLab.ConnectHeadroom);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = lab.Add("edited", "10.0.10.5", script =>
        {
            script.OnCommand = async (_, _, ct) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return new RemoteCommandResult(0, string.Empty, string.Empty, TimeSpan.Zero);
            };
        });

        // Start 4 commands in parallel on generation 0, then Evict (edit server settings) before they time out.
        var inFlight = Enumerable.Range(0, 4).Select(_ => lab.Run(host, TimeSpan.FromMilliseconds(250))).ToArray();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(40);
        lab.Pool.Evict(host.Id);

        foreach (var call in inFlight)
        {
            await Assert.ThrowsAsync<SshOperationTimeoutException>(() => call);
        }

        // None of the pre-edit timeouts may count against the new settings.
        Assert.Null(lab.Reachability.Get(host.Id));
    }

    [Fact]
    public void T7R21d_CommandStartedBeforeBackoff_DoesNotEscalateAfterWindowOrClearClaimedRetry()
    {
        var clock = new ManualClock();
        var tracker = new HostReachabilityTracker(clock);
        var id = Guid.NewGuid();
        var timeout = new SshOperationTimeoutException("timed out");

        var oldCommandStartedAt = clock.GetUtcNow();
        clock.Advance(TimeSpan.FromSeconds(10));
        for (var i = 0; i < IHostReachability.CommandTimeoutThreshold; i++)
        {
            tracker.RecordCommandTimeout(id, timeout);
        }

        var initial = tracker.Get(id)!;
        Assert.Equal(1, initial.ConsecutiveFailures);

        // Advance past the 1-minute backoff window and let a new caller claim the retry.
        clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
        Assert.True(tracker.TryBeginAttempt(id, bypassBackoff: false, out _));
        Assert.True(tracker.Get(id)!.RetryInProgress);

        // The old command (started before backoff) now times out after the window: must NOT escalate or clear RetryInProgress.
        Assert.Null(tracker.RecordCommandTimeout(id, timeout, force: false, commandStartedAtUtc: oldCommandStartedAt));
        var after = tracker.Get(id)!;
        Assert.Equal(1, after.ConsecutiveFailures);
        Assert.True(after.RetryInProgress);
    }

    [Fact]
    public async Task T7R21e_CommandTimeout_UsesDistinctWordingInServersBadgeAndAlert()
    {
        var clock = new ManualClock();
        using var lab = new HangLab(clock, HangLab.ConnectHeadroom);
        var hung = lab.Add("slow-db", "10.0.10.6");
        for (var i = 0; i < IHostReachability.CommandTimeoutThreshold; i++)
        {
            lab.Reachability.RecordCommandTimeout(hung.Id, new SshOperationTimeoutException("timed out"));
        }

        var status = ServerReachability.Status(hung.Id, lab.Map(), lab.Reachability)!;
        Assert.Equal(ServerReachabilityStatus.CommandTimeoutBadge, status.Badge);
        Assert.Equal("Not responding", status.Badge);
        Assert.Contains("is not answering commands", status.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("is unreachable", status.Text, StringComparison.Ordinal);

        var source = SshAuthFailureConsumerTests.AlertSource(lab);
        var conditions = await source.GetActiveConditionsAsync(CancellationToken.None);
        var alert = Assert.Single(conditions, c => c.ServerId == hung.Id);
        Assert.Equal("slow-db is not answering commands", alert.Title);
        Assert.DoesNotContain("is unreachable", alert.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task T7R21f_AlertCycle_UnstartedHostsAndCutSudoRetry_DoNotOpenHighUnreachableAlert()
    {
        // 10 servers; first 8 hang until the cycle deadline fills all 8 throttle slots, so servers 9 & 10 never get a slot.
        var servers = Enumerable.Range(1, 10)
            .Select(i => new Server($"srv-{i}", $"10.0.20.{i}", 22, "dva", "key", "staging"))
            .ToList();
        var options = new MonitoringCycleOptions
        {
            AlertProbeTimeout = TimeSpan.FromSeconds(20),
            AlertCycleDeadline = TimeSpan.FromMilliseconds(250)
        };
        var runner = new SlotStarvationRunner(servers.Take(8).Select(s => s.Id).ToHashSet());
        var source = new AlertSignalSource(
            new HangServers(servers), new EmptyMetrics(), new EmptyChecks(), new EmptySnapshots(),
            runner, new AuthLogScanner(runner, new HangSettings(), Options.Create(new SecurityMonitoringOptions()), TimeProvider.System),
            new DriftService(new EmptySnapshots(), null!),
            new ContainerRestartMonitor(new HangSettings(), TimeProvider.System, NullLogger<ContainerRestartMonitor>.Instance),
            TimeProvider.System, options);

        var snapshot = await source.EvaluateSignalsAsync(CancellationToken.None);
        // Servers 9 and 10 never got a slot: must NOT open a High server-unreachable alert (they hold existing alerts instead).
        foreach (var starved in servers.Skip(8))
        {
            Assert.DoesNotContain(snapshot.Conditions, c => c.ServerId == starved.Id && c.Rule == "server-unreachable");
            Assert.Contains(snapshot.HeldScopes, h => h.ServerId == starved.Id && h.Rule == "server-unreachable");
        }

        // Probe answered PermissionDenied, then sudo retry was cut by the cycle deadline -> NO High server-unreachable alert.
        var sudoServer = new Server("docker-sudo", "10.0.20.99", 22, "dva", "key", "staging");
        sudoServer.RecordCapabilities(true, "Ubuntu 24.04", DateTimeOffset.UtcNow);
        var retryRunner = new SudoRetryHangRunner();
        var retrySource = new AlertSignalSource(
            new HangServers([sudoServer]), new EmptyMetrics(), new EmptyChecks(), new EmptySnapshots(),
            retryRunner, new AuthLogScanner(retryRunner, new HangSettings(), Options.Create(new SecurityMonitoringOptions()), TimeProvider.System),
            new DriftService(new EmptySnapshots(), null!),
            new ContainerRestartMonitor(new HangSettings(), TimeProvider.System, NullLogger<ContainerRestartMonitor>.Instance),
            TimeProvider.System, options);

        var retrySnapshot = await retrySource.EvaluateSignalsAsync(CancellationToken.None);
        Assert.DoesNotContain(retrySnapshot.Conditions, c => c.ServerId == sudoServer.Id && c.Rule == "server-unreachable");
    }

    [Fact]
    public async Task T7R21h_AbandonedAttemptGrace_DoesNotExtendCallFiveSecondsPastDeadline()
    {
        using var lab = new HangLab(connectTimeout: TimeSpan.FromMilliseconds(100));
        var dead = lab.Add("wedged", "192.0.2.55", script => { script.ConnectNeverEnds = true; script.ConnectHonoursToken = false; });

        var stopwatch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<Exception>(() => lab.Run(dead, TimeSpan.FromMilliseconds(200)));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(1500),
            $"abandoned attempt extended call to {stopwatch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void T7R21i_SlowLoginFailure_AnchorsRetryToAttemptStart_Preventing90SecondSlip()
    {
        var clock = new ManualClock();
        var tracker = new HostReachabilityTracker(clock);
        var id = Guid.NewGuid();

        // Cycle starts at t=0; login takes 5 seconds (longer than the 1 s slack) and fails at t=5.
        Assert.True(tracker.TryBeginAttempt(id, bypassBackoff: false, out _));
        clock.Advance(TimeSpan.FromSeconds(5));
        var state = tracker.RecordFailure(id, new SshAuthFailedException(SshAuthFailure.ServerRejectedKey));

        // NextRetryUtc is anchored to t=0 + 60 s = t=60 s (so 55 s from t=5), not t=65 s.
        Assert.Equal(TimeSpan.FromSeconds(55), state.NextRetryUtc - clock.GetUtcNow());

        // At t=60 s (next 30 s cycle after t=30 s), retry is allowed immediately instead of slipping to t=90 s.
        clock.Advance(TimeSpan.FromSeconds(55));
        Assert.False(tracker.IsBlocked(id, out _));
        Assert.True(tracker.TryBeginAttempt(id, bypassBackoff: false, out _));
    }

    // ---- T7.R22: Shutdown and lease hardening ----

    [Fact]
    public async Task T7R22_ActionExecutionService_RethrowsSshShutdownException_LeavingActionRunning()
    {
        await using var db = await CreateDbAsync();
        var server = new Server("app", "10.0.30.1", 22, "dva", "key", "staging");
        db.Context.Servers.Add(server);
        var action = new ActionRecord("restart_service", "test", $"[\"{server.Id}\"]", "{\"unit\":\"nginx.service\"}", null, DateTimeOffset.UtcNow);
        db.Context.ActionRecords.Add(action);
        await db.Context.SaveChangesAsync();

        var executionModes = new ExecutionModeService(new AppSettingRepository(db.Context), new ServerRepository(db.Context), NullLogger<ExecutionModeService>.Instance);
        await executionModes.SaveAsync(new ExecutionModePolicy("staging", ExecutionMode.Confirm), CancellationToken.None);
        var registry = new ToolRegistry();
        var service = new ActionExecutionService(
            new ActionRecordRepository(db.Context),
            new ServerRepository(db.Context),
            registry,
            executionModes,
            new ToolCommandBuilder(registry),
            new ShutdownThrowingRunner(),
            TestBanGuard.Create(),
            TimeProvider.System);

        await Assert.ThrowsAsync<SshShutdownException>(() => service.ApproveAndRunAsync(action.Id, "owner", CancellationToken.None));

        var stored = await db.Context.ActionRecords.AsNoTracking().SingleAsync();
        Assert.Equal(ActionRecordStatus.Running, stored.Status);

        // Startup sweep marks the interrupted Running action cleanly.
        var recovered = await service.RecoverInterruptedAsync(CancellationToken.None);
        Assert.Equal(1, recovered);
        var swept = await db.Context.ActionRecords.AsNoTracking().SingleAsync();
        Assert.Equal(ActionRecordStatus.Failed, swept.Status);
        Assert.Contains("Interrupted", swept.ResultsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task T7R22_UpdatesService_RethrowsSshShutdownException_WithoutRecordingServerError()
    {
        await using var db = await CreateDbAsync();
        var server = new Server("app", "10.0.30.2", 22, "dva", "key", "staging");
        db.Context.Servers.Add(server);
        await db.Context.SaveChangesAsync();

        var serverRepo = new ServerRepository(db.Context);
        var scanRepo = new UpdateScanRepository(db.Context);
        var executionModes = new ExecutionModeService(new AppSettingRepository(db.Context), serverRepo, NullLogger<ExecutionModeService>.Instance);
        var service = new UpdatesService(serverRepo, new UpdatePackageRepository(db.Context), scanRepo,
            new ShutdownUpdatesCollector(), executionModes, TimeProvider.System);

        await Assert.ThrowsAsync<SshShutdownException>(() => service.ScanAsync(CancellationToken.None));
        Assert.Empty(await scanRepo.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task T7R22_PoolDispose_WithThreeFrozenClients_ExitsInUnderOneSecond()
    {
        using var lab = new HangLab();
        var frozen = Enumerable.Range(1, 3)
            .Select(i => lab.Add($"frozen-{i}", $"10.0.40.{i}", script => script.DisposeDelay = TimeSpan.FromMilliseconds(900)))
            .ToArray();

        foreach (var server in frozen)
        {
            await lab.Run(server, TimeSpan.FromSeconds(5));
        }

        var stopwatch = Stopwatch.StartNew();
        lab.Pool.Dispose();
        stopwatch.Stop();

        // Sequential disposal of three 900 ms frozen clients would take >= 2700 ms; parallel bounded disposal exits < 1 s.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"Pool.Dispose took {stopwatch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task T7R22_CommandRacingPlainPoolDispose_ThrowsSshShutdownException_NotObjectDisposed()
    {
        using var lab = new HangLab();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = lab.Add("plain-dispose", "10.0.40.9", script =>
        {
            script.OnCommand = async (_, _, ct) =>
            {
                started.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException)
                {
                    throw new ObjectDisposedException("SshClient");
                }

                return new RemoteCommandResult(0, "", "", TimeSpan.Zero);
            };
        });

        var running = lab.Run(host, TimeSpan.FromMinutes(1));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        lab.Pool.Dispose();

        await Assert.ThrowsAsync<SshShutdownException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task T7R22_LateCallerReusesJustPublishedClient_WithoutStartingSecondFlight()
    {
        using var lab = new HangLab();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = lab.Add("shared", "10.0.40.10", script => script.ConnectGate = gate.Task);

        var first = lab.Pool.GetSessionAsync(host.Id, false, CancellationToken.None);
        for (var i = 0; i < 100 && lab.Pool.ConnectWaiters(host.Id) == 0; i++) await Task.Delay(5);
        gate.SetResult();

        // Concurrent wave arriving right as the first flight publishes its client.
        var wave = Enumerable.Range(0, 12).Select(_ => lab.Pool.GetSessionAsync(host.Id, false, CancellationToken.None)).ToArray();
        using var s0 = await first;
        var sessions = await Task.WhenAll(wave);
        foreach (var session in sessions) session.Dispose();

        Assert.Equal(1, lab.Transports.Attempts(host));
        Assert.Single(lab.Transports.CreatedFor(host));
    }

    // ---- T7.R23: Auth-failed follow-ups ----

    [Fact]
    public async Task T7R23_TestConnectionBehindAuthFailedJumpHost_HintsJumpHostNotTarget()
    {
        using var lab = new HangLab();
        var edge = lab.Add("edge-gw", "10.0.50.1", script => script.ConnectFails = () => new SshAuthenticationException("denied"));
        var app = lab.Add("app-node", "10.0.50.2", jump: edge);

        var result = await new ServerCapabilityService(lab.Runner, lab.Servers, lab.Time).TestAsync(app.Id, CancellationToken.None);

        Assert.False(result.Reachable);
        Assert.Contains("jump host edge-gw", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("Check the identity key file, its passphrase, and that the key is authorised for the user on the server.",
            result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void T7R23_DataProtectionDecryptFailure_DoesNotCountTowardAuthPause()
    {
        var clock = new ManualClock();
        var tracker = new HostReachabilityTracker(clock);
        var id = Guid.NewGuid();

        for (var i = 0; i < 6; i++)
        {
            var state = tracker.RecordFailure(id, new SshAuthFailedException(SshAuthFailure.PassphraseUnreadable));
            Assert.Equal(HostFailureKind.AuthFailed, state.Kind);
            Assert.False(state.AutomaticRetriesPaused);
            Assert.Equal(1, state.ConsecutiveFailures);
            Assert.Contains("cannot be decrypted", state.Cause, StringComparison.Ordinal);
            clock.Advance(TimeSpan.FromMinutes(1));
        }
    }

    [Fact]
    public void T7R23_AuthPause_SurvivesTransientConnectFailure_UntilSuccessOrEdit()
    {
        var clock = new ManualClock();
        var tracker = new HostReachabilityTracker(clock);
        var id = Guid.NewGuid();

        for (var i = 0; i < IHostReachability.AuthFailedAttemptLimit; i++)
        {
            tracker.RecordFailure(id, new SshAuthFailedException(SshAuthFailure.ServerRejectedKey));
        }

        Assert.True(tracker.Get(id)!.AutomaticRetriesPaused);

        // Transient network flap during a manual retry must NOT lift the 3-failure auth pause.
        var afterFlap = tracker.RecordFailure(id, new SocketException((int)SocketError.TimedOut));
        Assert.True(afterFlap.AutomaticRetriesPaused);
        Assert.Equal(HostFailureKind.AuthFailed, afterFlap.Kind);
        Assert.Equal(DateTimeOffset.MaxValue, afterFlap.NextRetryUtc);

        // Even 10 minutes later, automatic retries stay blocked.
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.False(tracker.TryBeginAttempt(id, bypassBackoff: false, out _));

        // Only a successful connection or a server edit (Reset) lifts the pause.
        tracker.RecordSuccess(id);
        Assert.Null(tracker.Get(id));
    }

    [Fact]
    public async Task T7R23_EvictAtomicallyPreventsStaleFlightFromRecordingFailure()
    {
        using var lab = new HangLab();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = lab.Add("race-evict", "10.0.50.9", script =>
        {
            script.ConnectGate = gate.Task;
            script.ConnectHonoursToken = false;
            script.ConnectFails = () => new SshAuthenticationException("old-bad-key");
        });

        var call = lab.Run(host, TimeSpan.FromSeconds(5));
        for (var i = 0; i < 100 && lab.Transports.Attempts(host) == 0; i++) await Task.Delay(5);

        // Fix settings and Evict while old flight is still failing.
        lab.Transports.Script(host.Host).ConnectGate = null;
        lab.Transports.Script(host.Host).ConnectFails = null;
        lab.Pool.Evict(host.Id);
        gate.SetResult();

        var result = await call.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, result.ExitCode);
        Assert.Null(lab.Reachability.Get(host.Id));
    }

    // ---- Test doubles ----

    private sealed class ShutdownThrowingRunner : ISshRunner
    {
        public Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options, CancellationToken cancellationToken) =>
            throw new SshShutdownException();
    }

    private sealed class ShutdownUpdatesCollector : IUpdatesCollector
    {
        public Task<CollectedUpdateResult> CollectAsync(Server server, bool cacheOnly, CancellationToken cancellationToken) =>
            throw new SshShutdownException();
    }

    private sealed class SlotStarvationRunner(HashSet<Guid> hangingServers) : ISshRunner
    {
        public async Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options, CancellationToken cancellationToken)
        {
            if (hangingServers.Contains(serverId)) await Task.Delay(Timeout.Infinite, cancellationToken);
            return new RemoteCommandResult(0, "__DVA_DOCKER__\n__DVA_SYSTEMD_FAILED__\n", string.Empty, TimeSpan.Zero);
        }
    }

    private sealed class SudoRetryHangRunner : ISshRunner
    {
        public async Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options, CancellationToken cancellationToken)
        {
            if (options.UseSudo) await Task.Delay(Timeout.Infinite, cancellationToken);
            return new RemoteCommandResult(0, "__DVA_DOCKER__\n__DVA_DOCKER_DENIED__\n__DVA_SYSTEMD_FAILED__\n", string.Empty, TimeSpan.Zero);
        }
    }

    private sealed class EmptyMetrics : IMetricRepository
    {
        public Task AddSamplesAsync(IReadOnlyCollection<MetricSample> samples, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<MetricSample>> GetRecentAsync(Guid serverId, DateTimeOffset sinceUtc, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MetricSample>>([]);
        public Task<IReadOnlyList<MetricSample>> GetLatestAsync(Guid serverId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MetricSample>>([]);
        public Task<IReadOnlyList<MetricSample>> GetUnrolledAsync(DateTimeOffset beforeUtc, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<MetricSample>>([]);
        public Task SaveRollupsAsync(IReadOnlyCollection<MetricRollup> rollups, DateTimeOffset rawPruneBeforeUtc, DateTimeOffset rollupPruneBeforeUtc, CancellationToken cancellationToken) => Task.CompletedTask;
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

    private static async Task<SqliteDb> CreateDbAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var context = new AppDbContext(options);
        await context.Database.EnsureCreatedAsync();
        return new SqliteDb(connection, context);
    }

    private sealed class SqliteDb(SqliteConnection connection, AppDbContext context) : IAsyncDisposable
    {
        public AppDbContext Context { get; } = context;
        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
