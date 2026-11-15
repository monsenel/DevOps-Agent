using System.Net.Sockets;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Servers;
using DevOpsAgent.Infrastructure.Ssh;
using Renci.SshNet.Common;
using Renci.SshNet.Messages.Transport;

namespace DevOpsAgent.Tests;

/// <summary>
/// T7.R19: a failed login (rejected key, missing/unreadable/invalid key file, missing or wrong passphrase) is a state of its
/// own, "auth failed", never "unreachable"; it is retried every minute, never less often, and its wording is safe.
/// </summary>
public sealed class SshAuthFailureTests
{
    // Carried by the fake exceptions below: whatever happens, none of it may reach a message, alert, log or the UI.
    private const string KeyCanary = "-----BEGIN OPENSSH PRIVATE KEY----- b3BlbnNzaC1rZXktdjEAAAAACmFlczI1Ni1jdHI";
    private const string PassphraseCanary = "hunter2-passphrase-canary";

    private static readonly TimeSpan Short = TimeSpan.FromSeconds(5);

    private static Exception LoginError(string name) => name switch
    {
        "rejected" => new SshAuthenticationException($"Permission denied (publickey). {KeyCanary} {PassphraseCanary}"),
        "missing-key-file" => new FileNotFoundException($"Could not find file '/keys/{PassphraseCanary}'. {KeyCanary}", $"/keys/{PassphraseCanary}"),
        "missing-key-directory" => new DirectoryNotFoundException($"Could not find a part of the path '/keys/{PassphraseCanary}'."),
        "unreadable-key" => new UnauthorizedAccessException($"Access to the path '/keys/{PassphraseCanary}' is denied. {KeyCanary}"),
        "passphrase-needed" => new SshPassPhraseNullOrEmptyException($"Private key is encrypted but passphrase is empty. {PassphraseCanary}"),
        "invalid-key" => new SshAuthFailedException(SshAuthFailure.KeyInvalidOrWrongPassphrase),
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    [Theory]
    [InlineData("rejected", "the server rejected the SSH key")]
    [InlineData("missing-key-file", "key file not found")]
    [InlineData("missing-key-directory", "key file not found")]
    [InlineData("unreadable-key", "key file cannot be read")]
    [InlineData("passphrase-needed", "the key needs a passphrase and none is stored")]
    [InlineData("invalid-key", "wrong passphrase or invalid key file")]
    public async Task EveryLoginAndKeyErrorIsAuthFailedNeverUnreachable(string error, string reason)
    {
        using var lab = new HangLab();
        var host = lab.Add("srv", "10.0.0.1", script => script.ConnectFails = () => LoginError(error));

        var exception = await Assert.ThrowsAsync<HostUnreachableException>(() => lab.Run(host, Short));

        Assert.Equal(HostFailureKind.AuthFailed, exception.Kind);
        Assert.True(exception.IsAuthFailure);
        Assert.StartsWith($"srv: authentication failed ({reason}). Since ", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("unreachable", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("could not connect", exception.Message, StringComparison.OrdinalIgnoreCase);
        var state = Assert.IsType<HostReachabilityState>(lab.Reachability.Get(host.Id));
        Assert.Equal(HostFailureKind.AuthFailed, state.Kind);
        Assert.Equal(reason, state.Cause);
        // The next caller is skipped at once, with the same kind, and no new attempt is made.
        var second = await Assert.ThrowsAsync<HostUnreachableException>(() => lab.Run(host, Short));
        Assert.Equal(HostFailureKind.AuthFailed, second.Kind);
        Assert.Equal(1, lab.Transports.Attempts(host));
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("missing-key-file")]
    [InlineData("unreadable-key")]
    [InlineData("passphrase-needed")]
    [InlineData("invalid-key")]
    public async Task NoSecretOrLibraryTextIsEverEchoed(string error)
    {
        using var lab = new HangLab();
        var host = lab.Add("srv", "10.0.0.1", script => script.ConnectFails = () => LoginError(error));
        var jumped = lab.Add("app", "10.0.0.2", jump: host);

        var direct = await Assert.ThrowsAsync<HostUnreachableException>(() => lab.Run(host, Short));
        var behind = await Assert.ThrowsAsync<HostUnreachableException>(() => lab.Run(jumped, Short));
        var state = lab.Reachability.Get(host.Id)!;

        var everything = new[]
        {
            direct.Message, direct.ToString(), behind.Message, behind.ToString(), state.LastError, state.Cause ?? string.Empty,
            state.LastFailure!.ToString(), HostUnreachableException.Describe("srv", state), HostUnreachableException.Describe("app", state, "srv"),
            ServerReachability.Describe(jumped.Id, lab.Map(), lab.Reachability) ?? string.Empty,
            ServerReachability.Describe(host.Id, lab.Map(), lab.Reachability) ?? string.Empty
        };
        Assert.All(everything, text =>
        {
            Assert.DoesNotContain("PRIVATE KEY", text, StringComparison.Ordinal);
            Assert.DoesNotContain(KeyCanary, text, StringComparison.Ordinal);
            Assert.DoesNotContain(PassphraseCanary, text, StringComparison.Ordinal);
            Assert.DoesNotContain("/keys", text, StringComparison.Ordinal);
            Assert.DoesNotContain("dva", text, StringComparison.Ordinal); // the SSH user
        });
        // The original exception, which carries the library's text, is not kept at all.
        Assert.IsType<SshAuthFailedException>(state.LastFailure);
        Assert.Null(state.LastFailure!.InnerException);
    }

    [Theory]
    [InlineData("refused", "connection refused")]
    [InlineData("timeout", "connection timed out")]
    [InlineData("socket-timeout", "connection timed out")]
    [InlineData("reset", "connection reset by the peer")]
    [InlineData("dns", "host name could not be resolved")]
    [InlineData("noroute", "no route to host")]
    [InlineData("ssh-closed", "the SSH connection was closed or the SSH handshake failed")]
    [InlineData("host-key", "host key mismatch (the presented key does not match the stored key)")]
    public async Task ConnectionLevelFailuresKeepTheUnreachableSemanticsAndNameTheirCause(string failure, string cause)
    {
        using var lab = new HangLab();
        Exception Make() => failure switch
        {
            "refused" => new SocketException((int)SocketError.ConnectionRefused),
            "timeout" => new SshOperationTimeoutException($"Connecting timed out. {PassphraseCanary}"),
            "socket-timeout" => new SocketException((int)SocketError.TimedOut),
            "reset" => new SocketException((int)SocketError.ConnectionReset),
            "dns" => new SocketException((int)SocketError.HostNotFound),
            "noroute" => new SocketException((int)SocketError.HostUnreachable),
            "ssh-closed" => new SshConnectionException($"Server banner: {KeyCanary}", DisconnectReason.ConnectionLost),
            _ => new SshHostKeyMismatchException("10.0.0.1", 22, "abc")
        };
        var host = lab.Add("srv", "10.0.0.1", script => script.ConnectFails = Make);

        var exception = await Assert.ThrowsAsync<HostUnreachableException>(() => lab.Run(host, Short));

        Assert.Equal(HostFailureKind.Connect, exception.Kind);
        Assert.False(exception.IsAuthFailure);
        Assert.StartsWith("srv is unreachable (since ", exception.Message, StringComparison.Ordinal);
        Assert.EndsWith(": could not connect.", exception.Message, StringComparison.Ordinal);
        Assert.Equal(cause, exception.Cause);
        Assert.DoesNotContain(PassphraseCanary, exception.Cause, StringComparison.Ordinal);
        Assert.DoesNotContain(KeyCanary, exception.Cause, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBackoffOfAFailedLoginIsOneMinuteFlatWhileAnOutageStillDoublesToFifteen()
    {
        var clock = new ManualClock();
        var tracker = new HostReachabilityTracker(clock);
        var login = Guid.NewGuid();
        var outage = Guid.NewGuid();

        // D-039 amendment: after AuthFailedAttemptLimit (3) consecutive failures the retries stop; this test covers the
        // attempts that are still automatic (the cadence of the pause itself is in SshAuthFailurePauseTests).
        var seen = new List<TimeSpan>();
        for (var failure = 1; failure < IHostReachability.AuthFailedAttemptLimit; failure++)
        {
            Assert.True(tracker.TryBeginAttempt(login, bypassBackoff: false, out _));
            var state = tracker.RecordFailure(login, new SshAuthFailedException(SshAuthFailure.ServerRejectedKey));
            Assert.Equal(HostFailureKind.AuthFailed, state.Kind);
            Assert.Equal(failure, state.ConsecutiveFailures);
            seen.Add(state.NextRetryUtc - clock.GetUtcNow());
            // Nothing earlier than the minute (less the one second of slack); at the minute the retry is allowed again.
            clock.Advance(TimeSpan.FromSeconds(58));
            Assert.False(tracker.TryBeginAttempt(login, bypassBackoff: false, out _));
            clock.Advance(TimeSpan.FromSeconds(2));
        }

        Assert.All(seen, wait => Assert.Equal(TimeSpan.FromMinutes(1), wait));
        Assert.Equal(TimeSpan.FromMinutes(1), HostReachabilityTracker.AuthFailedBackoff);
        Assert.All(Enumerable.Range(1, 200), count =>
            Assert.True(HostReachabilityTracker.BackoffFor(HostFailureKind.AuthFailed, count) <= TimeSpan.FromMinutes(1)));

        // Regression: a connection failure on another host still doubles 1, 2, 4, 8, 15, 15 minutes.
        var doubled = new List<int>();
        for (var failure = 1; failure <= 6; failure++)
        {
            var state = tracker.RecordFailure(outage, new SocketException((int)SocketError.TimedOut));
            Assert.Equal(HostFailureKind.Connect, state.Kind);
            doubled.Add((int)(state.NextRetryUtc - clock.GetUtcNow()).TotalMinutes);
        }

        Assert.Equal(new[] { 1, 2, 4, 8, 15, 15 }, doubled);
    }

    [Fact]
    public void ACycleThatLandsJustBeforeTheMinuteIsOverStillRetriesALoginFailureButNotAnOutage()
    {
        var clock = new ManualClock();
        var tracker = new HostReachabilityTracker(clock);
        var login = Guid.NewGuid();
        var outage = Guid.NewGuid();
        tracker.RecordFailure(login, new SshAuthFailedException(SshAuthFailure.ServerRejectedKey));
        tracker.RecordFailure(outage, new SocketException((int)SocketError.TimedOut));

        // 40 ms early, as a 30 s cycle phase-locked to the failure lands: the login is retried, the outage is not.
        clock.Advance(TimeSpan.FromMinutes(1) - TimeSpan.FromMilliseconds(40));
        Assert.False(tracker.IsBlocked(login, out _));
        Assert.True(tracker.TryBeginAttempt(login, bypassBackoff: false, out _));
        Assert.True(tracker.IsBlocked(outage, out _));
        Assert.False(tracker.TryBeginAttempt(outage, bypassBackoff: false, out _));
        // But not a whole second early.
        var other = Guid.NewGuid();
        tracker.RecordFailure(other, new SshAuthFailedException(SshAuthFailure.ServerRejectedKey));
        clock.Advance(TimeSpan.FromSeconds(58));
        Assert.True(tracker.IsBlocked(other, out _));
    }

    [Fact]
    public void SinceIsKeptAcrossLoginFailuresAndRestartsWhenTheCauseChanges()
    {
        var clock = new ManualClock();
        var tracker = new HostReachabilityTracker(clock);
        var id = Guid.NewGuid();

        var first = tracker.RecordFailure(id, new SshAuthFailedException(SshAuthFailure.KeyFileNotFound));
        clock.Advance(TimeSpan.FromMinutes(3));
        var second = tracker.RecordFailure(id, new SshAuthFailedException(SshAuthFailure.ServerRejectedKey));
        Assert.Equal(first.UnreachableSinceUtc, second.UnreachableSinceUtc);
        Assert.Equal(2, second.ConsecutiveFailures);
        Assert.Equal("the server rejected the SSH key", second.Cause);

        // The login now works but the host stops answering: a new situation, with the first (not the doubled) backoff.
        clock.Advance(TimeSpan.FromMinutes(2));
        var outage = tracker.RecordFailure(id, new SocketException((int)SocketError.TimedOut));
        Assert.Equal(HostFailureKind.Connect, outage.Kind);
        Assert.Equal(1, outage.ConsecutiveFailures);
        Assert.Equal(clock.GetUtcNow(), outage.UnreachableSinceUtc);
        Assert.Equal(TimeSpan.FromMinutes(1), outage.NextRetryUtc - clock.GetUtcNow());

        // ... and back: an outage that turns into a login failure does not inherit the long backoff either.
        for (var failure = 0; failure < 4; failure++) tracker.RecordFailure(id, new SocketException((int)SocketError.TimedOut));
        var login = tracker.RecordFailure(id, new SshAuthFailedException(SshAuthFailure.PassphraseRequired));
        Assert.Equal(HostFailureKind.AuthFailed, login.Kind);
        Assert.Equal(1, login.ConsecutiveFailures);
        Assert.Equal(TimeSpan.FromMinutes(1), login.NextRetryUtc - clock.GetUtcNow());
    }

    [Fact]
    public void ASuccessfulConnectionClearsAFailedLoginAndAHostResetClearsItToo()
    {
        var tracker = new HostReachabilityTracker(new ManualClock());
        var id = Guid.NewGuid();

        tracker.RecordFailure(id, new SshAuthFailedException(SshAuthFailure.ServerRejectedKey));
        tracker.RecordSuccess(id);
        Assert.Null(tracker.Get(id));

        tracker.RecordFailure(id, new SshAuthFailedException(SshAuthFailure.ServerRejectedKey));
        tracker.Reset(id);
        Assert.Null(tracker.Get(id));

        // A command timeout is no news for a host that is not logged in: it does not turn the state into something else.
        tracker.RecordFailure(id, new SshAuthFailedException(SshAuthFailure.ServerRejectedKey));
        for (var count = 0; count < IHostReachability.CommandTimeoutThreshold + 1; count++)
            Assert.Null(tracker.RecordCommandTimeout(id, new TimeoutException(), force: true));
        Assert.Equal(HostFailureKind.AuthFailed, tracker.Get(id)!.Kind);
    }

    [Fact]
    public async Task ThePoolRetriesAFailedLoginEveryMinuteUntilTheLimitAndRetryNowEditAndSuccessClearIt()
    {
        var clock = new ManualClock();
        using var lab = new HangLab(clock);
        var host = lab.Add("srv", "10.0.0.1", script => script.ConnectFails = () => new SshAuthenticationException("Permission denied"));

        // D-039 amendment: the automatic attempts are limited to the first three (the pause is tested in
        // SshAuthFailurePauseTests); each is at the end of the previous minute: never a longer wait.
        for (var failure = 1; failure < IHostReachability.AuthFailedAttemptLimit; failure++)
        {
            await Assert.ThrowsAsync<HostUnreachableException>(() => lab.Run(host, Short));
            var state = lab.Reachability.Get(host.Id)!;
            Assert.Equal(HostFailureKind.AuthFailed, state.Kind);
            Assert.Equal(failure, state.ConsecutiveFailures);
            Assert.Equal(TimeSpan.FromMinutes(1), state.NextRetryUtc - clock.GetUtcNow());
            Assert.Equal(failure, lab.Transports.Attempts(host));

            // Inside the minute the host is skipped without an attempt.
            clock.Advance(TimeSpan.FromSeconds(58));
            await Assert.ThrowsAsync<HostUnreachableException>(() => lab.Run(host, Short));
            Assert.Equal(failure, lab.Transports.Attempts(host));
            clock.Advance(TimeSpan.FromSeconds(2));
        }

        // "Retry now" attempts whenever it is pressed; it is the third failure in a row and pauses the host.
        await Assert.ThrowsAsync<HostUnreachableException>(() => lab.Run(host, Short, bypass: true));
        Assert.Equal(3, lab.Transports.Attempts(host));
        Assert.True(lab.Reachability.Get(host.Id)!.AutomaticRetriesPaused);

        // Editing the server (Evict) clears the state and the next call tries at once with the new settings.
        lab.Pool.Evict(host.Id);
        Assert.Null(lab.Reachability.Get(host.Id));
        await Assert.ThrowsAsync<HostUnreachableException>(() => lab.Run(host, Short));
        Assert.Equal(4, lab.Transports.Attempts(host));

        // The key is fixed: the next retry (or Retry now) connects and the state is gone.
        lab.Transports.Script(host.Host).ConnectFails = null;
        clock.Advance(TimeSpan.FromMinutes(1));
        await lab.Run(host, Short);
        Assert.Null(lab.Reachability.Get(host.Id));
        Assert.Equal(5, lab.Transports.Attempts(host));
    }

    [Fact]
    public async Task RetryNowFixesAFailedLoginBeforeTheMinuteIsOver()
    {
        var clock = new ManualClock();
        using var lab = new HangLab(clock);
        var host = lab.Add("srv", "10.0.0.1", script => script.ConnectFails = () => new SshAuthenticationException("Permission denied"));
        await Assert.ThrowsAsync<HostUnreachableException>(() => lab.Run(host, Short));

        lab.Transports.Script(host.Host).ConnectFails = null;
        clock.Advance(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<HostUnreachableException>(() => lab.Run(host, Short));
        await lab.Run(host, Short, bypass: true);

        Assert.Null(lab.Reachability.Get(host.Id));
    }

    [Fact]
    public async Task DependentsBehindAJumpHostWhoseLoginFailedReportTheJumpHostsRealCause()
    {
        var clock = new ManualClock();
        using var lab = new HangLab(clock);
        var edge = lab.Add("edge", "10.0.0.1", script => script.ConnectFails = () => new FileNotFoundException("no such key"));
        var app = lab.Add("app", "10.0.0.2", jump: edge);
        var db = lab.Add("db", "10.0.0.3", jump: edge);

        var direct = await Assert.ThrowsAsync<HostUnreachableException>(() => lab.Run(edge, Short));
        var viaApp = await Assert.ThrowsAsync<HostUnreachableException>(() => lab.Run(app, Short));
        var viaDb = await Assert.ThrowsAsync<HostUnreachableException>(() => lab.Run(db, Short));

        Assert.StartsWith("edge: authentication failed (key file not found).", direct.Message, StringComparison.Ordinal);
        Assert.StartsWith("app via jump host edge: authentication failed (key file not found).", viaApp.Message, StringComparison.Ordinal);
        Assert.StartsWith("db via jump host edge: authentication failed (key file not found).", viaDb.Message, StringComparison.Ordinal);
        Assert.All(new[] { viaApp, viaDb }, exception =>
        {
            Assert.Equal(HostFailureKind.AuthFailed, exception.Kind);
            Assert.DoesNotContain("unreachable", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("key file not found", exception.Cause);
        });
        // Only the failing hop is attempted and earns the state; nothing behind it is tried.
        Assert.Equal(1, lab.Transports.Attempts(edge));
        Assert.Equal(0, lab.Transports.Attempts(app));
        Assert.Equal(0, lab.Transports.Attempts(db));
        Assert.Null(lab.Reachability.Get(app.Id));

        // The Servers page reads the same state through the jump chain.
        var status = ServerReachability.Status(app.Id, lab.Map(), lab.Reachability)!;
        Assert.Equal(HostFailureKind.AuthFailed, status.Kind);
        Assert.Equal("Auth failed", status.Badge);
        Assert.StartsWith("app via jump host edge: authentication failed (key file not found).", status.Text, StringComparison.Ordinal);
        Assert.Equal("Auth failed", ServerReachability.Status(edge.Id, lab.Map(), lab.Reachability)!.Badge);

        // Fixing the jump host's key frees the whole chain.
        lab.Transports.Script(edge.Host).ConnectFails = null;
        clock.Advance(TimeSpan.FromMinutes(1));
        await lab.Run(app, Short);
        Assert.Null(lab.Reachability.Get(edge.Id));
        Assert.Null(ServerReachability.Status(app.Id, lab.Map(), lab.Reachability));
    }

    [Fact]
    public void ADeadHostAndADeadJumpHostStillReadAsUnreachableInTheServersPage()
    {
        var clock = new ManualClock();
        var tracker = new HostReachabilityTracker(clock);
        var edge = Guid.NewGuid();
        var app = Guid.NewGuid();
        var inventory = new Dictionary<Guid, (string Name, Guid? JumpServerId)> { [edge] = ("edge", null), [app] = ("app", edge) };
        tracker.RecordFailure(edge, new SocketException((int)SocketError.TimedOut));

        var status = ServerReachability.Status(app, inventory, tracker)!;

        Assert.Equal(HostFailureKind.Connect, status.Kind);
        Assert.Equal("Unreachable", status.Badge);
        Assert.StartsWith("app is unreachable via jump host edge (since ", status.Text, StringComparison.Ordinal);
    }
}
