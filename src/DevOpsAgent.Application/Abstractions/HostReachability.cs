using System.Globalization;

namespace DevOpsAgent.Application.Abstractions;

/// <summary>Backoff state of a host whose last SSH connection attempt failed.</summary>
public sealed record HostReachabilityState(
    Guid ServerId,
    DateTimeOffset UnreachableSinceUtc,
    DateTimeOffset NextRetryUtc,
    int ConsecutiveFailures,
    string LastError,
    bool RetryInProgress,
    Exception? LastFailure);

/// <summary>
/// Per-host reachability with backoff (T7.R1): after a connection-level failure a host is skipped for 1 minute, doubling up
/// to 15 minutes, and reset by the first success. Callers never wait on a host in backoff.
/// </summary>
public interface IHostReachability
{
    /// <summary>The backoff state, or null when the host is not known to be unreachable.</summary>
    HostReachabilityState? Get(Guid serverId);

    /// <summary>True while the host is in backoff or another caller is already retrying it.</summary>
    bool IsBlocked(Guid serverId, out HostReachabilityState? state);

    /// <summary>
    /// Claims a connection attempt. Returns false (with the blocking state) when the host is in backoff or another retry is
    /// in flight. <paramref name="bypassBackoff"/> ("Test connection", "Retry now") always allows the attempt.
    /// </summary>
    bool TryBeginAttempt(Guid serverId, bool bypassBackoff, out HostReachabilityState? blockedBy);

    void RecordSuccess(Guid serverId);
    void RecordFailure(Guid serverId, Exception failure);

    /// <summary>Releases a claimed attempt that ended without an outcome (for example the caller cancelled).</summary>
    void EndAttempt(Guid serverId);

    /// <summary>Forgets the backoff, e.g. after the server's connection settings changed.</summary>
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
    }

    public Guid ServerId { get; }
    public string ServerName { get; }
    public string? ViaJumpServerName { get; }
    public DateTimeOffset UnreachableSinceUtc { get; }
    public DateTimeOffset NextRetryUtc { get; }
    public Exception? LastFailure { get; }

    public static string Describe(string serverName, HostReachabilityState state, string? viaJumpServerName = null)
    {
        var route = viaJumpServerName is null ? string.Empty : $" via jump host {viaJumpServerName}";
        var retry = state.RetryInProgress ? "retry in progress" : $"next retry {Time(state.NextRetryUtc)}";
        return $"{serverName} is unreachable{route} (since {Time(state.UnreachableSinceUtc)}, {retry}).";
    }

    private static string Time(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
}

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
