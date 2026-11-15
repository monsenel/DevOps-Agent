using System.Globalization;

namespace DevOpsAgent.Application.Abstractions;

/// <summary>
/// Why a host is in backoff: its connection failed, it connected but its commands keep timing out (T7.R17), or the login
/// failed (T7.R19).
/// </summary>
public enum HostFailureKind
{
    /// <summary>The SSH connection (or a jump hop) could not be established.</summary>
    Connect = 0,

    /// <summary>The host accepts connections but its commands time out repeatedly.</summary>
    CommandTimeout = 1,

    /// <summary>
    /// The login failed: the server rejected the key, or the key file is missing, unreadable, invalid or needs a passphrase
    /// (T7.R19). The host is not "unreachable"; the cause is the configuration, so it is retried every minute (never
    /// doubling) and a fix is picked up quickly.
    /// </summary>
    AuthFailed = 2
}

/// <summary>Backoff state of a host whose last SSH connection attempt failed or whose commands keep timing out.</summary>
/// <param name="Cause">
/// A short, safe description of the real cause ("connection timed out", "key file not found"), or null when unknown. Never
/// derived from a server's or a library's message text.
/// </param>
public sealed record HostReachabilityState(
    Guid ServerId,
    DateTimeOffset UnreachableSinceUtc,
    DateTimeOffset NextRetryUtc,
    int ConsecutiveFailures,
    string LastError,
    bool RetryInProgress,
    Exception? LastFailure,
    HostFailureKind Kind = HostFailureKind.Connect,
    string? Cause = null);

/// <summary>
/// Per-host reachability with backoff (T7.R1, T7.R17, T7.R19): after a connection-level failure, or after
/// <see cref="CommandTimeoutThreshold"/> consecutive command timeouts, a host is skipped for 1 minute, doubling up to
/// 15 minutes, and reset by the first success. A failed login is skipped for 1 minute and never longer. Callers never wait
/// on a host in backoff.
/// </summary>
public interface IHostReachability
{
    /// <summary>
    /// Consecutive command timeouts on a host (across client recycling) that put it in backoff. It is deliberately one more
    /// than the three timeouts that recycle a pooled client: the fourth one happened on a fresh connection, so the host
    /// itself, not a stale connection, is not answering.
    /// </summary>
    const int CommandTimeoutThreshold = 4;

    /// <summary>The backoff state, or null when the host is not known to be unreachable.</summary>
    HostReachabilityState? Get(Guid serverId);

    /// <summary>True while the host is in backoff or another caller is already retrying it.</summary>
    bool IsBlocked(Guid serverId, out HostReachabilityState? state);

    /// <summary>
    /// Claims a connection attempt. Returns false (with the blocking state) when the host is in backoff or another retry is
    /// in flight. <paramref name="bypassBackoff"/> ("Test connection", "Retry now") always allows the attempt.
    /// </summary>
    bool TryBeginAttempt(Guid serverId, bool bypassBackoff, out HostReachabilityState? blockedBy);

    /// <summary>
    /// A connection was established (which includes the login). Clears a connection or login failure; a host that is in
    /// backoff for command timeouts stays in it until a command succeeds (connecting says nothing about whether its commands
    /// are answered).
    /// </summary>
    void RecordSuccess(Guid serverId);

    /// <summary>
    /// A connection attempt failed. A <see cref="SshAuthFailedException"/> is a login failure
    /// (<see cref="HostFailureKind.AuthFailed"/>, constant 1 minute backoff); everything else is a connection failure.
    /// </summary>
    /// <param name="cause">The safe description of what went wrong, shown to the owner (see <see cref="HostReachabilityState.Cause"/>).</param>
    /// <returns>The new backoff state.</returns>
    HostReachabilityState RecordFailure(Guid serverId, Exception failure, string? cause = null);

    /// <summary>
    /// A command on the host timed out. Counts consecutive timeouts and returns the new backoff state once
    /// <see cref="CommandTimeoutThreshold"/> are reached, or when a host already in command-timeout backoff fails its retry;
    /// otherwise null. <paramref name="force"/> ("Retry now") lets a failing attempt extend a backoff that has not expired.
    /// </summary>
    HostReachabilityState? RecordCommandTimeout(Guid serverId, Exception failure, bool force = false);

    /// <summary>A command on the host completed: resets the timeout count and ends a command-timeout backoff.</summary>
    void RecordCommandSuccess(Guid serverId);

    /// <summary>Releases a claimed attempt that ended without an outcome (for example the caller cancelled).</summary>
    void EndAttempt(Guid serverId);

    /// <summary>Forgets the backoff and the timeout count, e.g. after the server's connection settings changed.</summary>
    void Reset(Guid serverId);
}

/// <summary>
/// Thrown immediately, without any connection attempt, for a host in backoff or a server whose jump host is in backoff. It
/// is the one "this host cannot be used right now" signal of the SSH layer: callers skip the host exactly as before whatever
/// the reason, and <see cref="Kind"/> tells a login failure (<see cref="HostFailureKind.AuthFailed"/>) from a host that is
/// really unreachable, so no screen has to call a bad key "unreachable".
/// </summary>
public sealed class HostUnreachableException : Exception
{
    public HostUnreachableException(Guid serverId, string serverName, HostReachabilityState state, string? viaJumpServerName = null)
        : base(Describe(serverName, state, viaJumpServerName), state.LastFailure)
    {
        ServerId = serverId;
        ServerName = serverName;
        ViaJumpServerName = viaJumpServerName;
        UnreachableSinceUtc = state.UnreachableSinceUtc;
        NextRetryUtc = state.NextRetryUtc;
        LastFailure = state.LastFailure;
        Kind = state.Kind;
        Cause = state.Cause;
    }

    public Guid ServerId { get; }
    public string ServerName { get; }
    public string? ViaJumpServerName { get; }
    public DateTimeOffset UnreachableSinceUtc { get; }
    public DateTimeOffset NextRetryUtc { get; }
    public Exception? LastFailure { get; }
    public HostFailureKind Kind { get; }

    /// <summary>The safe description of the real cause, or null when it is not known.</summary>
    public string? Cause { get; }

    /// <summary>The login failed (the host itself, or the jump host named in <see cref="ViaJumpServerName"/>, answered).</summary>
    public bool IsAuthFailure => Kind == HostFailureKind.AuthFailed;

    /// <summary>
    /// "name is unreachable (since …, next retry …): could not connect." or "…: commands timed out." The cause is named, so a
    /// host that connects but does not answer is not mistaken for one that cannot be reached. A failed login reads
    /// "name: authentication failed (key file not found). Since …, next retry …." and, behind a jump host whose login failed,
    /// "name via jump host edge: authentication failed (…)."
    /// </summary>
    public static string Describe(string serverName, HostReachabilityState state, string? viaJumpServerName = null)
    {
        var route = viaJumpServerName is null ? string.Empty : $" via jump host {viaJumpServerName}";
        var retry = state.RetryInProgress ? "retry in progress" : $"next retry {Time(state.NextRetryUtc)}";
        if (state.Kind == HostFailureKind.AuthFailed)
        {
            var reason = string.IsNullOrWhiteSpace(state.Cause) ? string.Empty : $" ({state.Cause})";
            return $"{serverName}{route}: authentication failed{reason}. Since {Time(state.UnreachableSinceUtc)}, {retry}.";
        }

        var cause = state.Kind == HostFailureKind.CommandTimeout
            ? viaJumpServerName is null ? "commands timed out" : $"commands on {viaJumpServerName} timed out"
            : viaJumpServerName is null ? "could not connect" : $"could not connect to {viaJumpServerName}";
        return $"{serverName} is unreachable{route} (since {Time(state.UnreachableSinceUtc)}, {retry}): {cause}.";
    }

    private static string Time(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
}

/// <summary>
/// The application is shutting down: no SSH connection or command is started, and one in flight is abandoned. It is a
/// cancellation, not a failure, so it never opens an alert or marks a server unreachable.
/// </summary>
public sealed class SshShutdownException()
    : OperationCanceledException("The application is shutting down; no new SSH connection or command is started.");

public static class SshFailures
{
    /// <summary>The host-key mismatch behind an SSH failure, if any (directly or as the cause of an unreachable host).</summary>
    public static SshHostKeyMismatchException? HostKeyMismatch(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SshHostKeyMismatchException mismatch) return mismatch;
        }

        return null;
    }
}
