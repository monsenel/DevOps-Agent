using DevOpsAgent.Application.Abstractions;

namespace DevOpsAgent.Infrastructure.Ssh;

/// <summary>
/// Sends commands through the pool. On a server with a stored sudo password (T7.R24, <see cref="SudoPasswordGate"/>) sudo runs
/// as <c>sudo -S -p ''</c> with the password written to stdin, one password sudo at a time while it authenticates, a rejected
/// password pauses every further sudo on that server, and the password is removed from everything the server sends back.
/// Without a stored password, sudo is <c>sudo -n</c> exactly as before.
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

        return await RunWithSudoPasswordAsync(serverId, RemoteCommand.WrapWithSudoPassword(command),
            RemoteCommand.Wrap(command, useSudo: true), credential, options, cancellationToken);
    }

    public async Task<RemoteCommandResult> ProbeSudoAsync(Guid serverId, SshRunOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        options = options with { UseSudo = false };
        if (sudoPasswords is null) return await RunAsync(serverId, RemoteCommand.SudoProbe, options, cancellationToken);

        var credential = await sudoPasswords.ReadAsync(serverId, cancellationToken);
        if (credential.Kind == SudoCredentialKind.None)
        {
            return await ExecuteAsync(serverId, RemoteCommand.SudoProbe, null, options, cancellationToken);
        }

        // A rejected password is not tried again by the probe either (D-040 amendment): one wrong password can cost two
        // pam_faillock failures, so only an edit or removal of the password lifts the pause. RunWithSudoPasswordAsync
        // returns the "not run" result for a paused credential without contacting the server.
        return await RunWithSudoPasswordAsync(serverId, RemoteCommand.SudoPasswordProbe, RemoteCommand.SudoProbe,
            credential, options, cancellationToken);
    }

    private async Task<RemoteCommandResult> RunWithSudoPasswordAsync(
        Guid serverId,
        string passwordCommand,
        string noPasswordCommand,
        SudoCredential credential,
        SshRunOptions options,
        CancellationToken cancellationToken)
    {
        var gate = sudoPasswords!;
        if (NotRunReason(credential) is { } early) return SudoPasswordGate.Redact(SudoPasswordGate.NotRun(early), credential.Password);

        var startedAt = _time.GetTimestamp();
        using var turn = await gate.EnterAsync(serverId, options.Timeout, cancellationToken);
        // Read again inside the turn: the command that held it may just have had the password rejected, or the owner edited it.
        credential = await gate.ReadAsync(serverId, cancellationToken);
        if (credential.Kind == SudoCredentialKind.None)
        {
            return await ExecuteAsync(serverId, noPasswordCommand, null, options, cancellationToken);
        }

        if (NotRunReason(credential) is { } reason) return SudoPasswordGate.Redact(SudoPasswordGate.NotRun(reason), credential.Password);

        var remaining = options.Timeout - _time.GetElapsedTime(startedAt);
        if (remaining <= TimeSpan.Zero)
        {
            throw new TimeoutException("Waiting for this server's sudo turn used up the command's deadline; the command was not started.");
        }

        turn.ReleaseAfter(SudoPasswordGate.AuthenticationWindow, _time);
        var result = await ExecuteAsync(serverId, passwordCommand, credential.Password + "\n",
            options with { Timeout = remaining }, cancellationToken);
        if (SudoPasswordGate.IsPasswordRejected(result))
        {
            // Recorded before the turn is released, so a command waiting for it reads the rejection and never tries.
            await gate.RecordRejectedAsync(serverId, credential.ProtectedValue!);
        }

        return SudoPasswordGate.Redact(result, credential.Password);
    }

    private static string? NotRunReason(SudoCredential credential) => credential.Kind switch
    {
        SudoCredentialKind.Paused => SudoPasswordGate.PausedMessage,
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
}
