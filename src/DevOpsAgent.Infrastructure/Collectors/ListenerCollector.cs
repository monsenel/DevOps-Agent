using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;

namespace DevOpsAgent.Infrastructure.Collectors;

public sealed class ListenerCollector : ICollector
{
    public const string Command = "ss -tulpnH";

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(15);

    public string Name => "listeners";

    public async Task<bool> IsApplicableAsync(ServerContext context, CancellationToken cancellationToken)
    {
        var result = await context.SshRunner.RunAsync(context.Server.Id, "command -v ss >/dev/null 2>&1",
            new SshRunOptions(CommandTimeout), cancellationToken);
        return result.ExitCode == 0;
    }

    public async Task<CollectorOutput> CollectAsync(ServerContext context, CancellationToken cancellationToken)
    {
        var useSudo = context.Server.CanUseSudo;
        var result = await context.SshRunner.RunAsync(context.Server.Id, Command,
            new SshRunOptions(CommandTimeout, useSudo), cancellationToken);
        return new CollectorOutput(result.StandardOutput, result.StandardError, result.ExitCode, result.Duration);
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
