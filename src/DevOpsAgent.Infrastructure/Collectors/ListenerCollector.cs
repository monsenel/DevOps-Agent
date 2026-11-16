using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;

namespace DevOpsAgent.Infrastructure.Collectors;

public sealed class ListenerCollector : ICollector
{
    public const string Command = "ss -tulpnH";

    /// <summary>Fallback for hosts without <c>ss</c> (BusyBox/Alpine, net-tools). Read-only; PIDs need root like <c>ss -p</c>.</summary>
    public const string NetstatCommand = "netstat -tulpn";

    /// <summary>Second fallback for netstat builds without <c>-p</c>. Runs without sudo; no process information.</summary>
    public const string NetstatNoProcessCommand = "netstat -tuln";

    private const string HasSsCommand = "command -v ss >/dev/null 2>&1";

    // More than the 20 s a password sudo needs left (T7.R25); `ss` itself answers in well under a second.
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    public string Name => "listeners";

    public async Task<bool> IsApplicableAsync(ServerContext context, CancellationToken cancellationToken) =>
        await HasCommandAsync(context, HasSsCommand, cancellationToken)
        || await HasCommandAsync(context, "command -v netstat >/dev/null 2>&1", cancellationToken);

    public async Task<CollectorOutput> CollectAsync(ServerContext context, CancellationToken cancellationToken)
    {
        var useSudo = context.Server.CanUseSudo;
        var command = await HasCommandAsync(context, HasSsCommand, cancellationToken) ? Command : NetstatCommand;
        var result = await context.SshRunner.RunAsync(context.Server.Id, command,
            new SshRunOptions(CommandTimeout, useSudo), cancellationToken);
        if (command == NetstatCommand && result.ExitCode is not 0)
        {
            // Some BusyBox builds lack FEATURE_NETSTAT_PRG: `netstat: invalid option -- 'p'`. Without -p the listeners
            // have no process name or pid, but sockets are readable by any user, so no sudo.
            var failedDuration = result.Duration;
            result = await context.SshRunner.RunAsync(context.Server.Id, NetstatNoProcessCommand,
                new SshRunOptions(CommandTimeout), cancellationToken);
            result = result with { Duration = result.Duration + failedDuration };
        }

        return new CollectorOutput(result.StandardOutput, result.StandardError, result.ExitCode, result.Duration);
    }

    private static async Task<bool> HasCommandAsync(ServerContext context, string probe, CancellationToken cancellationToken)
    {
        var result = await context.SshRunner.RunAsync(context.Server.Id, probe,
            new SshRunOptions(CommandTimeout), cancellationToken);
        return result.ExitCode == 0;
    }

    public CollectorResult Parse(CollectorOutput output)
    {
        try
        {
            var result = new ListenerDiscoveryResult(ListenerCollectorParser.Parse(output.StandardOutput));
            return new CollectorResult(JsonSerializer.SerializeToElement(result));
        }
        catch (FormatException exception)
        {
            return new CollectorResult(Error: exception.Message);
        }
    }
}
