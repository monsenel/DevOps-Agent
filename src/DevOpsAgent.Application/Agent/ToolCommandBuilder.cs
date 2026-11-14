using System.Text.Json;
using DevOpsAgent.Domain.Agent;

namespace DevOpsAgent.Application.Agent;

public sealed record ToolCommand(string Command, bool UseSudo);

public sealed class ToolCommandBuilder(ToolRegistry tools)
{
    public ToolCommand Build(ActionRecord action)
    {
        ArgumentNullException.ThrowIfNull(action);
        using var arguments = JsonDocument.Parse(action.ArgumentsJson);
        var typed = tools.ValidateArguments(action.ToolName, arguments.RootElement);
        return typed switch
        {
            RestartServiceArgs value => new($"systemctl restart -- {Quote(value.Unit)}", true),
            ApplyUpdatesArgs value => new(BuildApplyUpdates(value), true),
            BlockIpArgs value => new(BuildBlockIp(value), true),
            UnblockIpArgs value => new(BuildUnblockIp(value), true),
            RunShellArgs value => new(value.Command, false),
            _ => throw new InvalidOperationException($"Tool '{action.ToolName}' has no mutating command implementation.")
        };
    }

    private static string BuildApplyUpdates(ApplyUpdatesArgs value)
    {
        var packages = string.Join(" ", value.Packages.Select(Quote));
        return "if command -v apt-get >/dev/null 2>&1; then apt-get install -y -- " + packages
            + "; elif command -v dnf >/dev/null 2>&1; then dnf upgrade -y -- " + packages
            + "; else printf '%s\\n' 'No supported package manager found.' >&2; exit 127; fi";
    }

    private static string BuildBlockIp(BlockIpArgs value) =>
        "if command -v fail2ban-client >/dev/null 2>&1; then fail2ban-client set sshd banip " + Quote(value.Address)
        + "; elif command -v ufw >/dev/null 2>&1; then ufw insert 1 deny from " + Quote(value.Address)
        + "; else printf '%s\\n' 'No supported firewall tool found.' >&2; exit 127; fi";

    private static string BuildUnblockIp(UnblockIpArgs value) =>
        "if command -v fail2ban-client >/dev/null 2>&1; then fail2ban-client set sshd unbanip " + Quote(value.Address)
        + "; elif command -v ufw >/dev/null 2>&1; then ufw delete deny from " + Quote(value.Address)
        + "; else printf '%s\\n' 'No supported firewall tool found.' >&2; exit 127; fi";

    private static string Quote(string value) => $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";
}
