namespace DevOpsAgent.Application.Abstractions;

public interface ISshRunner
{
    Task<RemoteCommandResult> RunAsync(
        Guid serverId,
        string command,
        SshRunOptions options,
        CancellationToken cancellationToken);

    /// <summary>
    /// The owner's sudo check ("Test connection", "Retry now"): <c>sudo -n true</c>, or on a server with a stored sudo password
    /// <c>sudo -S -p '' true</c> with the password on stdin (T7.R24). A rejected password is not tried: the probe returns the
    /// "not run" result without contacting the server (D-040 amendment, pam_faillock).
    /// </summary>
    Task<RemoteCommandResult> ProbeSudoAsync(Guid serverId, SshRunOptions options, CancellationToken cancellationToken) =>
        RunAsync(serverId, RemoteCommand.SudoProbe, options with { UseSudo = false }, cancellationToken);
}

public sealed class SshHostKeyMismatchException(string host, int port, string? fingerprint)
    : Exception($"The SSH host key for {host}:{port} does not match the stored key.")
{
    public string Host { get; } = host;
    public int Port { get; } = port;
    public string? Fingerprint { get; } = fingerprint;
}

public interface ISshConnectionPool : IDisposable
{
    /// <summary>
    /// Leases the server's pooled connection. The connection is not closed under the lease (eviction and recycling only
    /// retire it for new callers); dispose the session when the commands are finished, also after cancellation or failure.
    /// </summary>
    /// <param name="bypassBackoff">Attempt the connection even if the host (or a jump host) is in backoff ("Test connection", "Retry now").</param>
    Task<ISshCommandSession> GetSessionAsync(Guid serverId, bool bypassBackoff, CancellationToken cancellationToken);

    /// <summary>
    /// Like the overload above, with a call deadline that includes connect time (T7.R17): connecting (and waiting for
    /// another caller's connect) is cut off when <paramref name="deadline"/> passes, and the session's first command gets
    /// only what is left of it. A connect cut off by the deadline counts as a failed attempt for the host's backoff.
    /// Pools without deadline support ignore it.
    /// </summary>
    Task<ISshCommandSession> GetSessionAsync(Guid serverId, bool bypassBackoff, TimeSpan deadline, CancellationToken cancellationToken) =>
        GetSessionAsync(serverId, bypassBackoff, cancellationToken);

    /// <summary>
    /// Retires any pooled connection for the server, e.g. after its host, user or key changed or it was removed. Commands
    /// already running on it finish; the connection is closed when the last of them ends and new callers connect afresh.
    /// </summary>
    void Evict(Guid serverId);
}

/// <summary>A lease on a pooled connection. Disposing it (idempotent) ends the lease.</summary>
public interface ISshCommandSession : IDisposable
{
    bool IsConnected { get; }
    Task<RemoteCommandResult> ExecuteAsync(string command, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Like the overload above, writing <paramref name="standardInput"/> to the command's stdin and then closing it.</summary>
    Task<RemoteCommandResult> ExecuteAsync(string command, string? standardInput, TimeSpan timeout, CancellationToken cancellationToken) =>
        standardInput is null
            ? ExecuteAsync(command, timeout, cancellationToken)
            : throw new NotSupportedException("This SSH session cannot write to a command's input.");

    /// <summary>
    /// Like the overload above, watching the output for <paramref name="marker"/> as it arrives (T7.R25): the marker is
    /// reported the moment its line comes back and is removed from the returned output.
    /// </summary>
    Task<RemoteCommandResult> ExecuteAsync(string command, string? standardInput, OutputMarker? marker, TimeSpan timeout, CancellationToken cancellationToken) =>
        marker is null
            ? ExecuteAsync(command, standardInput, timeout, cancellationToken)
            : throw new NotSupportedException("This SSH session cannot watch a command's output.");
}

/// <param name="BypassBackoff">Ignore the host's reachability backoff for this one command ("Test connection", "Retry now").</param>
/// <param name="NotAfterUtc">
/// The deadline of the cycle the call belongs to, when it ends before <paramref name="Timeout"/> does (T7.R25): a password
/// sudo is not started when less than <c>SudoPasswordGate.MinimumTimeLeft</c> is left of either.
/// </param>
public sealed record SshRunOptions(TimeSpan Timeout, bool UseSudo = false, bool BypassBackoff = false, DateTimeOffset? NotAfterUtc = null);

public sealed record RemoteCommandResult(int? ExitCode, string StandardOutput, string StandardError, TimeSpan Duration, bool SudoPasswordNotChecked = false);
