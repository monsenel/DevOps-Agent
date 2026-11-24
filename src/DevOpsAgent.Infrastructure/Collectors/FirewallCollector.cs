using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;

namespace DevOpsAgent.Infrastructure.Collectors;

public sealed class FirewallCollector : ICollector
{
    public const string Command =
        "if command -v ufw >/dev/null 2>&1; then printf '__DVA_FW__:ufw\\n'; ufw status verbose 2>&1 || true; fi; " +
        "if command -v firewall-cmd >/dev/null 2>&1; then printf '__DVA_FW__:firewalld\\n'; firewall-cmd --state 2>&1 || true; firewall-cmd --list-all 2>&1 || true; fi; " +
        "if command -v nft >/dev/null 2>&1; then printf '__DVA_FW__:nft\\n'; nft list ruleset 2>&1 || true; fi; " +
        "if command -v iptables >/dev/null 2>&1; then printf '__DVA_FW__:iptables\\n'; iptables -S 2>&1 || true; fi";

    private const string ApplicabilityCommand =
        "command -v ufw >/dev/null 2>&1 || command -v firewall-cmd >/dev/null 2>&1 || command -v nft >/dev/null 2>&1 || command -v iptables >/dev/null 2>&1";

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    public string Name => "firewall";

    public async Task<bool> IsApplicableAsync(ServerContext context, CancellationToken cancellationToken)
    {
        var result = await context.SshRunner.RunAsync(context.Server.Id, ApplicabilityCommand,
            new SshRunOptions(CommandTimeout), cancellationToken);
        return result.ExitCode == 0;
    }

    public async Task<CollectorOutput> CollectAsync(ServerContext context, CancellationToken cancellationToken)
    {
        var result = await context.SshRunner.RunAsync(context.Server.Id, Command,
            new SshRunOptions(CommandTimeout, context.Server.CanUseSudo), cancellationToken);
        return new CollectorOutput(result.StandardOutput, result.StandardError, result.ExitCode, result.Duration);
    }

    public CollectorResult Parse(CollectorOutput output)
    {
        var parsed = FirewallCollectorParser.Parse(output.StandardOutput);
        return new CollectorResult(JsonSerializer.SerializeToElement(parsed));
    }
}
