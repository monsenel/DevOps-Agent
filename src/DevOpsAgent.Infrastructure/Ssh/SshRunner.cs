using DevOpsAgent.Application.Abstractions;

namespace DevOpsAgent.Infrastructure.Ssh;

/// <summary>
/// Sends commands through the pool. On a server with a stored sudo password (T7.R24, T7.R25, <see cref="SudoPasswordGate"/>)
/// sudo runs as <c>sudo -S -p ''</c> with the password written to stdin and a random marker the command prints first: only the
/// marker coming back confirms the password. Without it the attempt is a rejection or "outcome unknown", and either pauses
/// password sudo on that server. One password sudo per server runs at a time until it is confirmed or its outcome is stored,
/// none starts with less than <see cref="SudoPasswordGate.MinimumTimeLeft"/> left, and the password is removed from
/// everything the server sends back. Without a stored password, sudo is <c>sudo -n</c> exactly as before.
/// </summary>
public sealed class SshRunner(
    ISshConnectionPool connectionPool,
    SudoPasswordGate? sudoPasswords = null,
    TimeProvider? timeProvider = null) : ISshRunner
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<RemoteCommandResult> RunAsync(
        Guid serverId,
        string command,
        SshRunOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Command timeout must be positive.");
        }

        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException("Command is required.", nameof(command));
        }

        if (sudoPasswords is null)
        {
            return await ExecuteAsync(serverId, RemoteCommand.Wrap(command, options.UseSudo), null, options, cancellationToken);
        }

        var credential = await sudoPasswords.ReadAsync(serverId, cancellationToken);
        if (!options.UseSudo || credential.Kind == SudoCredentialKind.None)
        {
            // Every command's output goes through the server's redaction list, not only sudo's.
            var result = await ExecuteAsync(serverId, RemoteCommand.Wrap(command, options.UseSudo), null, options, cancellationToken);
            return SudoPasswordGate.Redact(result, credential.Password);
        }

        return await RunWithSudoPasswordAsync(serverId, marker => RemoteCommand.WrapWithSudoPassword(command, marker),
            RemoteCommand.Wrap(command, useSudo: true), credential, options, cancellationToken);
    }

    public async Task<RemoteCommandResult> ProbeSudoAsync(Guid serverId, SshRunOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        options = options with { UseSudo = false };
        if (sudoPasswords is null) return await RunAsync(serverId, RemoteCommand.SudoProbe, options, cancellationToken);

        // The owner pressed Retry now / Test connection: an "outcome unknown" pause is lifted and the password tried once
        // (T7.R25). A rejected password is not (D-040 amendment 1): one wrong password can cost two pam_faillock failures, so
        // only an edit or removal lifts that pause, and RunWithSudoPasswordAsync returns the "not run" result for it.
        await sudoPasswords.ClearOutcomeUnknownAsync(serverId, cancellationToken);
        var credential = await sudoPasswords.ReadAsync(serverId, cancellationToken);
        if (credential.Kind == SudoCredentialKind.None)
        {
            return await ExecuteAsync(serverId, RemoteCommand.SudoProbe, null, options, cancellationToken);
        }

        return await RunWithSudoPasswordAsync(serverId, RemoteCommand.SudoPasswordProbe, RemoteCommand.SudoProbe,
            credential, options, cancellationToken);
    }

    private async Task<RemoteCommandResult> RunWithSudoPasswordAsync(
        Guid serverId,
        Func<string, string> passwordCommand,
        string noPasswordCommand,
        SudoCredential credential,
        SshRunOptions options,
        CancellationToken cancellationToken)
    {
        var gate = sudoPasswords!;
        if (NotRunReason(credential) is { } early) return SudoPasswordGate.Redact(SudoPasswordGate.NotRun(early), credential.Password);

        var startedAt = _time.GetTimestamp();
        // What is left of the call's own deadline and of its cycle's, whichever ends first.
        TimeSpan Left()
        {
            var left = options.Timeout - _time.GetElapsedTime(startedAt);
            if (options.NotAfterUtc is { } notAfter && notAfter - _time.GetUtcNow() is var cycle && cycle < left) left = cycle;
            return left;
        }

        void EnsureTimeLeft(string when)
        {
            if (Left() < SudoPasswordGate.MinimumTimeLeft)
                throw new TimeoutException(
                    $"Less than {SudoPasswordGate.MinimumTimeLeft.TotalSeconds:0} s of the deadline were left {when}; the password sudo was not started.");
        }

        using var turn = await gate.EnterAsync(serverId, Left() - SudoPasswordGate.MinimumTimeLeft, cancellationToken);
        // Read again inside the turn: the command that held it may just have had its outcome recorded, or the owner edited it.
        credential = await gate.ReadAsync(serverId, cancellationToken);
        if (credential.Kind == SudoCredentialKind.None)
        {
            return await ExecuteAsync(serverId, noPasswordCommand, null, options, cancellationToken);
        }

        if (NotRunReason(credential) is { } reason) return SudoPasswordGate.Redact(SudoPasswordGate.NotRun(reason), credential.Password);
        EnsureTimeLeft("after waiting for this server's sudo turn");

        // Connected before anything is recorded: a host that cannot be reached never gets the password and costs no attempt.
        using var session = await connectionPool.GetSessionAsync(serverId, options.BypassBackoff, Left(), cancellationToken);
        EnsureTimeLeft("after connecting");
        var protectedValue = credential.ProtectedValue!;
        if (!await gate.BeginAttemptAsync(serverId, protectedValue))
        {
            return SudoPasswordGate.Redact(SudoPasswordGate.NotRun(SudoPasswordGate.AttemptNotRecordedMessage), credential.Password);
        }

        var marker = OutputMarker.Create();
        var outcome = new AttemptOutcome();
        // The marker can come back long before a slow command ends: then the password is confirmed and the turn released at
        // once, so other sudo on this server does not wait for, say, a package upgrade.
        var confirming = ConfirmWhenSeenAsync();
        try
        {
            var result = await session.ExecuteAsync(passwordCommand(marker.Value), credential.Password + "\n", marker, Left(), cancellationToken);
            if (marker.IsSeen) await confirming;
            // No marker: sudo never let the command start. Its own rejection lines make it "rejected", anything else unknown.
            else if (outcome.TryDecide(AttemptOutcome.Unconfirmed)) await RecordUnconfirmedAsync(SudoPasswordGate.IsPasswordRejected(result));

            return SudoPasswordGate.Redact(result, credential.Password);
        }
        catch (Exception) when (marker.IsSeen)
        {
            // Authenticated, then the command failed, timed out or was cancelled: the password itself is fine.
            await confirming;
            throw;
        }
        catch (Exception)
        {
            // Timeout, cancellation, a lost connection, shutdown: whether sudo saw the password is unknown.
            if (outcome.TryDecide(AttemptOutcome.Unconfirmed)) await RecordUnconfirmedAsync(rejected: false);
            throw;
        }
        finally
        {
            gate.EndAttempt(serverId);
        }

        async Task ConfirmWhenSeenAsync()
        {
            await marker.Seen.ConfigureAwait(false);
            if (!outcome.TryDecide(AttemptOutcome.Confirmed)) return;
            await gate.RecordConfirmedAsync(serverId, protectedValue, credential.AlertOpen);
            gate.EndAttempt(serverId);
            turn.Dispose();
        }

        // Recorded before the turn is released, so a command waiting for it reads the pause and never tries.
        Task RecordUnconfirmedAsync(bool rejected) => rejected
            ? gate.RecordRejectedAsync(serverId, protectedValue)
            : gate.RecordOutcomeUnknownAsync(serverId, protectedValue);
    }

    private static string? NotRunReason(SudoCredential credential) => credential.Kind switch
    {
        SudoCredentialKind.Paused => credential.PausedMessage,
        SudoCredentialKind.Unusable => SudoPasswordGate.UnusableMessage,
        _ => null
    };

    private async Task<RemoteCommandResult> ExecuteAsync(
        Guid serverId,
        string wrappedCommand,
        string? standardInput,
        SshRunOptions options,
        CancellationToken cancellationToken)
    {
        // The timeout is the deadline of the whole call (T7.R17): connecting, or waiting for a connect another caller
        // started, is cut off at it and the command gets what is left. The lease is released in every outcome (result,
        // failure, timeout, cancellation).
        using var session = await connectionPool.GetSessionAsync(serverId, options.BypassBackoff, options.Timeout, cancellationToken);
        return await session.ExecuteAsync(wrappedCommand, standardInput, options.Timeout, cancellationToken);
    }

    /// <summary>Decided once: confirmed by the marker, or unconfirmed (rejected or unknown), whichever is seen first.</summary>
    private sealed class AttemptOutcome
    {
        public const int Confirmed = 1;
        public const int Unconfirmed = 2;
        private int _value;

        public bool TryDecide(int value) => Interlocked.CompareExchange(ref _value, value, 0) == 0;
    }
}
