using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using DevOpsAgent.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DevOpsAgent.Infrastructure.Ssh;

/// <summary>
/// The per-server sudo password (T7.R24): reads it, takes care that a wrong one is tried only once, and keeps it out of what
/// the server sends back. Used by <see cref="SshRunner"/> only; nothing else ever sees the plain password.
/// <para>
/// A rejected password is stored on the server (<c>SudoPasswordRejectedAtUtc</c>): from then on no sudo command is sent to that
/// server (pam_faillock counts every wrong attempt, twice with sudo's default <c>passwd_tries=3</c>) until the owner edits or
/// removes the password; "Retry now" and "Test connection" do not try it again (D-040 amendment). Password sudo on a server
/// runs one at a time while it authenticates, so two commands can never both be the first to find out that the password is
/// wrong.
/// </para>
/// </summary>
public sealed partial class SudoPasswordGate(
    IServiceScopeFactory scopeFactory,
    ISecretProtector secretProtector,
    TimeProvider timeProvider,
    ILogger<SudoPasswordGate> logger)
{
    /// <summary>
    /// How long the next password sudo of the same server waits behind a running one. A rejected password ends sudo within
    /// PAM's failure delay (about 2-3 s), so a command still running after this long got past authentication.
    /// </summary>
    public static readonly TimeSpan AuthenticationWindow = TimeSpan.FromSeconds(15);

    public const string Redacted = "[REDACTED]";

    /// <summary>What a sudo command gets instead of running while the server's sudo password is rejected.</summary>
    public const string PausedMessage =
        "sudo: not run: the saved sudo password for this server was rejected; sudo is paused until the password is edited or removed.";

    public const string UnusableMessage =
        "sudo: not run: the saved sudo password for this server cannot be decrypted; enter it again in the server settings.";

    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _turns = new();

    /// <summary>
    /// Rejected passwords (their protected value) as seen by this process, kept besides the stored rejection: if storing it
    /// fails, the password is still never sent again until it is edited (a new protected value) or removed.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, string> _rejected = new();

    /// <summary>The server's sudo password state, read fresh from the database for every call.</summary>
    internal async Task<SudoCredential> ReadAsync(Guid serverId, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var server = await scope.ServiceProvider.GetRequiredService<IServerRepository>().GetAsync(serverId, cancellationToken);
        if (server?.ProtectedSudoPassword is not { Length: > 0 } protectedValue) return SudoCredential.None;
        try
        {
            // A paused password is decrypted too: it is never sent, but it stays on the server's redaction list.
            var password = secretProtector.Unprotect(protectedValue);
            var rejectedHere = _rejected.TryGetValue(serverId, out var rejected) && rejected == protectedValue;
            return server.SudoPasswordRejectedAtUtc is null && !rejectedHere
                ? SudoCredential.WithPassword(server.Name, protectedValue, password, server.SudoAuthFailedSinceUtc is not null)
                : SudoCredential.Paused(server.Name, protectedValue, password);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Never the exception's message: only its type, and the password is not sent anywhere.
            logger.LogWarning("{ServerName}: the stored sudo password cannot be decrypted ({ErrorType}); sudo is not used on it",
                server.Name, exception.GetType().Name);
            return SudoCredential.Unusable(server.Name);
        }
    }

    /// <summary>
    /// Waits for the server's password-sudo turn. The turn is released when the command ends or after
    /// <see cref="AuthenticationWindow"/>, whichever comes first (see <see cref="SudoTurn.ReleaseAfter"/>).
    /// </summary>
    internal async Task<SudoTurn> EnterAsync(Guid serverId, TimeSpan wait, CancellationToken cancellationToken)
    {
        var turn = _turns.GetOrAdd(serverId, _ => new SemaphoreSlim(1, 1));
        if (!await turn.WaitAsync(wait, cancellationToken))
        {
            throw new TimeoutException(
                $"Another sudo command on this server was still authenticating; this one was not started within {wait.TotalSeconds:0.#} s.");
        }

        return new SudoTurn(turn);
    }

    /// <summary>
    /// Records that the stored password was rejected, unless it was replaced or removed in the meantime (then the rejection was
    /// for a password that no longer exists).
    /// </summary>
    internal async Task RecordRejectedAsync(Guid serverId, string protectedValue)
    {
        // Remembered in memory first: a failing database write must not leave the password armed for the next command.
        _rejected[serverId] = protectedValue;
        // Not cancellable: the attempt has been made, and forgetting it would allow a second wrong attempt.
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetRequiredService<IServerRepository>();
            var server = await repository.GetAsync(serverId, CancellationToken.None);
            if (server is null || server.ProtectedSudoPassword != protectedValue) return;
            server.RecordSudoPasswordRejected(timeProvider.GetUtcNow());
            await repository.SaveAsync(server, CancellationToken.None);
            logger.LogWarning("{ServerName}: the sudo password was rejected; no sudo is used on this server until its sudo password is edited or removed",
                server.Name);
        }
        catch (Exception exception)
        {
            // Only the type: sudo stays paused in this process, but a restart would allow one more attempt.
            logger.LogError("The rejection of a sudo password could not be stored ({ErrorType}); sudo stays paused on this server until the app restarts or the password is edited or removed",
                exception.GetType().Name);
        }
    }

    /// <summary>
    /// Records that sudo worked with the stored password (D-040 amendment 2): the server's sudo-auth-failed alert resolves.
    /// Only called while that alert is open, so a healthy server costs no write.
    /// </summary>
    internal async Task RecordSucceededAsync(Guid serverId, string protectedValue)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IServerRepository>();
        var server = await repository.GetAsync(serverId, CancellationToken.None);
        if (server is null || server.ProtectedSudoPassword != protectedValue || server.SudoAuthFailedSinceUtc is null) return;
        server.RecordSudoPasswordSucceeded();
        await repository.SaveAsync(server, CancellationToken.None);
        logger.LogInformation("{ServerName}: sudo works again with the stored sudo password", server.Name);
    }

    /// <summary>
    /// True when sudo refused the password: sudo exits non-zero and says "Sorry, try again." (each wrong password, also when
    /// pam_faillock has locked the account), "N incorrect password attempt(s)" or "no password was provided". Matched on whole
    /// stderr lines in the C locale (the command sets <c>LC_ALL=C</c>).
    /// </summary>
    public static bool IsPasswordRejected(RemoteCommandResult result) =>
        result.ExitCode is not 0
        && result.StandardError.Split('\n').Any(line => RejectionLine().IsMatch(line.TrimEnd('\r')));

    [GeneratedRegex(@"^(?:Sorry, try again\.|sudo: \d+ incorrect password attempts?|sudo: no password was provided)$",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex RejectionLine();

    /// <summary>The server's redaction list: its sudo password never leaves the runner in anything the server sent back.</summary>
    public static RemoteCommandResult Redact(RemoteCommandResult result, string? password) =>
        string.IsNullOrEmpty(password)
            ? result
            : result with
            {
                StandardOutput = result.StandardOutput.Replace(password, Redacted, StringComparison.Ordinal),
                StandardError = result.StandardError.Replace(password, Redacted, StringComparison.Ordinal)
            };

    public static RemoteCommandResult NotRun(string message) => new(1, string.Empty, message, TimeSpan.Zero);
}

internal enum SudoCredentialKind
{
    /// <summary>No sudo password stored: <c>sudo -n</c>, as always.</summary>
    None,
    WithPassword,
    /// <summary>The stored password was rejected: no sudo until the password is edited or removed.</summary>
    Paused,
    /// <summary>The stored password cannot be decrypted (Data Protection keys lost): no sudo.</summary>
    Unusable
}

/// <summary>A server's sudo credential. <see cref="ToString"/> never shows the password.</summary>
internal sealed class SudoCredential
{
    private SudoCredential(SudoCredentialKind kind, string? serverName, string? protectedValue, string? password, bool alertOpen = false)
    {
        Kind = kind;
        ServerName = serverName;
        ProtectedValue = protectedValue;
        Password = password;
        AlertOpen = alertOpen;
    }

    public static SudoCredential None { get; } = new(SudoCredentialKind.None, null, null, null);

    public static SudoCredential WithPassword(string serverName, string protectedValue, string password, bool alertOpen = false) =>
        new(SudoCredentialKind.WithPassword, serverName, protectedValue, password, alertOpen);

    public static SudoCredential Paused(string serverName, string protectedValue, string password) =>
        new(SudoCredentialKind.Paused, serverName, protectedValue, password);

    public static SudoCredential Unusable(string serverName) => new(SudoCredentialKind.Unusable, serverName, null, null);

    public SudoCredentialKind Kind { get; }
    public string? ServerName { get; }
    public string? ProtectedValue { get; }
    public string? Password { get; }
    /// <summary>The server's sudo-auth-failed alert is open (a rejection, then an edit, and no success yet).</summary>
    public bool AlertOpen { get; }

    public override string ToString() => $"{Kind} sudo credential";
}

/// <summary>
/// A server's password-sudo turn, released once: by <see cref="Dispose"/> after the command's outcome (a rejection included)
/// has been recorded, or by the authentication window if the command is still running then.
/// </summary>
internal sealed class SudoTurn(SemaphoreSlim turn) : IDisposable
{
    private ITimer? _window;
    private int _released;

    /// <summary>Releases the turn after <paramref name="window"/> unless it has been disposed before.</summary>
    public void ReleaseAfter(TimeSpan window, TimeProvider timeProvider) =>
        _window = timeProvider.CreateTimer(_ => Release(), null, window, Timeout.InfiniteTimeSpan);

    public void Dispose()
    {
        _window?.Dispose();
        Release();
    }

    private void Release()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0) turn.Release();
    }
}
