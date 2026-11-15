using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Servers;
using DevOpsAgent.Infrastructure.Monitoring;
using DevOpsAgent.Infrastructure.Ssh;
using Renci.SshNet.Common;

namespace DevOpsAgent.Tests;

/// <summary>
/// T7.R25 (owner decision 2026-11-16): a password sudo is accepted only when its random marker comes back. Anything else
/// after the password was written (timeout, cancellation, lost connection, shutdown, an unknown answer) is "outcome unknown":
/// password sudo pauses, persisted, until Retry now or an edit/removal; and no password sudo starts with less than 20 s left.
/// </summary>
public sealed partial class SudoPasswordTests
{
    public static TheoryData<string> UnconfirmedEndings => ["timeout", "dropped connection", "shutdown", "unknown answer", "no answer"];

    [Theory]
    [MemberData(nameof(UnconfirmedEndings))]
    public async Task UnconfirmedAttempt_IsOutcomeUnknown_TriedOnce_AndPausedPersistently(string ending)
    {
        await using var lab = await SudoLab.CreateAsync(WrongPassword);
        lab.Session.SudoAnswer = ending switch
        {
            "timeout" => () => throw new SshOperationTimeoutException("The command did not finish within 30 s."),
            "dropped connection" => () => throw new SshConnectionException("An established connection was aborted by the server."),
            "shutdown" => () => throw new SshShutdownException(),
            "unknown answer" => () => new RemoteCommandResult(1, "", "sudo: PAM account management error: Authentication service cannot retrieve authentication info\n", TimeSpan.Zero),
            _ => () => new RemoteCommandResult(null, "", "", TimeSpan.Zero)
        };

        try { await lab.Runner.RunAsync(lab.ServerId, "ss -tulpnH", Sudo, CancellationToken.None); }
        catch (Exception exception) when (exception is SshException or OperationCanceledException) { }

        lab.Session.SudoAnswer = null; // the server would now answer, and with a wrong password: it must not be asked
        var later = new List<RemoteCommandResult>();
        for (var index = 0; index < 4; index++)
            later.Add(await lab.Runner.RunAsync(lab.ServerId, "docker ps -q", Sudo, CancellationToken.None));

        Assert.Equal(1, lab.Session.SudoAttempts);
        Assert.All(later, result => Assert.Equal(SudoPasswordGate.OutcomeUnknownMessage, result.StandardError));
        var server = await lab.GetServerAsync();
        Assert.NotNull(server.SudoPasswordOutcomeUnknownAtUtc);
        Assert.Null(server.SudoPasswordRejectedAtUtc);
        Assert.Null(server.SudoPasswordAttemptStartedAtUtc);
        Assert.NotNull(server.SudoAuthFailedSinceUtc); // the same Medium sudo-auth-failed alert
        Assert.False(server.CanUseSudo);
        var status = (await lab.ListSummaryAsync()).Sudo;
        Assert.Equal("sudo: password — outcome unknown, paused", status.Label);
        Assert.True(status.Paused);
        Assert.True(status.RetryAllowed);
        Assert.Contains(lab.Logs.Messages, message => message.Contains("outcome of a sudo password attempt is unknown", StringComparison.Ordinal));
        lab.AssertPasswordNowhere(WrongPassword);
    }

    [Fact]
    public async Task CancellationAfterThePasswordWasSent_IsOutcomeUnknown()
    {
        await using var lab = await SudoLab.CreateAsync(Password);
        var freeze = new TaskCompletionSource();
        lab.Session.Freeze = freeze.Task;
        using var cycle = new CancellationTokenSource();

        var running = lab.Runner.RunAsync(lab.ServerId, "ss -tulpnH", Sudo, cycle.Token);
        await lab.Session.WaitForSudoAttemptsAsync(1);
        await cycle.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);

        Assert.NotNull((await lab.GetServerAsync()).SudoPasswordOutcomeUnknownAtUtc);
        Assert.Equal(SudoPasswordGate.OutcomeUnknownMessage,
            (await lab.Runner.RunAsync(lab.ServerId, "ss -tulpnH", Sudo, CancellationToken.None)).StandardError);
        Assert.Equal(1, lab.Session.SudoAttempts);
    }

    [Fact]
    public async Task MarkerSeen_ThenTheCommandTimesOut_ThePasswordStaysUsable()
    {
        await using var lab = await SudoLab.CreateAsync(Password);
        lab.Session.Hold = new TaskCompletionSource().Task; // a command that never ends after sudo accepted the password
        using var cycle = new CancellationTokenSource();

        var running = lab.Runner.RunAsync(lab.ServerId, "apt-get -y upgrade", Sudo, cycle.Token);
        await lab.Session.WaitForSudoAttemptsAsync(1);
        await cycle.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);

        lab.Session.Hold = null;
        var server = await lab.GetServerAsync();
        Assert.Null(server.SudoPasswordOutcomeUnknownAtUtc);
        Assert.Null(server.SudoPasswordAttemptStartedAtUtc);
        Assert.Equal(0, (await lab.Runner.RunAsync(lab.ServerId, "ss -tulpnH", Sudo, CancellationToken.None)).ExitCode);
    }

    [Fact]
    public async Task RetryNow_LiftsAnUnknownOutcome_AndTriesThePasswordOnce()
    {
        await using var lab = await SudoLab.CreateAsync(Password);
        var capability = new ServerCapabilityService(lab.Runner, lab.Repository, TimeProvider.System);
        Assert.True((await capability.TestAsync(lab.ServerId, CancellationToken.None)).SudoCapable);
        lab.Session.SudoAnswer = () => throw new SshConnectionException("Connection lost.");
        await Assert.ThrowsAsync<SshConnectionException>(() => lab.Runner.RunAsync(lab.ServerId, "ss -tulpnH", Sudo, CancellationToken.None));
        Assert.NotNull((await lab.GetServerAsync()).SudoPasswordOutcomeUnknownAtUtc);

        // Retry now while the host still drops the command: one more attempt, still unknown, capability kept.
        var dropped = await capability.TestAsync(lab.ServerId, CancellationToken.None);
        Assert.True(dropped.SudoPasswordOutcomeUnknown);
        Assert.True(dropped.SudoCapable);
        Assert.Equal(3, lab.Session.SudoAttempts);

        // Retry now with the host answering: confirmed, the pause and the alert end.
        lab.Session.SudoAnswer = null;
        var retried = await capability.TestAsync(lab.ServerId, CancellationToken.None);
        Assert.False(retried.SudoProbeSkipped);
        Assert.False(retried.SudoPasswordOutcomeUnknown);
        Assert.True(retried.SudoCapable);
        Assert.Equal(4, lab.Session.SudoAttempts);
        var server = await lab.GetServerAsync();
        Assert.Null(server.SudoPasswordOutcomeUnknownAtUtc);
        Assert.Null(server.SudoAuthFailedSinceUtc);
        Assert.True(server.CanUseSudo);
    }

    [Fact]
    public async Task RetryNow_AfterAnUnknownOutcome_WithAWrongPassword_RejectsAndStopsRetrying()
    {
        await using var lab = await SudoLab.CreateAsync(WrongPassword);
        var capability = new ServerCapabilityService(lab.Runner, lab.Repository, TimeProvider.System);
        lab.Session.SudoAnswer = () => throw new SshOperationTimeoutException("timed out");
        await Assert.ThrowsAsync<SshOperationTimeoutException>(() => lab.Runner.RunAsync(lab.ServerId, "ss -tulpnH", Sudo, CancellationToken.None));
        lab.Session.SudoAnswer = null;

        var first = await capability.TestAsync(lab.ServerId, CancellationToken.None);
        var second = await capability.TestAsync(lab.ServerId, CancellationToken.None);

        Assert.True(first.SudoPasswordRejected);
        Assert.True(second.SudoProbeSkipped);
        Assert.Equal(2, lab.Session.SudoAttempts);
        Assert.False((await lab.ListSummaryAsync()).Sudo.RetryAllowed);
    }

    [Fact]
    public async Task EditingOrRemovingThePassword_LiftsAnUnknownOutcome()
    {
        await using var lab = await SudoLab.CreateAsync(Password);
        lab.Session.SudoAnswer = () => throw new SshOperationTimeoutException("timed out");
        await Assert.ThrowsAsync<SshOperationTimeoutException>(() => lab.Runner.RunAsync(lab.ServerId, "ss -tulpnH", Sudo, CancellationToken.None));
        lab.Session.SudoAnswer = null;

        await lab.EditAsync(lab.Input("db") with { SudoPassword = Password });
        Assert.Equal(0, (await lab.Runner.RunAsync(lab.ServerId, "ss -tulpnH", Sudo, CancellationToken.None)).ExitCode);
        Assert.Null((await lab.GetServerAsync()).SudoAuthFailedSinceUtc);

        lab.Session.SudoAnswer = () => throw new SshOperationTimeoutException("timed out");
        await Assert.ThrowsAsync<SshOperationTimeoutException>(() => lab.Runner.RunAsync(lab.ServerId, "ss -tulpnH", Sudo, CancellationToken.None));
        await lab.EditAsync(lab.Input("db") with { RemoveSudoPassword = true });
        var removed = await lab.GetServerAsync();
        Assert.Null(removed.SudoPasswordOutcomeUnknownAtUtc);
        Assert.Null(removed.SudoAuthFailedSinceUtc);
    }

    [Fact]
    public async Task AttemptInterruptedByARestart_IsOutcomeUnknown_AndNotTriedAgain()
    {
        await using var lab = await SudoLab.CreateAsync(Password);
        // What a crash between "attempt started" and its outcome leaves in the database.
        await lab.ExecuteSqlAsync("UPDATE Servers SET SudoPasswordAttemptStartedAtUtc = '2026-11-16 08:00:00+00:00'");

        var result = await lab.Runner.RunAsync(lab.ServerId, "ss -tulpnH", Sudo, CancellationToken.None);

        Assert.Equal(SudoPasswordGate.OutcomeUnknownMessage, result.StandardError);
        Assert.Equal(0, lab.Session.SudoAttempts);
        var server = await lab.GetServerAsync();
        Assert.NotNull(server.SudoPasswordOutcomeUnknownAtUtc);
        Assert.Null(server.SudoPasswordAttemptStartedAtUtc);
        Assert.Contains(lab.Logs.Messages, message => message.Contains("interrupted before its outcome was known", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheAttemptIsRecordedBeforeThePasswordIsSent()
    {
        await using var lab = await SudoLab.CreateAsync(Password);
        var freeze = new TaskCompletionSource();
        lab.Session.Freeze = freeze.Task;

        var running = lab.Runner.RunAsync(lab.ServerId, "ss -tulpnH", Sudo, CancellationToken.None);
        await lab.Session.WaitForSudoAttemptsAsync(1);
        Assert.NotNull((await lab.GetServerAsync()).SudoPasswordAttemptStartedAtUtc);
        // While it runs in this process, the stored "started" mark does not pause anything: a plain command still runs.
        Assert.Equal(0, (await lab.Runner.RunAsync(lab.ServerId, "uptime -p", Plain, CancellationToken.None)).ExitCode);

        freeze.SetResult();
        Assert.Equal(0, (await running).ExitCode);
        Assert.Null((await lab.GetServerAsync()).SudoPasswordAttemptStartedAtUtc);
    }

    [Fact]
    public async Task AttemptThatCannotBeRecorded_SendsNoPassword()
    {
        await using var lab = await SudoLab.CreateAsync(Password);
        await lab.ExecuteSqlAsync("""
            CREATE TRIGGER fail_attempt BEFORE UPDATE OF SudoPasswordAttemptStartedAtUtc ON Servers
            WHEN NEW.SudoPasswordAttemptStartedAtUtc IS NOT NULL BEGIN SELECT RAISE(ABORT, 'database is locked'); END;
            """);

        var result = await lab.Runner.RunAsync(lab.ServerId, "ss -tulpnH", Sudo, CancellationToken.None);

        Assert.Equal(SudoPasswordGate.AttemptNotRecordedMessage, result.StandardError);
        Assert.Equal(0, lab.Session.SudoAttempts);
    }

    [Theory]
    [InlineData(19, null)]
    [InlineData(60, 10)]
    [InlineData(60, 19)]
    public async Task NoPasswordSudo_StartsWithLessThanTwentySecondsLeft(int callSeconds, int? cycleSecondsLeft)
    {
        await using var lab = await SudoLab.CreateAsync(Password);
        var options = new SshRunOptions(TimeSpan.FromSeconds(callSeconds), UseSudo: true,
            NotAfterUtc: cycleSecondsLeft is { } left ? DateTimeOffset.UtcNow.AddSeconds(left) : null);

        var refused = await Assert.ThrowsAsync<TimeoutException>(() => lab.Runner.RunAsync(lab.ServerId, "ss -tulpnH", options, CancellationToken.None));

        Assert.Contains("20 s", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, lab.Session.SudoAttempts);
        var server = await lab.GetServerAsync();
        Assert.Null(server.SudoPasswordOutcomeUnknownAtUtc); // nothing was sent, nothing is paused
        Assert.Null(server.SudoPasswordAttemptStartedAtUtc);
        // A plain command with the same short deadline still runs.
        Assert.Equal(0, (await lab.Runner.RunAsync(lab.ServerId, "uptime -p", options with { UseSudo = false }, CancellationToken.None)).ExitCode);
    }

    [Fact]
    public async Task WaitingForTheTurn_StopsWhenTwentySecondsAreLeft()
    {
        await using var lab = await SudoLab.CreateAsync(Password);
        var freeze = new TaskCompletionSource();
        lab.Session.Freeze = freeze.Task;
        var first = lab.Runner.RunAsync(lab.ServerId, "ss -tulpnH", new SshRunOptions(TimeSpan.FromSeconds(60), UseSudo: true), CancellationToken.None);
        await lab.Session.WaitForSudoAttemptsAsync(1);

        var started = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() =>
            lab.Runner.RunAsync(lab.ServerId, "docker ps -q", new SshRunOptions(TimeSpan.FromSeconds(21), UseSudo: true), CancellationToken.None));
        Assert.InRange(started.Elapsed, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5));

        freeze.SetResult();
        Assert.Equal(0, (await first).ExitCode);
        Assert.Equal(1, lab.Session.SudoAttempts);
    }

    [Fact]
    public void PasswordSudoCommands_AreExemptFromTheSudoAlert_OnlyWithAWellFormedMarker()
    {
        var marker = OutputMarker.Create().Value;
        string Logged(string command) => "/usr/bin/sh -c '" + (RemoteCommand.SudoPasswordPreamble(marker) + command)
            .Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'";

        Assert.True(AppSudoCommands.IsAppCommand(Logged(DevOpsAgent.Infrastructure.Collectors.ListenerCollector.Command)));
        Assert.True(AppSudoCommands.IsAppCommand(Logged("true"))); // the password sudo probe
        Assert.False(AppSudoCommands.IsAppCommand(Logged("cat /etc/shadow")));
        Assert.False(AppSudoCommands.IsAppCommand(Logged("true; id")));
        Assert.False(AppSudoCommands.IsAppCommand(Logged("true").Replace(marker, "dva-sudo-ok-nothex", StringComparison.Ordinal)));
        Assert.False(AppSudoCommands.IsAppCommand("/usr/bin/sh -c 'true'"));
    }
}

/// <summary>
/// T7.R25 follow-up: every caller that may use password sudo leaves it more than the 20 s it needs (found live: the listener
/// collector's 15 s timeout meant it never ran with a password).
/// </summary>
public sealed class SudoCallerTimeoutTests
{
    [Fact]
    public async Task SudoCollectors_GiveAPasswordSudoMoreThanTwentySeconds()
    {
        var runner = new TimeoutRecordingRunner();
        var server = new DevOpsAgent.Domain.Servers.Server("db", "db.test", 22, "dvapw", "key", "staging");
        server.RecordCapabilities(true, null, DateTimeOffset.UnixEpoch);
        var context = new DevOpsAgent.Application.Abstractions.ServerContext(server, runner);
        DevOpsAgent.Application.Abstractions.ICollector[] collectors =
        [
            new DevOpsAgent.Infrastructure.Collectors.ListenerCollector(),
            new DevOpsAgent.Infrastructure.Collectors.NginxCollector(),
            new DevOpsAgent.Infrastructure.Collectors.DockerCollector(),
            new DevOpsAgent.Infrastructure.Collectors.ConnectionCollector()
        ];

        foreach (var collector in collectors)
        {
            try { await collector.CollectAsync(context, CancellationToken.None); } catch (Exception) { /* only the timeouts matter */ }
        }

        Assert.NotEmpty(runner.SudoTimeouts);
        Assert.All(runner.SudoTimeouts, timeout => Assert.True(timeout > SudoPasswordGate.MinimumTimeLeft, timeout.ToString()));
        Assert.True(AuthLogScanner.CommandTimeout > SudoPasswordGate.MinimumTimeLeft);
    }

    private sealed class TimeoutRecordingRunner : ISshRunner
    {
        public List<TimeSpan> SudoTimeouts { get; } = [];

        public Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options, CancellationToken cancellationToken)
        {
            if (options.UseSudo) SudoTimeouts.Add(options.Timeout);
            return Task.FromResult(new RemoteCommandResult(0, string.Empty, string.Empty, TimeSpan.Zero));
        }
    }
}
