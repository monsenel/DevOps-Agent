using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using DevOpsAgent.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DevOpsAgent.Infrastructure.Ssh;

/// <summary>
/// The per-server sudo password (T7.R24, T7.R25): reads it, takes care that it is never sent twice without a confirmed
/// outcome, and keeps it out of what the server sends back. Used by <see cref="SshRunner"/> only; nothing else ever sees the
/// plain password.
/// <para>
/// A password sudo counts as accepted only when the command's random marker line comes back (<see cref="OutputMarker"/>).
/// Without it the outcome is a rejection (sudo's own rejection lines) or unknown (timeout, lost connection, cancellation,
/// shutdown, any other message); both pause password sudo on that server, persisted. A rejection is lifted only by editing or
/// removing the password (pam_faillock, D-040 amendment 1); an unknown outcome also by Retry now / Test connection. The
/// attempt is written down before the password is sent, so an attempt cut off by a crash is found afterwards and counts as
/// unknown. Password sudo on a server runs one at a time until it is confirmed or its outcome is recorded.
/// </para>
/// <para>
/// It also holds the redaction list of every server (T7.R26, <see cref="IKnownSecrets"/>): all stored sudo passwords, so the
/// output of any server is cleaned of all of them. Background state changes are announced through
/// <see cref="ServerStateEvents"/>, so the Servers page shows them without a reload.
/// </para>
/// </summary>
public sealed partial class SudoPasswordGate(
    IServiceScopeFactory scopeFactory,
    ISecretProtector secretProtector,
    TimeProvider timeProvider,
    ILogger<SudoPasswordGate> logger,
    ServerStateEvents? events = null) : IKnownSecrets
{
    /// <summary>How long the list of all stored passwords is reused before it is read again (edits invalidate it at once).</summary>
    private static readonly TimeSpan SecretsLifetime = TimeSpan.FromSeconds(60);

    /// <summary>
    /// A password sudo is not started with less than this left of its call or cycle deadline (owner decision, T7.R25): PAM
    /// answers a wrong password within its 2-3 s failure delay, so the outcome is seen well before the deadline.
    /// </summary>
    public static readonly TimeSpan MinimumTimeLeft = TimeSpan.FromSeconds(20);

    public const string Redacted = "[REDACTED]";

    /// <summary>What a sudo command gets instead of running while the server's sudo password is rejected.</summary>
    public const string PausedMessage =
        "sudo: not run: the saved sudo password for this server was rejected; sudo is paused until the password is edited or removed.";

    /// <summary>What a sudo command gets instead of running while the outcome of the last attempt is unknown.</summary>
    public const string OutcomeUnknownMessage =
        "sudo: not run: the outcome of the last attempt with the saved sudo password is unknown; sudo is paused until Retry now, or until the password is edited or removed.";

    public const string UnusableMessage =
        "sudo: not run: the saved sudo password for this server cannot be decrypted; enter it again in the server settings.";

    public const string AttemptNotRecordedMessage =
        "sudo: not run: the attempt could not be recorded before sending the sudo password; try again later.";

    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _turns = new();

    /// <summary>
    /// Paused passwords (their protected value) as seen by this process, kept besides the stored state: if storing a rejection
    /// or an unknown outcome fails, the password is still never sent again until it is edited (a new protected value), removed,
    /// or (unknown only) Retry now is pressed.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, (string ProtectedValue, bool Rejected)> _paused = new();

    /// <summary>Servers with a password sudo of this process between "attempt started" and its recorded outcome.</summary>
    private readonly ConcurrentDictionary<Guid, byte> _inFlight = new();

    /// <summary>Decrypted passwords by protected value, so a refresh of the list does not decrypt them again.</summary>
    private readonly ConcurrentDictionary<string, string> _plain = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _secretsLoad = new(1, 1);
    private volatile SecretsSnapshot? _secrets;

    private sealed record SecretsSnapshot(IReadOnlyList<string> Secrets, long LoadedAt);

    public async Task<IReadOnlyList<string>> GetAsync(CancellationToken cancellationToken)
    {
        if (_secrets is { } current && timeProvider.GetElapsedTime(current.LoadedAt) < SecretsLifetime) return current.Secrets;
        await _secretsLoad.WaitAsync(cancellationToken);
        try
        {
            if (_secrets is { } loaded && timeProvider.GetElapsedTime(loaded.LoadedAt) < SecretsLifetime) return loaded.Secrets;
            await using var scope = scopeFactory.CreateAsyncScope();
            var servers = await scope.ServiceProvider.GetRequiredService<IServerRepository>().ListAsync(cancellationToken);
            var secrets = new List<string>();
            foreach (var protectedValue in servers.Select(server => server.ProtectedSudoPassword).OfType<string>().Where(value => value.Length > 0))
            {
                if (!_plain.TryGetValue(protectedValue, out var plain))
                {
                    try { plain = _plain[protectedValue] = secretProtector.Unprotect(protectedValue); }
                    catch (Exception exception) when (exception is not OperationCanceledException) { continue; } // logged by ReadAsync
                }

                secrets.Add(plain);
            }

            var ordered = secrets.Distinct(StringComparer.Ordinal).OrderByDescending(secret => secret.Length).ToArray();
            _secrets = new SecretsSnapshot(ordered, timeProvider.GetTimestamp());
            return ordered;
        }
        finally
        {
            _secretsLoad.Release();
        }
    }

    public void Invalidate() => _secrets = null;

    /// <summary>
    /// Every server's result passes through here: redacted with every stored sudo password of every server, and with the
    /// server's own one even before the shared list has caught up with an edit.
    /// </summary>
    internal async Task<RemoteCommandResult> RedactAsync(RemoteCommandResult result, string? ownPassword, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> secrets;
        try { secrets = await GetAsync(CancellationToken.None); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The list cannot be read: keep only the server's own password out, and say so without any detail.
            logger.LogWarning("The list of stored sudo passwords could not be read ({ErrorType}); output is redacted with this server's password only",
                exception.GetType().Name);
            secrets = [];
        }

        if (!string.IsNullOrEmpty(ownPassword) && !secrets.Contains(ownPassword, StringComparer.Ordinal))
            secrets = [.. secrets.Append(ownPassword).OrderByDescending(secret => secret.Length)];
        return KnownSecretRedaction.Redact(result, secrets);
    }

    /// <summary>
    /// The server's sudo password state, read fresh from the database for every call. An attempt that was started but whose
    /// outcome was never recorded, and that is not running in this process, becomes "outcome unknown" here.
    /// </summary>
    internal async Task<SudoCredential> ReadAsync(Guid serverId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IServerRepository>();
        var server = await repository.GetAsync(serverId, cancellationToken);
        if (server?.ProtectedSudoPassword is not { Length: > 0 } protectedValue) return SudoCredential.None;
        string password;
        try
        {
            // A paused password is decrypted too: it is never sent, but it stays on the redaction list.
            password = secretProtector.Unprotect(protectedValue);
            if (_plain.TryAdd(protectedValue, password)) Invalidate(); // a password the shared list does not hold yet
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Never the exception's message: only its type, and the password is not sent anywhere.
            logger.LogWarning("{ServerName}: the stored sudo password cannot be decrypted ({ErrorType}); sudo is not used on it",
                server.Name, exception.GetType().Name);
            return SudoCredential.Unusable(server.Name);
        }

        if (server.SudoPasswordAttemptStartedAtUtc is not null && !_inFlight.ContainsKey(serverId)
            && server.SudoPasswordRejectedAtUtc is null && server.SudoPasswordOutcomeUnknownAtUtc is null)
        {
            // Started, never finished, not running here: the app stopped in the middle of it, or storing its outcome failed
            // (then this process knows the outcome, and a stored "unknown" still keeps the pause across a restart).
            if (!_paused.TryGetValue(serverId, out var known) || known.ProtectedValue != protectedValue) _paused[serverId] = (protectedValue, false);
            server.RecordSudoPasswordOutcomeUnknown(timeProvider.GetUtcNow());
            await TrySaveAsync(repository, server, "an interrupted sudo password attempt");
            logger.LogWarning("{ServerName}: a sudo password attempt was interrupted before its outcome was known; password sudo is paused until Retry now or the password is edited or removed",
                server.Name);
            events?.NotifyChanged(serverId);
        }

        var pausedHere = _paused.TryGetValue(serverId, out var paused) && paused.ProtectedValue == protectedValue;
        if (server.SudoPasswordRejectedAtUtc is not null || (pausedHere && paused.Rejected))
            return SudoCredential.Paused(server.Name, protectedValue, password, PausedMessage);
        if (server.SudoPasswordOutcomeUnknownAtUtc is not null || pausedHere)
            return SudoCredential.Paused(server.Name, protectedValue, password, OutcomeUnknownMessage);
        return SudoCredential.WithPassword(server.Name, protectedValue, password, server.SudoAuthFailedSinceUtc is not null);
    }

    /// <summary>
    /// Waits for the server's password-sudo turn. The holder releases it once its password is confirmed or its outcome has been
    /// recorded, so a second password sudo never starts while the first one's outcome is open.
    /// </summary>
    internal async Task<SudoTurn> EnterAsync(Guid serverId, TimeSpan wait, CancellationToken cancellationToken)
    {
        var turn = _turns.GetOrAdd(serverId, _ => new SemaphoreSlim(1, 1));
        if (wait <= TimeSpan.Zero || !await turn.WaitAsync(wait, cancellationToken))
        {
            throw new TimeoutException(wait <= TimeSpan.Zero
                ? $"Less than {MinimumTimeLeft.TotalSeconds:0} s of the deadline were left; the password sudo was not started."
                : $"Another sudo command on this server was still authenticating; the password sudo was not started (less than {MinimumTimeLeft.TotalSeconds:0} s would have been left).");
        }

        return new SudoTurn(turn);
    }

    /// <summary>
    /// Writes down that the password is about to be sent. False when that cannot be stored: then nothing is sent, because an
    /// attempt cut off by a crash could not be found afterwards.
    /// </summary>
    internal async Task<bool> BeginAttemptAsync(Guid serverId, string protectedValue)
    {
        _inFlight[serverId] = 0;
        try
        {
            if (await UpdateAsync(serverId, protectedValue, server => server.RecordSudoPasswordAttemptStarted(timeProvider.GetUtcNow())) is not null) return true;
        }
        catch (Exception exception)
        {
            logger.LogError("A sudo password attempt could not be recorded ({ErrorType}); the password was not sent", exception.GetType().Name);
        }

        _inFlight.TryRemove(serverId, out _);
        return false;
    }

    /// <summary>The attempt is over and its outcome recorded.</summary>
    internal void EndAttempt(Guid serverId) => _inFlight.TryRemove(serverId, out _);

    /// <summary>
    /// The marker came back: sudo accepted the password. Clears the attempt and resolves the sudo-auth-failed alert (D-040
    /// amendment 2). Not cancellable: the outcome is known and must be stored.
    /// </summary>
    internal async Task RecordConfirmedAsync(Guid serverId, string protectedValue, bool alertOpen)
    {
        try
        {
            var name = await UpdateAsync(serverId, protectedValue, server => server.RecordSudoPasswordSucceeded());
            if (alertOpen && name is not null)
            {
                logger.LogInformation("{ServerName}: sudo works again with the stored sudo password", name);
                events?.NotifyChanged(serverId);
            }
        }
        catch (Exception exception)
        {
            // Still marked as started in the database: in this process the attempt is over, a restart would read it as unknown.
            logger.LogError("A confirmed sudo password attempt could not be recorded ({ErrorType})", exception.GetType().Name);
        }
    }

    /// <summary>
    /// Records that the stored password was rejected, unless it was replaced or removed in the meantime (then the rejection was
    /// for a password that no longer exists).
    /// </summary>
    internal Task RecordRejectedAsync(Guid serverId, string protectedValue) =>
        RecordPausedAsync(serverId, protectedValue, rejected: true);

    /// <summary>Records that an attempt ended without confirmation and without a rejection: outcome unknown (T7.R25).</summary>
    internal Task RecordOutcomeUnknownAsync(Guid serverId, string protectedValue) =>
        RecordPausedAsync(serverId, protectedValue, rejected: false);

    /// <summary>
    /// Retry now / Test connection (T7.R25): lifts an "outcome unknown" pause so the probe tries the password once. A rejection
    /// is not lifted (D-040 amendment 1).
    /// </summary>
    internal async Task ClearOutcomeUnknownAsync(Guid serverId, CancellationToken cancellationToken)
    {
        if (_paused.TryGetValue(serverId, out var paused) && !paused.Rejected) _paused.TryRemove(KeyValuePair.Create(serverId, paused));
        await using var scope = scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IServerRepository>();
        var server = await repository.GetAsync(serverId, cancellationToken);
        if (server?.SudoPasswordOutcomeUnknownAtUtc is null) return;
        server.ClearSudoPasswordOutcomeUnknown();
        await repository.SaveAsync(server, cancellationToken);
        events?.NotifyChanged(serverId);
    }

    private async Task RecordPausedAsync(Guid serverId, string protectedValue, bool rejected)
    {
        // Remembered in memory first: a failing database write must not leave the password armed for the next command.
        _paused[serverId] = (protectedValue, rejected);
        // Not cancellable: the attempt has been made, and forgetting it would allow a second attempt.
        try
        {
            var now = timeProvider.GetUtcNow();
            var name = await UpdateAsync(serverId, protectedValue, server =>
            {
                if (rejected) server.RecordSudoPasswordRejected(now);
                else server.RecordSudoPasswordOutcomeUnknown(now);
            });
            if (name is null) return;
            events?.NotifyChanged(serverId);
            if (rejected)
                logger.LogWarning("{ServerName}: the sudo password was rejected; no sudo is used on this server until its sudo password is edited or removed",
                    name);
            else
                logger.LogWarning("{ServerName}: the outcome of a sudo password attempt is unknown; password sudo is paused until Retry now or the password is edited or removed",
                    name);
        }
        catch (Exception exception)
        {
            // Only the type: sudo stays paused in this process, and the attempt is still marked as started in the database.
            logger.LogError("The outcome of a sudo password attempt could not be stored ({ErrorType}); sudo stays paused on this server until the app restarts or the password is edited or removed",
                exception.GetType().Name);
        }
    }

    // Applies a change to the server if its stored password is still the one the attempt used; returns the server's name.
    private async Task<string?> UpdateAsync(Guid serverId, string protectedValue, Action<Domain.Servers.Server> change)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IServerRepository>();
        var server = await repository.GetAsync(serverId, CancellationToken.None);
        if (server is null || server.ProtectedSudoPassword != protectedValue) return null;
        change(server);
        await repository.SaveAsync(server, CancellationToken.None);
        return server.Name;
    }

    private async Task TrySaveAsync(IServerRepository repository, Domain.Servers.Server server, string what)
    {
        try { await repository.SaveAsync(server, CancellationToken.None); }
        catch (Exception exception)
        {
            logger.LogError("Storing {What} failed ({ErrorType}); password sudo stays paused in this process", what, exception.GetType().Name);
        }
    }

    /// <summary>
    /// True when sudo refused the password: sudo exits non-zero and says "Sorry, try again." (each wrong password, also when
    /// pam_faillock has locked the account), "N incorrect password attempt(s)" or "no password was provided". Matched on whole
    /// stderr lines in the C locale (the command sets <c>LC_ALL=C</c>). Only used to word an unconfirmed attempt: without the
    /// marker the password is never treated as accepted.
    /// </summary>
    public static bool IsPasswordRejected(RemoteCommandResult result) =>
        result.ExitCode is not 0
        && result.StandardError.Split('\n').Any(line => RejectionLine().IsMatch(line.TrimEnd('\r')));

    [GeneratedRegex(@"^(?:Sorry, try again\.|sudo: \d+ incorrect password attempts?|sudo: no password was provided)$",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex RejectionLine();

    /// <summary>Redacts one password only; the runner uses <see cref="RedactAsync"/> (every stored password, T7.R26).</summary>
    public static RemoteCommandResult Redact(RemoteCommandResult result, string? password) =>
        string.IsNullOrEmpty(password) ? result : KnownSecretRedaction.Redact(result, [password]);

    public static RemoteCommandResult NotRun(string message) => new(1, string.Empty, message, TimeSpan.Zero);
}

internal enum SudoCredentialKind
{
    /// <summary>No sudo password stored: <c>sudo -n</c>, as always.</summary>
    None,
    WithPassword,
    /// <summary>The stored password was rejected or the outcome of an attempt is unknown: no password sudo.</summary>
    Paused,
    /// <summary>The stored password cannot be decrypted (Data Protection keys lost): no sudo.</summary>
    Unusable
}

/// <summary>A server's sudo credential. <see cref="ToString"/> never shows the password.</summary>
internal sealed class SudoCredential
{
    private SudoCredential(SudoCredentialKind kind, string? serverName, string? protectedValue, string? password,
        bool alertOpen = false, string? pausedMessage = null)
    {
        Kind = kind;
        ServerName = serverName;
        ProtectedValue = protectedValue;
        Password = password;
        AlertOpen = alertOpen;
        PausedMessage = pausedMessage;
    }

    public static SudoCredential None { get; } = new(SudoCredentialKind.None, null, null, null);

    public static SudoCredential WithPassword(string serverName, string protectedValue, string password, bool alertOpen = false) =>
        new(SudoCredentialKind.WithPassword, serverName, protectedValue, password, alertOpen);

    public static SudoCredential Paused(string serverName, string protectedValue, string password, string message) =>
        new(SudoCredentialKind.Paused, serverName, protectedValue, password, pausedMessage: message);

    public static SudoCredential Unusable(string serverName) => new(SudoCredentialKind.Unusable, serverName, null, null);

    public SudoCredentialKind Kind { get; }
    public string? ServerName { get; }
    public string? ProtectedValue { get; }
    public string? Password { get; }
    /// <summary>The server's sudo-auth-failed alert is open (a rejection or unknown outcome, then an edit, and no success yet).</summary>
    public bool AlertOpen { get; }
    /// <summary>The "not run" text of a paused credential: rejected or outcome unknown.</summary>
    public string? PausedMessage { get; }

    public override string ToString() => $"{Kind} sudo credential";
}

/// <summary>A server's password-sudo turn, released once: early when the password is confirmed, else by <see cref="Dispose"/>.</summary>
internal sealed class SudoTurn(SemaphoreSlim turn) : IDisposable
{
    private int _released;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0) turn.Release();
    }
}
