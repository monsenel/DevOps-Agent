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
    Task<ISshCommandSession> GetSessionAsync(Guid serverId, CancellationToken cancellationToken);

    /// <summary>Drops any pooled connection for the server, e.g. after its host, user or key changed or it was removed.</summary>
    void Evict(Guid serverId);
}

public interface ISshCommandSession
{
    bool IsConnected { get; }
    Task<RemoteCommandResult> ExecuteAsync(string command, TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed record SshRunOptions(TimeSpan Timeout, bool UseSudo = false);

public sealed record RemoteCommandResult(int? ExitCode, string StandardOutput, string StandardError, TimeSpan Duration);
