using System.Text.Json;
using DevOpsAgent.Application.Updates;
using DevOpsAgent.Domain.Agent;

namespace DevOpsAgent.Application.Agent;

public sealed record ToolCommand(string Command, bool UseSudo);

public sealed class ToolCommandBuilder(ToolRegistry tools, ConfigEditOptions? configEdits = null)
{
    private readonly ConfigEditOptions _configEdits = configEdits ?? new ConfigEditOptions();

    public ToolCommand Build(ActionRecord action)
    {
        ArgumentNullException.ThrowIfNull(action);
        using var arguments = JsonDocument.Parse(action.ArgumentsJson);
        var typed = tools.ValidateArguments(action.ToolName, arguments.RootElement);
        return typed switch
        {
            RestartServiceArgs value => new($"systemctl restart -- {Quote(value.Unit)}", true),
            RestartContainerArgs value => new($"docker restart -- {Quote(value.Container)}", false),
            ApplyUpdatesArgs value => new(BuildApplyUpdates(value), true),
            BlockIpArgs value => new(BuildBlockIp(value), true),
            UnblockIpArgs value => new(BuildUnblockIp(value), true),
            RunShellArgs value => new(value.Command, false),
            // The root step of edit_config (T8.8 / T7.R28); staging, the health check and a rollback run around it (ConfigEditService).
            EditConfigArgs { IsPrepared: true } value => new(ConfigEditService.ApplyCommand(value.Path,
                ConfigEditService.StagingDirectory(action.Id) + "/content", ConfigEditService.Timestamp(action), value.BaseSha256!,
                _configEdits.RuleFor(value.Path), ConfigEditService.Sha256(value.Content!)), true),
            _ => throw new InvalidOperationException($"Tool '{action.ToolName}' has no mutating command implementation.")
        };
    }

    public ToolCommand BuildReadOnly(string toolName, object arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return (toolName, arguments) switch
        {
            ("service_status", ServiceStatusArgs value) => new(
                $"systemctl show --property=Id,LoadState,ActiveState,SubState,UnitFileState -- {Quote(value.Unit)}", false),
            ("docker_ps", DockerPsArgs) => new(
                "docker ps --no-trunc --format '{{.ID}} {{.Names}} {{.Image}} {{.Status}} {{.Ports}}'", false),
            ("docker_disk_usage", DockerDiskUsageArgs) => new("docker system df -v", false),
            ("docker_logs", DockerLogsArgs value) => new($"docker logs --tail {value.Tail} {Quote(value.Container)} 2>&1", false),
            ("tail_log", TailLogArgs value) when AllowPath(value.Path) => new($"tail -n {value.Lines} -- {Quote(value.Path)}", false),
            ("read_file", ReadFileArgs value) when AllowPath(value.Path) => new($"head -c {value.MaxBytes} -- {Quote(value.Path)}", false),
            ("disk_usage", DiskUsageArgs { Path: not null } value) => new(
                $"df -P -- {Quote(value.Path)} 2>&1; printf '%s\\n' 'Top-level disk usage:'; timeout 90s du -x -h --max-depth=1 -- {Quote(value.Path)} 2>/dev/null | sort -hr | head -20", true),
            ("disk_usage", DiskUsageArgs) => new(
                "df -P -- / /var/lib/docker 2>&1; printf '%s\\n' 'Top-level directory usage:'; for path in / /var/lib/docker; do if [ -d \"$path\" ]; then printf '\\n%s\\n' \"$path\"; timeout 90s du -x -h --max-depth=1 -- \"$path\" 2>/dev/null | sort -hr | head -20; fi; done", true),
            // Read-only on every group: apt reads its cache and dnf runs cache-only (-C), so nothing is refreshed or written.
            ("list_updates", ListUpdatesArgs) => new(
                "if command -v apt >/dev/null 2>&1; then apt list --upgradable 2>/dev/null; rc=$?; "
                + "elif command -v dnf >/dev/null 2>&1; then dnf -C check-update; rc=$?; "
                + "else printf '%s\\n' 'No supported package manager found.' >&2; exit 127; fi; "
                + "ts=$(" + PackageListProbe.Command + "); ts=${ts%%.*}; "
                + "if [ -n \"$ts\" ]; then printf 'Package lists last refreshed: %s\\n' \"$(date -u -d \"@$ts\" '+%Y-%m-%dT%H:%M:%SZ' 2>/dev/null || printf '%s' \"$ts\")\"; "
                + "else printf '%s\\n' 'Package lists last refreshed: unknown'; fi; exit \"$rc\"", false),
            ("list_bans", ListBansArgs) => new(
                "if command -v fail2ban-client >/dev/null 2>&1 && fail2ban-client status sshd >/dev/null 2>&1; then fail2ban-client status sshd; "
                + "elif command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -q '^Status: active'; then ufw status numbered; "
                + "elif command -v nft >/dev/null 2>&1; then nft list table inet dva 2>/dev/null || true; "
                + "else printf '%s\\n' 'No supported firewall tool found.'; fi", false),
            _ => throw new InvalidOperationException($"Tool '{toolName}' has no read-only command implementation.")
        };
    }

    private static string BuildApplyUpdates(ApplyUpdatesArgs value)
    {
        var packages = string.Join(" ", value.Packages.Select(Quote));
        return "set -eu; before=$(mktemp); after=$(mktemp); trap 'rm -f \"$before\" \"$after\"' EXIT; "
            + "units=$(systemctl list-units --type=service --state=running --no-legend --plain 2>/dev/null | awk '{print $1}' || true); "
            + "for unit in $units; do printf '%s %s\\n' \"$unit\" \"$(systemctl show -p MainPID --value \"$unit\" 2>/dev/null || true)\"; done > \"$before\"; "
            + "if command -v apt-get >/dev/null 2>&1; then apt-get install -y -- " + packages
            + "; elif command -v dnf >/dev/null 2>&1; then dnf upgrade -y -- " + packages
            + "; else printf '%s\\n' 'No supported package manager found.' >&2; exit 127; fi; "
            + "units=$(systemctl list-units --type=service --state=running --no-legend --plain 2>/dev/null | awk '{print $1}' || true); "
            + "for unit in $units; do printf '%s %s\\n' \"$unit\" \"$(systemctl show -p MainPID --value \"$unit\" 2>/dev/null || true)\"; done > \"$after\"; "
            + "printf '%s\\n' 'RESTARTED_SERVICES_BEGIN'; awk 'NR==FNR {before[$1]=$2; next} ($1 in before) && before[$1]!=$2 {print $1}' \"$before\" \"$after\"; "
            + "printf '%s\\n' 'RESTARTED_SERVICES_END'; if [ -e /var/run/reboot-required ]; then printf '%s\\n' 'REBOOT_REQUIRED=true'; "
            // `needs-restarting -r` exits 1 when a reboot IS needed and 0 when it is not; any other status is unknown and treated as no reboot.
            + "elif command -v needs-restarting >/dev/null 2>&1; then rc=0; needs-restarting -r >/dev/null 2>&1 || rc=$?; "
            + "if [ \"$rc\" -eq 1 ]; then printf '%s\\n' 'REBOOT_REQUIRED=true'; else printf '%s\\n' 'REBOOT_REQUIRED=false'; fi; "
            + "else printf '%s\\n' 'REBOOT_REQUIRED=false'; fi";
    }

    private static string BuildBlockIp(BlockIpArgs value) =>
        "if command -v fail2ban-client >/dev/null 2>&1 && fail2ban-client status sshd >/dev/null 2>&1; then fail2ban-client set sshd banip " + Quote(value.Address)
        + "; elif command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -q '^Status: active'; then ufw insert 1 deny from " + Quote(value.Address)
        + "; elif command -v nft >/dev/null 2>&1; then " + BuildNftablesBlock(value.Address)
        + "; else printf '%s\\n' 'No supported firewall tool found.' >&2; exit 127; fi";

    private static string BuildUnblockIp(UnblockIpArgs value) =>
        "if command -v fail2ban-client >/dev/null 2>&1 && fail2ban-client status sshd >/dev/null 2>&1; then fail2ban-client set sshd unbanip " + Quote(value.Address)
        + "; elif command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -q '^Status: active'; then ufw delete deny from " + Quote(value.Address)
        + "; elif command -v nft >/dev/null 2>&1; then " + BuildNftablesUnblock(value.Address)
        + "; else printf '%s\\n' 'No supported firewall tool found.' >&2; exit 127; fi";

    private static string BuildNftablesBlock(string address)
    {
        var isIpv6 = address.Contains(':');
        var family = isIpv6 ? "ipv6_addr" : "ipv4_addr";
        var match = isIpv6 ? "ip6 saddr" : "ip saddr";
        var set = isIpv6 ? "blocked_ipv6" : "blocked_ipv4";
        return "nft list table inet dva >/dev/null 2>&1 || nft add table inet dva; "
            + "nft list chain inet dva input >/dev/null 2>&1 || nft add chain inet dva input '{ type filter hook input priority 0; policy accept; }'; "
            + "nft list set inet dva " + set + " >/dev/null 2>&1 || nft add set inet dva " + set
            + " '{ type " + family + "; flags timeout; timeout 24h; }'; "
            + "nft list chain inet dva input | grep -Fq '" + match + " @" + set + " drop' || nft add rule inet dva input "
            + match + " @" + set + " drop; nft add element inet dva " + set + " { " + Quote(address) + " timeout 24h }";
    }

    private static string BuildNftablesUnblock(string address)
    {
        var set = address.Contains(':') ? "blocked_ipv6" : "blocked_ipv4";
        return "nft delete element inet dva " + set + " { " + Quote(address) + " }";
    }

    // Defence in depth: the registry already rejects these paths, but a command must never be built for one.
    private static bool AllowPath(string path)
    {
        SensitivePathPolicy.EnsureAllowed(path);
        return true;
    }

    private static string Quote(string value) => $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";
}
