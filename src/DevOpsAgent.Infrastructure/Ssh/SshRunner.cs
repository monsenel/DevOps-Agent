using DevOpsAgent.Application.Abstractions;

namespace DevOpsAgent.Infrastructure.Ssh;

public sealed class SshRunner(ISshConnectionPool connectionPool) : ISshRunner
{
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

        var wrappedCommand = RemoteCommand.Wrap(command, options.UseSudo);
        var session = await connectionPool.GetSessionAsync(serverId, options.BypassBackoff, cancellationToken);
        return await session.ExecuteAsync(wrappedCommand, options.Timeout, cancellationToken);
    }
}
