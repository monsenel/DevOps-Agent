using System.Text;
using System.Text.RegularExpressions;

namespace DevOpsAgent.Application.Security;

public sealed record SudoersCommandSpec(string Alias, string Category, string CommandTemplate, bool IsReadOnly, string Purpose);

public sealed record SudoersProfileOptions(
    string SshUser,
    bool IncludeDiscovery = true,
    bool IncludeMonitoring = true,
    bool IncludeSecurityAudit = true,
    bool IncludeUpdates = false,
    bool IncludeServiceControl = false,
    bool IncludeFirewall = false,
    bool IncludeConfigEdit = false,
    string HelperScriptPath = SudoersProfileGenerator.DefaultHelperScriptPath);

public sealed record SudoersProfileResult(
    string SshUser,
    string SudoersContent,
    string HelperScriptTemplate,
    IReadOnlyList<SudoersCommandSpec> Commands);

/// <summary>
/// T7.R14 helper that generates a restricted, pinnable <c>/etc/sudoers.d/devops-agent</c> profile and optional
/// root-owned helper script for DevOps Agent's exact read-only and approved privileged commands without granting
/// unconstrained <c>sh -c</c> or <c>ALL</c> access.
/// </summary>
public static partial class SudoersProfileGenerator
{
    public const string DefaultHelperScriptPath = "/usr/local/sbin/dva-sudo-helper";

    private static readonly IReadOnlyList<SudoersCommandSpec> DiscoveryCommands =
    [
        new("DVA_DISCOVERY", "Discovery", "/usr/bin/true", true, "Non-interactive sudo capability probe"),
        new("DVA_DISCOVERY", "Discovery", "/usr/bin/ss -tulpnH", true, "Read listening TCP/UDP sockets and owning PIDs"),
        new("DVA_DISCOVERY", "Discovery", "/usr/bin/ss -tnpH", true, "Read established TCP connections"),
        new("DVA_DISCOVERY", "Discovery", "/usr/bin/netstat -tulpn", true, "Fallback listener probe on BusyBox/Alpine hosts"),
        new("DVA_DISCOVERY", "Discovery", "/usr/sbin/nginx -T", true, "Dump parsed nginx configuration"),
        new("DVA_DISCOVERY", "Discovery", "/usr/bin/docker info --format *", true, "Inspect Docker daemon metadata"),
        new("DVA_DISCOVERY", "Discovery", "/usr/bin/docker ps -a *", true, "List Docker containers and status"),
        new("DVA_DISCOVERY", "Discovery", "/usr/bin/docker network ls *", true, "List Docker networks"),
        new("DVA_DISCOVERY", "Discovery", "/usr/bin/docker inspect *", true, "Inspect container and network details"),
        new("DVA_DISCOVERY", "Discovery", "/usr/bin/nsenter -t [0-9]* -n /usr/bin/ss -tnpH", true, "Read container network namespace sockets")
    ];

    private static readonly IReadOnlyList<SudoersCommandSpec> MonitoringCommands =
    [
        new("DVA_MONITORING", "Monitoring", "/usr/bin/journalctl -u ssh*", true, "Read SSH authentication events for security alerts"),
        new("DVA_MONITORING", "Monitoring", "/usr/bin/systemctl --failed --type=service --no-legend --plain", true, "Check failed systemd units"),
        new("DVA_MONITORING", "Monitoring", "/usr/bin/df -P *", true, "Read filesystem mount usage"),
        new("DVA_MONITORING", "Monitoring", "/usr/bin/du -x -h --max-depth=1 *", true, "Inspect top-level directory sizes")
    ];

    private static readonly IReadOnlyList<SudoersCommandSpec> SecurityAuditCommands =
    [
        new("DVA_SECURITY", "Security audit", "/usr/sbin/sshd -T", true, "Read effective SSH daemon configuration"),
        new("DVA_SECURITY", "Security audit", "/usr/sbin/ufw status", true, "Check UFW firewall status"),
        new("DVA_SECURITY", "Security audit", "/usr/sbin/nft list ruleset", true, "Check nftables firewall ruleset"),
        new("DVA_SECURITY", "Security audit", "/usr/sbin/iptables -S", true, "Check iptables rules"),
        new("DVA_SECURITY", "Security audit", "/usr/bin/fail2ban-client status*", true, "Check fail2ban jail status and banned IPs")
    ];

    private static readonly IReadOnlyList<SudoersCommandSpec> UpdateCommands =
    [
        new("DVA_UPDATES", "Updates (approved)", "/usr/bin/apt-get update -qq", false, "Refresh APT package lists (non-read-only groups only)"),
        new("DVA_UPDATES", "Updates (approved)", "/usr/bin/apt-get install -y *", false, "Install owner-approved APT packages"),
        new("DVA_UPDATES", "Updates (approved)", "/usr/bin/dnf check-update*", false, "Check pending DNF updates"),
        new("DVA_UPDATES", "Updates (approved)", "/usr/bin/dnf upgrade -y *", false, "Install owner-approved DNF packages")
    ];

    private static readonly IReadOnlyList<SudoersCommandSpec> ServiceControlCommands =
    [
        new("DVA_SERVICES", "Service control (approved)", "/usr/bin/systemctl restart *", false, "Restart owner-approved systemd service"),
        new("DVA_SERVICES", "Service control (approved)", "/usr/bin/systemctl reload nginx", false, "Reload nginx after validated config edit"),
        new("DVA_SERVICES", "Service control (approved)", "/usr/bin/systemctl daemon-reload", false, "Reload systemd after validated unit edit")
    ];

    private static readonly IReadOnlyList<SudoersCommandSpec> FirewallCommands =
    [
        new("DVA_FIREWALL", "Firewall (approved)", "/usr/bin/fail2ban-client set sshd banip *", false, "Ban attacker IP in fail2ban"),
        new("DVA_FIREWALL", "Firewall (approved)", "/usr/bin/fail2ban-client set sshd unbanip *", false, "Unban IP in fail2ban"),
        new("DVA_FIREWALL", "Firewall (approved)", "/usr/sbin/ufw insert 1 deny from *", false, "Block attacker IP in UFW"),
        new("DVA_FIREWALL", "Firewall (approved)", "/usr/sbin/nft add element inet dva *", false, "Block attacker IP in dedicated nftables set"),
        new("DVA_FIREWALL", "Firewall (approved)", "/usr/sbin/nft delete element inet dva *", false, "Remove attacker IP from dedicated nftables set")
    ];

    public static SudoersProfileResult Generate(SudoersProfileOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var user = options.SshUser?.Trim() ?? string.Empty;
        if (!LinuxUserPattern().IsMatch(user))
            throw new ArgumentException("SSH username must be a valid POSIX username (lowercase letters, digits, '_', '-').", nameof(options));

        var helperPath = string.IsNullOrWhiteSpace(options.HelperScriptPath)
            ? DefaultHelperScriptPath
            : options.HelperScriptPath.Trim();
        if (!SafeHelperPathPattern().IsMatch(helperPath))
            throw new ArgumentException("Helper script path must be an absolute path under /usr/local/sbin/ or /usr/sbin/ without spaces.", nameof(options));

        var selected = new List<SudoersCommandSpec>();
        var aliases = new List<string>();

        if (options.IncludeDiscovery)
        {
            selected.AddRange(DiscoveryCommands);
            aliases.Add("DVA_DISCOVERY");
        }

        if (options.IncludeMonitoring)
        {
            selected.AddRange(MonitoringCommands);
            aliases.Add("DVA_MONITORING");
        }

        if (options.IncludeSecurityAudit)
        {
            selected.AddRange(SecurityAuditCommands);
            aliases.Add("DVA_SECURITY");
        }

        if (options.IncludeUpdates)
        {
            selected.AddRange(UpdateCommands);
            aliases.Add("DVA_UPDATES");
        }

        if (options.IncludeServiceControl)
        {
            selected.AddRange(ServiceControlCommands);
            aliases.Add("DVA_SERVICES");
        }

        if (options.IncludeFirewall)
        {
            selected.AddRange(FirewallCommands);
            aliases.Add("DVA_FIREWALL");
        }

        if (options.IncludeConfigEdit)
        {
            selected.Add(new("DVA_CONFIG_EDIT", "Config edit (approved)", $"{helperPath} read-config *", false, "Read allowlisted config via root-owned helper"));
            selected.Add(new("DVA_CONFIG_EDIT", "Config edit (approved)", $"{helperPath} apply-config *", false, "Backup, validate, and apply allowlisted config via root-owned helper"));
            selected.Add(new("DVA_CONFIG_EDIT", "Config edit (approved)", $"{helperPath} rollback-config *", false, "Restore previous backup via root-owned helper"));
            aliases.Add("DVA_CONFIG_EDIT");
        }

        if (aliases.Count == 0)
            throw new ArgumentException("Select at least one command group for the sudoers profile.", nameof(options));

        var sb = new StringBuilder();
        sb.AppendLine("# /etc/sudoers.d/devops-agent — generated by DevOps Agent (T7.R14)");
        sb.AppendLine("# Install with: sudo visudo -cf /etc/sudoers.d/devops-agent && sudo chmod 0440 /etc/sudoers.d/devops-agent");
        sb.AppendLine("# Keep this file restricted to the exact commands below; DevOps Agent alerts on any unexpected sudo by this user (D-031).");
        sb.AppendLine();
        sb.Append("Defaults:").Append(user).AppendLine(" !requiretty, use_pty");
        sb.AppendLine();

        foreach (var group in selected.GroupBy(item => item.Alias))
        {
            sb.Append("Cmnd_Alias ").Append(group.Key).Append(" = ");
            sb.AppendLine(string.Join(", \\\n    ", group.Select(item => item.CommandTemplate)));
            sb.AppendLine();
        }

        sb.Append(user).Append(" ALL=(root) NOPASSWD: ").AppendLine(string.Join(", ", aliases));

        return new SudoersProfileResult(user, sb.ToString(), BuildHelperScriptTemplate(helperPath), selected);
    }

    private static string BuildHelperScriptTemplate(string helperPath) =>
        $$"""
        #!/bin/sh
        # Root-owned typed helper ({{helperPath}}) — chmod 0755, chown root:root
        # Allows pinning config edits in sudoers without granting `sh -c`.
        set -eu
        action="${1:-}"
        target="${2:-}"
        case "$target" in
          /etc/nginx/*|/etc/systemd/system/*.service|/opt/*/compose.yml|/opt/*/docker-compose.yml|/srv/*/compose.yml|/srv/*/docker-compose.yml) ;;
          *) echo "Path not in allowlist" >&2; exit 2 ;;
        esac
        case "$target" in
          *..* ) echo "Path traversal rejected" >&2; exit 2 ;;
        esac
        case "$action" in
          read-config)
            cat -- "$target"
            ;;
          apply-config)
            staged="${3:-}"
            case "$staged" in /tmp/dva-edit-*/content) ;; *) echo "Invalid staged path" >&2; exit 2 ;; esac
            if [ -L "$target" ] || [ -L "$staged" ]; then echo "Symlink rejected" >&2; exit 2; fi
            cp -p -- "$staged" "$target"
            ;;
          rollback-config)
            backup="${3:-}"
            case "$backup" in *.dva-bak-*) ;; *) echo "Invalid backup path" >&2; exit 2 ;; esac
            if [ -L "$target" ] || [ -L "$backup" ]; then echo "Symlink rejected" >&2; exit 2; fi
            cp -p -- "$backup" "$target"
            ;;
          *)
            echo "Unknown action" >&2
            exit 2
            ;;
        esac
        """;

    [GeneratedRegex(@"\A[a-z_][a-z0-9_-]{0,31}\z", RegexOptions.CultureInvariant)]
    private static partial Regex LinuxUserPattern();

    [GeneratedRegex(@"\A/usr/(?:local/)?sbin/[A-Za-z0-9._-]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SafeHelperPathPattern();
}
