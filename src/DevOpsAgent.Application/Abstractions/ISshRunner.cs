namespace DevOpsAgent.Application.Abstractions;

public interface ISshRunner
{
    Task<RemoteCommandResult> RunAsync(
        Guid serverId,
        string command,
        SshRunOptions options,
        CancellationToken cancellationToken);
}

public sealed class SshHostKeyMismatchException(string host, int port, string? fingerprint)
    : Exception($"The SSH host key for {host}:{port} is not pinned.")
{
    public string Host { get; } = host;
    public int Port { get; } = port;
    public string? Fingerprint { get; } = fingerprint;
}

public interface ISshConnectionPool : IDisposable
{
    /// <param name="bypassBackoff">Attempt the connection even if the host (or a jump host) is in backoff ("Test connection", "Retry now").</param>
    Task<ISshCommandSession> GetSessionAsync(Guid serverId, bool bypassBackoff, CancellationToken cancellationToken);

    /// <summary>Drops any pooled connection for the server, e.g. after its host, user or key changed or it was removed.</summary>
    void Evict(Guid serverId);
}

public interface ISshCommandSession
{
    bool IsConnected { get; }
    Task<RemoteCommandResult> ExecuteAsync(string command, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <param name="BypassBackoff">Ignore the host's reachability backoff for this one command ("Test connection", "Retry now").</param>
public sealed record SshRunOptions(TimeSpan Timeout, bool UseSudo = false, bool BypassBackoff = false);

public sealed record RemoteCommandResult(int? ExitCode, string StandardOutput, string StandardError, TimeSpan Duration);
