using System.Globalization;

namespace DevOpsAgent.Application.Abstractions;

/// <summary>Why a host is in backoff: its connection failed, or it connected but its commands keep timing out (T7.R17).</summary>
public enum HostFailureKind
{
    /// <summary>The SSH connection (or a jump hop) could not be established.</summary>
    Connect = 0,

    /// <summary>The host accepts connections but its commands time out repeatedly.</summary>
    CommandTimeout = 1
}

/// <summary>Backoff state of a host whose last SSH connection attempt failed or whose commands keep timing out.</summary>
public sealed record HostReachabilityState(
    Guid ServerId,
    DateTimeOffset UnreachableSinceUtc,
    DateTimeOffset NextRetryUtc,
    int ConsecutiveFailures,
    string LastError,
    bool RetryInProgress,
    Exception? LastFailure,
    HostFailureKind Kind = HostFailureKind.Connect);

/// <summary>
/// Per-host reachability with backoff (T7.R1, T7.R17): after a connection-level failure, or after
/// <see cref="CommandTimeoutThreshold"/> consecutive command timeouts, a host is skipped for 1 minute, doubling up to
/// 15 minutes, and reset by the first success. Callers never wait on a host in backoff.
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
    /// A connection was established. Clears a connection failure; a host that is in backoff for command timeouts stays in
    /// it until a command succeeds (connecting says nothing about whether its commands are answered).
    /// </summary>
    void RecordSuccess(Guid serverId);

    /// <returns>The new backoff state.</returns>
    HostReachabilityState RecordFailure(Guid serverId, Exception failure);

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
/// Thrown immediately, without any connection attempt, for a host in backoff or a server whose jump host is unreachable.
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
    }

    public Guid ServerId { get; }
    public string ServerName { get; }
    public string? ViaJumpServerName { get; }
    public DateTimeOffset UnreachableSinceUtc { get; }
    public DateTimeOffset NextRetryUtc { get; }
    public Exception? LastFailure { get; }
    public HostFailureKind Kind { get; }

    /// <summary>
    /// "name is unreachable (since …, next retry …): could not connect." or "…: commands timed out." The cause is named, so a
    /// host that connects but does not answer is not mistaken for one that cannot be reached.
    /// </summary>
    public static string Describe(string serverName, HostReachabilityState state, string? viaJumpServerName = null)
    {
        var route = viaJumpServerName is null ? string.Empty : $" via jump host {viaJumpServerName}";
        var retry = state.RetryInProgress ? "retry in progress" : $"next retry {Time(state.NextRetryUtc)}";
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
