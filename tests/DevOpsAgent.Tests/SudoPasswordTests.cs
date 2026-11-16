using System.Collections.Concurrent;
using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Agent;
using DevOpsAgent.Application.Servers;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Infrastructure.Persistence;
using DevOpsAgent.Infrastructure.Ssh;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DevOpsAgent.Tests;

/// <summary>
/// T7.R24: the per-server sudo password goes to stdin only, never into a command, a log or an output; a rejected password is
/// tried exactly once and then pauses all sudo on that server until the password is edited or the owner presses Retry now.
/// </summary>
public sealed partial class SudoPasswordTests
{
    private const string Password = "Pw-7f3K!q 9z";
    private const string WrongPassword = "Wrong-Pw-55";
    private static readonly SshRunOptions Sudo = new(TimeSpan.FromSeconds(30), UseSudo: true);
    private static readonly SshRunOptions Plain = new(TimeSpan.FromSeconds(30));

    [Fact]
    public async Task NoPassword_UsesSudoNExactlyAsBefore_WithoutStdin()
    {
        await using var lab = await SudoLab.CreateAsync(sudoPassword: null);

        await lab.Runner.RunAsync(lab.ServerId, "ss -tulpnH", Sudo, CancellationToken.None);
        await lab.Runner.ProbeSudoAsync(lab.ServerId, Plain, CancellationToken.None);

        Assert.Equal(["sudo -n -- sh -c 'ss -tulpnH'", "sudo -n true"], lab.Session.Calls.Select(call => call.Command));
        Assert.All(lab.Session.Calls, call => Assert.Null(call.Input));
    }

    [Fact]
    public async Task Password_GoesToStdinOnly_WithSudoS()
    {
        await using var lab = await SudoLab.CreateAsync(Password);

        var sudo = await lab.Runner.RunAsync(lab.ServerId, "ss -tulpnH", Sudo, CancellationToken.None);
        var plain = await lab.Runner.RunAsync(lab.ServerId, "uptime -p", Plain, CancellationToken.None);
        var probe = await lab.Runner.ProbeSudoAsync(lab.ServerId, Plain, CancellationToken.None);

        Assert.Equal(0, sudo.ExitCode);
        Assert.Equal(0, plain.ExitCode);
        Assert.Equal(0, probe.ExitCode);
        Assert.Matches(@"^LC_ALL=C sudo -S -p '' -- sh -c 'exec </dev/null; printf ""%s\\n"" ""dva-sudo-ok-[0-9a-f]{32}""; ss -tulpnH'$",
            lab.Session.Calls[0].Command);
        Assert.Equal(Password + "\n", lab.Session.Calls[0].Input);
        Assert.Equal("ok", sudo.StandardOutput); // the marker line is not part of the output
        Assert.Equal("uptime -p", lab.Session.Calls[1].Command);
        Assert.Null(lab.Session.Calls[1].Input);
        Assert.Matches(@"^LC_ALL=C sudo -S -p '' -- sh -c 'exec </dev/null; printf ""%s\\n"" ""dva-sudo-ok-[0-9a-f]{32}""; true'$",
            lab.Session.Calls[2].Command);
        Assert.Equal(Password + "\n", lab.Session.Calls[2].Input);
        Assert.NotEqual(Marker(lab.Session.Calls[0].Command), Marker(lab.Session.Calls[2].Command)); // a new marker per call
        lab.AssertPasswordNowhere(Password);
    }

    [Fact]
    public async Task Output_IsRedacted_ForSudoAndPlainCommands_AlsoWhilePaused()
    {
        await using var lab = await SudoLab.CreateAsync(Password);
        // A command that prints its stdin (or a file that happens to hold the password) must not leak it.
        lab.Session.Echo = true;

        var sudo = await lab.Runner.RunAsync(lab.ServerId, "cat", Sudo, CancellationToken.None);
        var plain = await lab.Runner.RunAsync(lab.ServerId, $"printf '%s' '{Password}'", Plain, CancellationToken.None);

        Assert.Contains(SudoPasswordGate.Redacted, sudo.StandardOutput);
        Assert.Contains(SudoPasswordGate.Redacted, sudo.StandardError);
        Assert.Contains(SudoPasswordGate.Redacted, plain.StandardOutput);
        Assert.DoesNotContain(Password, sudo.StandardOutput + sudo.StandardError + plain.StandardOutput + plain.StandardError);

        await lab.SetRejectedAsync();
        var paused = await lab.Runner.RunAsync(lab.ServerId, $"printf '%s' '{Password}'", Plain, CancellationToken.None);
        Assert.DoesNotContain(Password, paused.StandardOutput + paused.StandardError);
        Assert.Contains(SudoPasswordGate.Redacted, paused.StandardOutput);
    }

    [Fact]
    public async Task WrongPassword_IsTriedOnce_ThenNoSudoIsSent_NonSudoKeepsRunning()
    {
        await using var lab = await SudoLab.CreateAsync(WrongPassword);

        var first = await lab.Runner.RunAsync(lab.ServerId, "ss -tulpnH", Sudo, CancellationToken.None);
        Assert.Equal(1, first.ExitCode);
        Assert.True(SudoPasswordGate.IsPasswordRejected(first));

        var later = new List<RemoteCommandResult>();
        for (var index = 0; index < 5; index++)
            later.Add(await lab.Runner.RunAsync(lab.ServerId, "docker ps -q", Sudo, CancellationToken.None));
        var probeLikeCollector = await lab.Runner.RunAsync(lab.ServerId, "true", Sudo, CancellationToken.None);
        var plain = await lab.Runner.RunAsync(lab.ServerId, "uptime -p", Plain, CancellationToken.None);

        Assert.Equal(1, lab.Session.SudoAttempts);
        Assert.All(later.Append(probeLikeCollector), result =>
        {
            Assert.Equal(1, result.ExitCode);
            Assert.Equal(SudoPasswordGate.PausedMessage, result.StandardError);
            Assert.Equal(TimeSpan.Zero, result.Duration);
        });
        Assert.Equal(0, plain.ExitCode);
        Assert.Equal("uptime -p", lab.Session.Calls[^1].Command);

        var server = await lab.GetServerAsync();
        Assert.NotNull(server.SudoPasswordRejectedAtUtc);
        Assert.False(server.CanUseSudo);
        Assert.Contains(lab.Logs.Messages, message => message.Contains("sudo password was rejected", StringComparison.Ordinal));
        lab.AssertPasswordNowhere(WrongPassword);
    }

    [Fact]
    public async Task WrongPassword_ConcurrentSudoCommands_OnlyOneAttemptReachesTheServer()
    {
        await using var lab = await SudoLab.CreateAsync(WrongPassword);
        lab.Session.Delay = TimeSpan.FromMilliseconds(150);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(index =>
            lab.Runner.RunAsync(lab.ServerId, $"docker inspect {index:x12}", Sudo, CancellationToken.None)));

        Assert.Equal(1, lab.Session.SudoAttempts);
        Assert.Single(results, result => SudoPasswordGate.IsPasswordRejected(result));
        Assert.Equal(7, results.Count(result => result.StandardError == SudoPasswordGate.PausedMessage));
    }

    [Fact]
    public async Task CorrectPassword_ConcurrentSudo_RunsOneAtATimeWhileAuthenticating()
    {
        await using var lab = await SudoLab.CreateAsync(Password);
        lab.Session.Delay = TimeSpan.FromMilliseconds(40);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            lab.Runner.RunAsync(lab.ServerId, "ss -tulpnH", Sudo, CancellationToken.None)));

        Assert.All(results, result => Assert.Equal(0, result.ExitCode));
        Assert.Equal(4, lab.Session.SudoAttempts);
        Assert.Equal(1, lab.Session.MaxConcurrentSudo);
    }

    [Fact]
    public async Task ConfirmedPassword_ReleasesTheTurn_WhileALongCommandRuns()
    {
        await using var lab = await SudoLab.CreateAsync(Password);
        var release = new TaskCompletionSource();
        lab.Session.Hold = release.Task;

        var longCommand = lab.Runner.RunAsync(lab.ServerId, "apt-get -y upgrade", Sudo, CancellationToken.None);
        await lab.Session.WaitForSudoAttemptsAsync(1);
        lab.Session.Hold = null;
        // The marker of the long command came back: the next password sudo does not wait for the upgrade to end.
        Assert.Equal(0, (await lab.Runner.RunAsync(lab.ServerId, "ss -tulpnH", Sudo, CancellationToken.None)).ExitCode);
        Assert.Equal(2, lab.Session.SudoAttempts);
        Assert.False(longCommand.IsCompleted);

        release.SetResult();
        Assert.Equal(0, (await longCommand).ExitCode);
        Assert.Null((await lab.GetServerAsync()).SudoPasswordAttemptStartedAtUtc);
    }

    [Fact]
    public async Task UnconfirmedAttempt_HoldsTheTurn_WithoutATimeLimit_ThenPausesTheNextOne()
    {
        await using var lab = await SudoLab.CreateAsync(Password);
        var unfreeze = new TaskCompletionSource();
        lab.Session.Freeze = unfreeze.Task;
        var longCall = new SshRunOptions(TimeSpan.FromSeconds(60), UseSudo: true);

        var first = lab.Runner.RunAsync(lab.ServerId, "ss -tulpnH", longCall, CancellationToken.None);
        await lab.Session.WaitForSudoAttemptsAsync(1);
        var second = lab.Runner.RunAsync(lab.ServerId, "docker ps -q", longCall, CancellationToken.None);
        await Task.Delay(200);
        Assert.Equal(1, lab.Session.SudoAttempts); // the first outcome is still open: nothing else may try the password

        lab.Session.SudoAnswer = () => new RemoteCommandResult(null, "", "", TimeSpan.Zero); // the channel closed without an answer
        unfreeze.SetResult();
        Assert.Null((await first).ExitCode);
        Assert.Equal(SudoPasswordGate.OutcomeUnknownMessage, (await second).StandardError);
        Assert.Equal(1, lab.Session.SudoAttempts);
    }

    [Fact]
    public async Task TheTurnIsHeldAcrossASlowConnect()
    {
        await using var lab = await SudoLab.CreateAsync(WrongPassword);
        var connected = new TaskCompletionSource();
        lab.Pool.Connect = connected.Task;

        var first = lab.Runner.RunAsync(lab.ServerId, "ss -tulpnH", Sudo, CancellationToken.None);
        for (var index = 0; index < 200 && lab.Pool.Connects < 1; index++) await Task.Delay(10);
        var second = lab.Runner.RunAsync(lab.ServerId, "docker ps -q", Sudo, CancellationToken.None);
        await Task.Delay(100);
        Assert.Equal(1, lab.Pool.Connects);

        connected.SetResult();
        Assert.True(SudoPasswordGate.IsPasswordRejected(await first));
        Assert.Equal(SudoPasswordGate.PausedMessage, (await second).StandardError);
        Assert.Equal(1, lab.Session.SudoAttempts);
    }

    [Fact]
    public async Task WrongPassword_WhoseRejectionCannotBeStored_IsStillNotSentAgain()
    {
        await using var lab = await SudoLab.CreateAsync(WrongPassword);
        await lab.ExecuteSqlAsync("""
            CREATE TRIGGER fail_rejection BEFORE UPDATE OF SudoPasswordRejectedAtUtc ON Servers
            BEGIN SELECT RAISE(ABORT, 'database is locked'); END;
            """);

        var first = await lab.Runner.RunAsync(lab.ServerId, "ss -tulpnH", Sudo, CancellationToken.None);
        var later = await lab.Runner.RunAsync(lab.ServerId, "docker ps -q", Sudo, CancellationToken.None);
        var probe = await lab.Runner.ProbeSudoAsync(lab.ServerId, Plain, CancellationToken.None);

        Assert.True(SudoPasswordGate.IsPasswordRejected(first));
        Assert.Equal(SudoPasswordGate.PausedMessage, later.StandardError);
        Assert.Equal(SudoPasswordGate.PausedMessage, probe.StandardError);
        Assert.Equal(1, lab.Session.SudoAttempts);
        Assert.Null((await lab.GetServerAsync()).SudoPasswordRejectedAtUtc);
        Assert.Contains(lab.Logs.Messages, message => message.StartsWith("Error", StringComparison.Ordinal)
            && message.Contains("could not be stored", StringComparison.Ordinal));
        lab.AssertPasswordNowhere(WrongPassword);

        await lab.EditAsync(lab.Input("db") with { SudoPassword = Password });
        Assert.Equal(0, (await lab.Runner.RunAsync(lab.ServerId, "ss -tulpnH", Sudo, CancellationToken.None)).ExitCode);
        Assert.Equal(2, lab.Session.SudoAttempts);
    }

    [Fact]
    public async Task EditingThePassword_LiftsThePause_RenameDoesNot()
    {
        await using var lab = await SudoLab.CreateAsync(WrongPassword);
        await lab.Runner.RunAsync(lab.ServerId, "ss -tulpnH", Sudo, CancellationToken.None);

        await lab.EditAsync(lab.Input("db-renamed"));
        await lab.Runner.RunAsync(lab.ServerId, "ss -tulpnH", Sudo, CancellationToken.None);
        Assert.Equal(1, lab.Session.SudoAttempts);
        Assert.True((await lab.ListSummaryAsync()).Sudo.Paused);

        await lab.EditAsync(lab.Input("db-renamed") with { SudoPassword = Password });
        var afterEdit = await lab.Runner.RunAsync(lab.ServerId, "ss -tulpnH", Sudo, CancellationToken.None);

        Assert.Equal(0, afterEdit.ExitCode);
        Assert.Equal(2, lab.Session.SudoAttempts);
        Assert.Equal(Password + "\n", lab.Session.Calls[^1].Input);
        Assert.Null((await lab.GetServerAsync()).SudoPasswordRejectedAtUtc);
        lab.AssertPasswordNowhere(Password, WrongPassword);
    }

    [Fact]
    public async Task RemovingThePassword_GoesBackToSudoN()
    {
        await using var lab = await SudoLab.CreateAsync(WrongPassword);
        await lab.Runner.RunAsync(lab.ServerId, "ss -tulpnH", Sudo, CancellationToken.None);

        await lab.EditAsync(lab.Input("db") with { RemoveSudoPassword = true });
        await lab.Runner.RunAsync(lab.ServerId, "ss -tulpnH", Sudo, CancellationToken.None);

        var server = await lab.GetServerAsync();
        Assert.False(server.HasSudoPassword);
        Assert.Null(server.SudoPasswordRejectedAtUtc);
        Assert.Equal("sudo -n -- sh -c 'ss -tulpnH'", lab.Session.Calls[^1].Command);
        Assert.Null(lab.Session.Calls[^1].Input);
    }

    [Fact]
    public async Task RetryNowAndTestConnection_NeverTryARejectedPassword_KeepSudoCapability()
    {
        await using var lab = await SudoLab.CreateAsync(Password);
        var capability = new ServerCapabilityService(lab.Runner, lab.Repository, TimeProvider.System);
        Assert.True((await capability.TestAsync(lab.ServerId, CancellationToken.None)).SudoCapable);
        lab.Session.AcceptedPassword = "changed-on-the-server"; // the stored password no longer works
        await lab.Runner.RunAsync(lab.ServerId, "ss -tulpnH", Sudo, CancellationToken.None);
        Assert.Equal(2, lab.Session.SudoAttempts);

        // "Retry now" and "Test connection" are the same call on the Servers page: pressed three times, no sudo is sent.
        for (var press = 0; press < 3; press++)
        {
            var result = await capability.TestAsync(lab.ServerId, CancellationToken.None);
            Assert.True(result.Reachable);
            Assert.True(result.SudoProbeSkipped);
            Assert.True(result.SudoPasswordRejected);
            Assert.True(result.SudoCapable); // the last real probe's answer is kept, not overwritten with "no sudo"
        }

        var probe = await lab.Runner.ProbeSudoAsync(lab.ServerId, Plain, CancellationToken.None);
        Assert.Equal(SudoPasswordGate.PausedMessage, probe.StandardError);
        Assert.Equal(2, lab.Session.SudoAttempts);
        Assert.Equal(4, lab.Session.Calls.Count(call => call.Command == "uptime -p")); // the other probe steps still ran
        var server = await lab.GetServerAsync();
        Assert.NotNull(server.SudoPasswordRejectedAtUtc);
        Assert.True(server.SudoCapable);
        Assert.False(server.CanUseSudo);
        var status = (await lab.ListSummaryAsync()).Sudo;
        Assert.True(status.Paused);
        Assert.Contains("edited or removed", status.Detail, StringComparison.Ordinal);
        Assert.Contains("pam_faillock", ServerSudoStatus.RetryBlockedReason, StringComparison.Ordinal);

        // Only an edit lifts the pause; then sudo is used at once (capability kept) and Test connection probes again.
        await lab.EditAsync(lab.Input("db") with { SudoPassword = "changed-on-the-server" });
        Assert.Equal(0, (await lab.Runner.RunAsync(lab.ServerId, "ss -tulpnH", Sudo, CancellationToken.None)).ExitCode);
        var afterEdit = await capability.TestAsync(lab.ServerId, CancellationToken.None);
        Assert.False(afterEdit.SudoProbeSkipped);
        Assert.True(afterEdit.SudoCapable);
        Assert.Equal(4, lab.Session.SudoAttempts);
        lab.AssertPasswordNowhere(Password, "changed-on-the-server");
    }

    [Fact]
    public async Task TestConnection_WithANeverTriedWrongPassword_TriesOnce_ThenNotAgain()
    {
        await using var lab = await SudoLab.CreateAsync(WrongPassword);
        var capability = new ServerCapabilityService(lab.Runner, lab.Repository, TimeProvider.System);

        var first = await capability.TestAsync(lab.ServerId, CancellationToken.None);
        var second = await capability.TestAsync(lab.ServerId, CancellationToken.None);

        Assert.Equal(1, lab.Session.SudoAttempts);
        Assert.False(first.SudoProbeSkipped);
        Assert.True(first.SudoPasswordRejected);
        Assert.False(first.SudoCapable);
        Assert.EndsWith("; true'", lab.Session.Calls.Single(call => call.IsSudo).Command);
        Assert.True(second.SudoProbeSkipped);
        Assert.True(second.SudoPasswordRejected);
    }

    [Fact]
    public async Task RemovingARejectedPassword_LiftsThePause_AndProbesWithSudoN()
    {
        await using var lab = await SudoLab.CreateAsync(WrongPassword);
        var capability = new ServerCapabilityService(lab.Runner, lab.Repository, TimeProvider.System);
        await capability.TestAsync(lab.ServerId, CancellationToken.None);

        await lab.EditAsync(lab.Input("db") with { RemoveSudoPassword = true });
        var result = await capability.TestAsync(lab.ServerId, CancellationToken.None);

        Assert.False(result.SudoProbeSkipped);
        Assert.False(result.SudoPasswordUsed);
        Assert.Equal("sudo -n true", lab.Session.Calls[^2].Command);
    }

    [Fact]
    public async Task SudoAuthFailedState_OpensOnRejection_SurvivesAnEdit_ClosesOnSuccessOrRemoval()
    {
        await using var lab = await SudoLab.CreateAsync(WrongPassword);
        await lab.Runner.RunAsync(lab.ServerId, "ss -tulpnH", Sudo, CancellationToken.None);
        var since = (await lab.GetServerAsync()).SudoAuthFailedSinceUtc;
        Assert.NotNull(since);

        await lab.EditAsync(lab.Input("db") with { SudoPassword = Password });
        var edited = await lab.GetServerAsync();
        Assert.Null(edited.SudoPasswordRejectedAtUtc); // the pause is lifted ...
        Assert.Equal(since, edited.SudoAuthFailedSinceUtc); // ... but the alert waits for sudo to work

        // The marker came back, so sudo accepted the password, even though the command itself then failed (T7.R25).
        await lab.Runner.RunAsync(lab.ServerId, "failing-command", Sudo, CancellationToken.None);
        Assert.Null((await lab.GetServerAsync()).SudoAuthFailedSinceUtc);
        Assert.Single(lab.Logs.Messages, message => message.Contains("sudo works again", StringComparison.Ordinal));

        // A healthy server records nothing more on further successes.
        await lab.Runner.RunAsync(lab.ServerId, "ss -tulpnH", Sudo, CancellationToken.None);
        Assert.Single(lab.Logs.Messages, message => message.Contains("sudo works again", StringComparison.Ordinal));

        await lab.EditAsync(lab.Input("db") with { SudoPassword = WrongPassword });
        await lab.Runner.RunAsync(lab.ServerId, "ss -tulpnH", Sudo, CancellationToken.None);
        Assert.NotNull((await lab.GetServerAsync()).SudoAuthFailedSinceUtc);
        await lab.EditAsync(lab.Input("db") with { RemoveSudoPassword = true });
        var removed = await lab.GetServerAsync();
        Assert.Null(removed.SudoAuthFailedSinceUtc);
        Assert.Null(removed.SudoPasswordRejectedAtUtc);
        lab.AssertPasswordNowhere(Password, WrongPassword);
    }

    [Fact]
    public async Task StoredPassword_IsProtected_AndNeverInASummaryOrInputText()
    {
        await using var lab = await SudoLab.CreateAsync(Password);

        var server = await lab.GetServerAsync();
        Assert.NotNull(server.ProtectedSudoPassword);
        Assert.DoesNotContain(Password, server.ProtectedSudoPassword);
        var summary = await lab.ListSummaryAsync();
        Assert.True(summary.HasSudoPassword);
        Assert.DoesNotContain(Password, JsonSerializer.Serialize(summary));
        Assert.DoesNotContain(Password, (lab.Input("db") with { SudoPassword = Password }).ToString());
    }

    [Fact]
    public async Task InvalidPassword_IsRefused_WithoutEchoingIt()
    {
        await using var lab = await SudoLab.CreateAsync(sudoPassword: null);

        var lineBreak = await Assert.ThrowsAsync<ArgumentException>(() =>
            lab.EditAsync(lab.Input("db") with { SudoPassword = "abc\ndef-secret" }));
        var both = await Assert.ThrowsAsync<ArgumentException>(() =>
            lab.EditAsync(lab.Input("db") with { SudoPassword = "xyz-secret", RemoveSudoPassword = true }));

        Assert.DoesNotContain("secret", lineBreak.Message);
        Assert.DoesNotContain("secret", both.Message);
        Assert.False((await lab.GetServerAsync()).HasSudoPassword);
    }

    [Fact]
    public async Task UndecryptablePassword_SendsNoSudo_AndLogsNoSecret()
    {
        await using var lab = await SudoLab.CreateAsync(Password);
        lab.Protector.Broken = true;

        var result = await lab.Runner.RunAsync(lab.ServerId, "ss -tulpnH", Sudo, CancellationToken.None);

        Assert.Equal(SudoPasswordGate.UnusableMessage, result.StandardError);
        Assert.Equal(0, lab.Session.SudoAttempts);
        Assert.Null((await lab.GetServerAsync()).SudoPasswordRejectedAtUtc); // nothing was tried, nothing was rejected
        lab.AssertPasswordNowhere(Password);
    }

    [Fact]
    public void ActionPreview_ShowsTheSudoSWrapper_WithoutThePassword()
    {
        var withPassword = new ActionServerResult(Guid.NewGuid(), "db", "systemctl restart -- 'nginx'", null, "", "", 0, null, "db:22", UseSudo: true, SudoPassword: true);
        var without = withPassword with { SudoPassword = false };

        Assert.Equal(@"LC_ALL=C sudo -S -p '' -- sh -c 'exec </dev/null; printf ""%s\n"" ""dva-sudo-ok-<random>""; systemctl restart -- '\''nginx'\'''",
            withPassword.DisplayCommand);
        Assert.Equal(@"sudo -n -- sh -c 'systemctl restart -- '\''nginx'\'''", without.DisplayCommand);
    }

    [Theory]
    [InlineData(1, "Sorry, try again.\nsudo: no password was provided\nsudo: 1 incorrect password attempt\n", true)]
    [InlineData(1, "Sorry, try again.\r\n", true)]
    [InlineData(1, "sudo: 3 incorrect password attempts", true)]
    [InlineData(1, "sudo: no password was provided", true)]
    [InlineData(1, "The account is locked due to 3 failed logins.\nSorry, try again.\n", true)]
    [InlineData(0, "Sorry, try again.", false)]
    [InlineData(1, "sudo: a password is required", false)]
    [InlineData(1, "db is not in the sudoers file.", false)]
    [InlineData(1, "nginx: [emerg] Sorry, try again. later", false)]
    [InlineData(null, "", false)]
    public void Rejection_IsRecognisedFromSudosOwnLines(int? exitCode, string stderr, bool rejected) =>
        Assert.Equal(rejected, SudoPasswordGate.IsPasswordRejected(new RemoteCommandResult(exitCode, "", stderr, TimeSpan.Zero)));

    [Theory]
    [InlineData(true, false, false, "sudo: NOPASSWD")]
    [InlineData(false, false, false, "sudo: none")]
    [InlineData(null, false, false, "sudo: not tested")]
    [InlineData(true, true, false, "sudo: password")]
    [InlineData(null, true, false, "sudo: password · not tested")]
    [InlineData(false, true, false, "sudo: none")]
    [InlineData(true, true, true, "sudo: password — rejected")]
    public void SudoStatus_Labels(bool? sudoCapable, bool hasPassword, bool rejected, string label)
    {
        var status = ServerSudoStatus.Describe(sudoCapable, hasPassword, rejected ? DateTimeOffset.UnixEpoch : null);
        Assert.Equal(label, status.Label);
        Assert.Equal(rejected, status.Paused);
    }

    [Fact]
    public void CanUseSudo_IsFalseWhileThePasswordIsRejected()
    {
        var server = new Server("db", "10.0.0.3", 22, "dva", "id", "staging");
        server.RecordCapabilities(true, null, DateTimeOffset.UnixEpoch);
        server.SetSudoPassword("protected");
        Assert.True(server.CanUseSudo);

        server.RecordSudoPasswordRejected(DateTimeOffset.UnixEpoch);
        Assert.False(server.CanUseSudo);
        server.SetSudoPassword("protected-2");
        Assert.True(server.CanUseSudo);
    }

    private sealed class ListSwitch
    {
        public volatile bool Fail;
    }

    private sealed class SwitchableServerRepository(IServerRepository inner, ListSwitch listSwitch) : IServerRepository
    {
        public Task<IReadOnlyList<Server>> ListAsync(CancellationToken cancellationToken) =>
            listSwitch.Fail ? throw new InvalidOperationException("the database is not available") : inner.ListAsync(cancellationToken);

        public Task<Server?> GetAsync(Guid id, CancellationToken cancellationToken) => inner.GetAsync(id, cancellationToken);
        public Task AddAsync(Server server, CancellationToken cancellationToken) => inner.AddAsync(server, cancellationToken);
        public Task SaveAsync(Server server, CancellationToken cancellationToken) => inner.SaveAsync(server, cancellationToken);
        public Task DeleteAsync(Server server, CancellationToken cancellationToken) => inner.DeleteAsync(server, cancellationToken);
    }

    private sealed class SudoLab : IAsyncDisposable
    {
        private readonly string _databasePath;
        private readonly ServiceProvider _services;
        private readonly AsyncServiceScope _scope;

        private SudoLab(string databasePath, ServiceProvider services, TimeProvider clock, ServerStateEvents? events, ListSwitch listSwitch)
        {
            ListSwitch = listSwitch;
            _databasePath = databasePath;
            _services = services;
            _scope = services.CreateAsyncScope();
            Repository = _scope.ServiceProvider.GetRequiredService<IServerRepository>();
            Gate = new SudoPasswordGate(services.GetRequiredService<IServiceScopeFactory>(), Protector, clock, Logs, events);
            Inventory = new ServerService(Repository, Protector, new KeyStore(), new NoPool(), Gate);
            Pool = new SessionPool(Session);
            Runner = new SshRunner(Pool, Gate, clock);
        }

        public SudoPasswordGate Gate { get; }

        /// <summary>While <see cref="ListSwitch.Fail"/> is set, listing the servers throws (a failing or busy database).</summary>
        public ListSwitch ListSwitch { get; }

        public RecordingSession Session { get; } = new();
        public SessionPool Pool { get; }
        public MappingProtector Protector { get; } = new();
        public ListLogger<SudoPasswordGate> Logs { get; } = new();
        public IServerRepository Repository { get; }
        public ServerService Inventory { get; }
        public SshRunner Runner { get; }
        public Guid ServerId { get; private set; }

        public static async Task<SudoLab> CreateAsync(string? sudoPassword, TimeProvider? clock = null, ServerStateEvents? events = null)
        {
            var path = Path.Combine(Path.GetTempPath(), $"dva-sudo-{Guid.NewGuid():N}.db");
            var listSwitch = new ListSwitch();
            var services = new ServiceCollection()
                .AddDbContext<AppDbContext>(options => options.UseSqlite($"Data Source={path};Pooling=False"))
                .AddScoped<IServerRepository>(provider => new SwitchableServerRepository(new ServerRepository(provider.GetRequiredService<AppDbContext>()), listSwitch))
                .BuildServiceProvider();
            await using (var setup = services.CreateAsyncScope())
                await setup.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreatedAsync();

            var lab = new SudoLab(path, services, clock ?? TimeProvider.System, events, listSwitch);
            lab.Session.AcceptedPassword = Password;
            var created = await lab.Inventory.CreateAsync(lab.Input("db") with { SudoPassword = sudoPassword }, CancellationToken.None);
            lab.ServerId = created.Id;
            return lab;
        }

        public ServerInput Input(string name) => new(name, "10.88.20.30", 22, "dva", "id_ed25519", null, null, "staging", []);

        public Task EditAsync(ServerInput input) => Inventory.UpdateAsync(ServerId, input, CancellationToken.None);

        public async Task<ServerSummary> ListSummaryAsync() =>
            (await Inventory.ListAsync(CancellationToken.None)).Single(server => server.Id == ServerId);

        public async Task<Server> GetServerAsync()
        {
            await using var scope = _services.CreateAsyncScope();
            return (await scope.ServiceProvider.GetRequiredService<IServerRepository>().GetAsync(ServerId, CancellationToken.None))!;
        }

        public async Task ExecuteSqlAsync(string sql)
        {
            await using var scope = _services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlRawAsync(sql);
        }

        public async Task SetRejectedAsync()
        {
            await using var scope = _services.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetRequiredService<IServerRepository>();
            var server = (await repository.GetAsync(ServerId, CancellationToken.None))!;
            server.RecordSudoPasswordRejected(DateTimeOffset.UtcNow);
            await repository.SaveAsync(server, CancellationToken.None);
        }

        /// <summary>No command text and no log line may hold the passwords; only stdin of a sudo -S command does.</summary>
        public void AssertPasswordNowhere(params string[] secrets)
        {
            foreach (var secret in secrets)
            {
                Assert.All(Session.Calls, call => Assert.DoesNotContain(secret, call.Command));
                Assert.All(Session.Calls.Where(call => !call.IsSudoS), call => Assert.Null(call.Input));
                Assert.All(Logs.Messages, message => Assert.DoesNotContain(secret, message));
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _scope.DisposeAsync();
            await _services.DisposeAsync();
            SqliteConnection.ClearAllPools();
            File.Delete(_databasePath);
        }
    }

    private static string Marker(string command) =>
        System.Text.RegularExpressions.Regex.Match(command, "dva-sudo-ok-[0-9a-f]{32}").Value;

    internal sealed record SessionCall(string Command, string? Input, TimeSpan Timeout = default)
    {
        public bool IsSudo => Command.Contains("sudo ", StringComparison.Ordinal);
        public bool IsSudoS => Command.Contains("sudo -S -p '' ", StringComparison.Ordinal);
    }

    /// <summary>
    /// Plays a host whose sudo accepts <see cref="AcceptedPassword"/> from stdin, like <c>sudo -S</c>: an accepted password lets
    /// the shell print the marker first (reported at once, like the real transport), a wrong one ends in sudo's rejection lines.
    /// </summary>
    private sealed class RecordingSession : ISshCommandSession
    {
        private readonly ConcurrentQueue<SessionCall> _calls = new();
        private int _sudoAttempts;
        private int _authenticating;
        private int _maxConcurrentSudo;

        public string AcceptedPassword { get; set; } = string.Empty;
        public bool Echo { get; set; }
        public TimeSpan Delay { get; set; }
        /// <summary>When set, a sudo command waits for it after authenticating (a long-running command).</summary>
        public Task? Hold { get; set; }
        /// <summary>When set, a sudo command waits for it before sudo answers (a frozen host, PAM still deciding).</summary>
        public Task? Freeze { get; set; }
        /// <summary>Replaces what sudo does with the password (an unknown answer, a dropped connection ...).</summary>
        public Func<RemoteCommandResult>? SudoAnswer { get; set; }
        /// <summary>What a command without the password prints, instead of "ok".</summary>
        public string? PlainOutput { get; set; }
        public IReadOnlyList<SessionCall> Calls => _calls.ToArray();
        public int SudoAttempts => Volatile.Read(ref _sudoAttempts);
        /// <summary>The most password sudo commands that were authenticating at the same time.</summary>
        public int MaxConcurrentSudo => Volatile.Read(ref _maxConcurrentSudo);
        public bool IsConnected => true;

        public void Dispose() { }

        public Task<RemoteCommandResult> ExecuteAsync(string command, TimeSpan timeout, CancellationToken cancellationToken) =>
            ExecuteAsync(command, null, null, timeout, cancellationToken);

        public Task<RemoteCommandResult> ExecuteAsync(string command, string? standardInput, TimeSpan timeout, CancellationToken cancellationToken) =>
            ExecuteAsync(command, standardInput, null, timeout, cancellationToken);

        public async Task<RemoteCommandResult> ExecuteAsync(string command, string? standardInput, OutputMarker? marker, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var call = new SessionCall(command, standardInput, timeout);
            _calls.Enqueue(call);
            if (!call.IsSudoS) return Result(0, command.StartsWith("printf", StringComparison.Ordinal) ? command : PlainOutput ?? "ok", "");

            Assert.NotNull(marker);
            Assert.Contains($"\"{marker.Value}\"", command, StringComparison.Ordinal);
            Interlocked.Increment(ref _sudoAttempts);
            var authenticating = Interlocked.Increment(ref _authenticating);
            InterlockedMax(ref _maxConcurrentSudo, authenticating);
            var stillAuthenticating = true;
            try
            {
                if (Freeze is { } freeze) await freeze.WaitAsync(timeout, cancellationToken);
                if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
                if (SudoAnswer is { } answer) return answer();
                if (standardInput != AcceptedPassword + "\n")
                    return Result(1, "", "Sorry, try again.\nsudo: no password was provided\nsudo: 1 incorrect password attempt\n");

                marker.MarkSeen();
                stillAuthenticating = false;
                Interlocked.Decrement(ref _authenticating);
                if (Hold is { } hold) await hold.WaitAsync(cancellationToken);
                if (command.Contains("failing-command", StringComparison.Ordinal)) return Result(1, "", "failing-command: not found");
                var stdout = marker.Value + "\n" + (Echo ? $"read: {standardInput}" : "ok");
                return Result(0, marker.Strip(stdout), Echo ? $"note {AcceptedPassword} end" : "");
            }
            finally
            {
                if (stillAuthenticating) Interlocked.Decrement(ref _authenticating);
            }
        }

        public async Task WaitForSudoAttemptsAsync(int count)
        {
            for (var index = 0; index < 200 && SudoAttempts < count; index++) await Task.Delay(10);
            Assert.Equal(count, SudoAttempts);
        }

        private static RemoteCommandResult Result(int exitCode, string stdout, string stderr) =>
            new(exitCode, stdout, stderr, TimeSpan.FromMilliseconds(3));

        private static void InterlockedMax(ref int target, int value)
        {
            for (var current = Volatile.Read(ref target); value > current; current = Volatile.Read(ref target))
                if (Interlocked.CompareExchange(ref target, value, current) == current) return;
        }
    }

    private sealed class SessionPool(ISshCommandSession session) : ISshConnectionPool
    {
        private int _connects;

        /// <summary>When set, connecting waits for it (a slow connect).</summary>
        public Task? Connect { get; set; }
        public int Connects => Volatile.Read(ref _connects);

        public async Task<ISshCommandSession> GetSessionAsync(Guid serverId, bool bypassBackoff, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _connects);
            if (Connect is { } connect) await connect.WaitAsync(cancellationToken);
            return session;
        }
        public void Evict(Guid serverId) { }
        public void Dispose() { }
    }

    private sealed class NoPool : ISshConnectionPool
    {
        public Task<ISshCommandSession> GetSessionAsync(Guid serverId, bool bypassBackoff, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("not used");
        public void Evict(Guid serverId) { }
        public void Dispose() { }
    }

    /// <summary>Stores protected values as opaque tokens, so the database never holds the plain password.</summary>
    internal sealed class MappingProtector : ISecretProtector
    {
        private readonly ConcurrentDictionary<string, string> _values = new();
        public bool Broken { get; set; }

        public string Protect(string plaintext)
        {
            var token = $"dp:{Guid.NewGuid():N}";
            _values[token] = plaintext;
            return token;
        }

        public string Unprotect(string protectedValue) => Broken
            ? throw new System.Security.Cryptography.CryptographicException("The key was not found in the key ring.")
            : _values[protectedValue];
    }

    private sealed class KeyStore : IServerKeyStore
    {
        public IReadOnlyList<string> ListFileNames() => ["id_ed25519"];
        public bool Contains(string fileName) => fileName == "id_ed25519";
        public string GetFullPath(string fileName) => Path.Combine(Path.GetTempPath(), fileName);
    }

    /// <summary>Formats every log entry, structured values included, so a test can search all of it.</summary>
    internal sealed class ListLogger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<string> _messages = new();
        public IReadOnlyList<string> Messages => _messages.ToArray();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs ? string.Join(";", pairs.Select(pair => $"{pair.Key}={pair.Value}")) : "";
            _messages.Enqueue($"{logLevel} {formatter(state, exception)} | {values} | {exception}");
        }
    }

    /// <summary>A clock whose timers fire only when the test says so.</summary>
    private sealed class TimerClock : TimeProvider
    {
        private readonly ConcurrentQueue<(TimerCallback Callback, object? State)> _timers = new();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _timers.Enqueue((callback, state));
            return new NoTimer();
        }

        public void FireAll()
        {
            while (_timers.TryDequeue(out var timer)) timer.Callback(timer.State);
        }

        private sealed class NoTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
