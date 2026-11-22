using System.Globalization;

namespace DevOpsAgent.Application.Security;

/// <summary>
/// Deterministic read-only parser for proactive server security hardening checks (T9.3, D-065).
/// Inspects sshd effective settings, host firewall, fail2ban sshd jail, and unattended security upgrades.
/// </summary>
public static class SecurityAuditParser
{
    public const string SshdMarker = "__DVA_SSHD__";
    public const string FirewallMarker = "__DVA_FIREWALL__";
    public const string Fail2BanMarker = "__DVA_FAIL2BAN__";
    public const string UnattendedMarker = "__DVA_UNATTENDED__";

    /// <summary>Read-only command that collects SSH, firewall, fail2ban, and unattended-upgrade posture.</summary>
    public const string AuditCommand =
        "printf '__DVA_SSHD__\\n'; "
        + "if command -v sshd >/dev/null 2>&1 && sshd -T 2>/dev/null; then true; "
        + "elif [ -r /etc/ssh/sshd_config ]; then cat /etc/ssh/sshd_config; "
        + "else printf '__DVA_SSHD_UNREADABLE__\\n'; fi; "
        + "printf '__DVA_FIREWALL__\\n'; "
        + "if command -v ufw >/dev/null 2>&1; then printf 'TOOL=ufw\\n'; ufw status 2>/dev/null || printf 'UFW_UNREADABLE\\n'; "
        + "elif command -v nft >/dev/null 2>&1; then printf 'TOOL=nft\\n'; nft list ruleset 2>/dev/null || printf 'NFT_UNREADABLE\\n'; "
        + "elif command -v iptables >/dev/null 2>&1; then printf 'TOOL=iptables\\n'; iptables -S 2>/dev/null || printf 'IPTABLES_UNREADABLE\\n'; "
        + "else printf 'TOOL=none\\n'; fi; "
        + "printf '__DVA_FAIL2BAN__\\n'; "
        + "if command -v fail2ban-client >/dev/null 2>&1; then fail2ban-client status sshd 2>/dev/null || printf 'F2B_NO_SSHD\\n'; "
        + "else printf 'F2B_NONE\\n'; fi; "
        + "printf '__DVA_UNATTENDED__\\n'; "
        + "if [ -f /etc/apt/apt.conf.d/20auto-upgrades ]; then cat /etc/apt/apt.conf.d/20auto-upgrades; "
        + "elif command -v systemctl >/dev/null 2>&1 && systemctl is-enabled dnf-automatic.timer >/dev/null 2>&1; then printf 'DNF_AUTOMATIC=enabled\\n'; "
        + "else printf 'UNATTENDED_NONE\\n'; fi";

    public static SecurityAuditReport Parse(
        Guid serverId,
        string serverName,
        string output,
        DateTimeOffset auditedAtUtc,
        int pendingSecurityUpdates = 0)
    {
        var sshdSection = Section(output, SshdMarker, FirewallMarker);
        var firewallSection = Section(output, FirewallMarker, Fail2BanMarker);
        var fail2banSection = Section(output, Fail2BanMarker, UnattendedMarker);
        var unattendedSection = Section(output, UnattendedMarker, null);

        var findings = new List<SecurityAuditFinding>();
        ParseSshd(sshdSection, findings);
        ParseFirewall(firewallSection, findings);
        ParseFail2Ban(fail2banSection, findings);
        ParseUnattended(unattendedSection, findings);

        if (pendingSecurityUpdates > 0)
        {
            findings.Add(new SecurityAuditFinding(
                "updates-security-pending",
                "Updates",
                SecurityFindingSeverity.Warning,
                $"{pendingSecurityUpdates} security update(s) pending",
                "The latest update scan reported unapplied security packages.",
                "Review and apply pending security updates from the Updates page."));
        }

        return new SecurityAuditReport(serverId, serverName, auditedAtUtc, findings);
    }

    private static void ParseSshd(string text, List<SecurityAuditFinding> findings)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Contains("__DVA_SSHD_UNREADABLE__", StringComparison.Ordinal))
        {
            findings.Add(new SecurityAuditFinding("ssh-config", "SSH", SecurityFindingSeverity.Info,
                "SSH daemon configuration could not be read",
                "Neither sshd -T nor /etc/ssh/sshd_config was readable without elevated privileges.",
                "Ensure sudo is available so DevOps Agent can inspect effective sshd settings."));
            return;
        }

        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var split = line.Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries);
            if (split.Length == 2)
            {
                // First occurrence wins in sshd_config; sshd -T outputs one effective line per directive.
                settings.TryAdd(split[0], split[1].Trim().ToLowerInvariant());
            }
        }

        if (settings.TryGetValue("permitrootlogin", out var rootLogin))
        {
            if (rootLogin == "yes")
            {
                findings.Add(new SecurityAuditFinding("ssh-root-login", "SSH", SecurityFindingSeverity.High,
                    "Root SSH login is enabled",
                    "PermitRootLogin is set to 'yes', allowing direct root password and key logins.",
                    "Set 'PermitRootLogin no' in /etc/ssh/sshd_config and reload sshd."));
            }
            else if (rootLogin is "prohibit-password" or "without-password" or "forced-commands-only")
            {
                findings.Add(new SecurityAuditFinding("ssh-root-login", "SSH", SecurityFindingSeverity.Info,
                    $"Root SSH login is restricted ({rootLogin})",
                    "Root password login is blocked, though key-based root access remains allowed.",
                    "Prefer 'PermitRootLogin no' and administer via a sudo user."));
            }
            else
            {
                findings.Add(new SecurityAuditFinding("ssh-root-login", "SSH", SecurityFindingSeverity.Pass,
                    "Root SSH login is disabled",
                    $"PermitRootLogin is '{rootLogin}'."));
            }
        }

        if (settings.TryGetValue("passwordauthentication", out var passwordAuth))
        {
            if (passwordAuth == "yes")
            {
                findings.Add(new SecurityAuditFinding("ssh-password-auth", "SSH", SecurityFindingSeverity.High,
                    "SSH password authentication is enabled",
                    "PasswordAuthentication is 'yes', exposing SSH to password brute-force attacks.",
                    "Set 'PasswordAuthentication no' after confirming SSH key login works."));
            }
            else
            {
                findings.Add(new SecurityAuditFinding("ssh-password-auth", "SSH", SecurityFindingSeverity.Pass,
                    "SSH password authentication is disabled",
                    $"PasswordAuthentication is '{passwordAuth}'."));
            }
        }

        if (settings.TryGetValue("permitemptypasswords", out var emptyPasswords))
        {
            if (emptyPasswords == "yes")
            {
                findings.Add(new SecurityAuditFinding("ssh-empty-passwords", "SSH", SecurityFindingSeverity.High,
                    "Empty SSH passwords are permitted",
                    "PermitEmptyPasswords is 'yes'.",
                    "Set 'PermitEmptyPasswords no' immediately."));
            }
            else
            {
                findings.Add(new SecurityAuditFinding("ssh-empty-passwords", "SSH", SecurityFindingSeverity.Pass,
                    "Empty SSH passwords are disabled",
                    "PermitEmptyPasswords is 'no'."));
            }
        }

        if (settings.TryGetValue("maxauthtries", out var maxTriesText)
            && int.TryParse(maxTriesText, NumberStyles.None, CultureInfo.InvariantCulture, out var maxTries))
        {
            if (maxTries > 6)
            {
                findings.Add(new SecurityAuditFinding("ssh-max-auth-tries", "SSH", SecurityFindingSeverity.Warning,
                    $"SSH MaxAuthTries is high ({maxTries})",
                    "Allowing many authentication attempts per connection speeds up brute-force probes.",
                    "Set 'MaxAuthTries 3' or 'MaxAuthTries 4' in /etc/ssh/sshd_config."));
            }
            else
            {
                findings.Add(new SecurityAuditFinding("ssh-max-auth-tries", "SSH", SecurityFindingSeverity.Pass,
                    $"SSH MaxAuthTries is {maxTries}",
                    "Authentication attempts per connection are reasonably bounded."));
            }
        }
    }

    private static void ParseFirewall(string text, List<SecurityAuditFinding> findings)
    {
        if (text.Contains("UFW_UNREADABLE", StringComparison.Ordinal)
            || text.Contains("NFT_UNREADABLE", StringComparison.Ordinal)
            || text.Contains("IPTABLES_UNREADABLE", StringComparison.Ordinal))
        {
            findings.Add(new SecurityAuditFinding("firewall-status", "Firewall", SecurityFindingSeverity.Info,
                "Host firewall status requires sudo",
                "A firewall tool is installed, but its rules could not be read without root privileges.",
                "Grant read-only sudo access to inspect firewall status."));
            return;
        }

        if (text.Contains("TOOL=ufw", StringComparison.Ordinal))
        {
            if (text.Contains("Status: active", StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new SecurityAuditFinding("firewall-status", "Firewall", SecurityFindingSeverity.Pass,
                    "Host firewall (ufw) is active",
                    "ufw reports active filtering rules."));
            }
            else
            {
                findings.Add(new SecurityAuditFinding("firewall-status", "Firewall", SecurityFindingSeverity.High,
                    "Host firewall (ufw) is inactive",
                    "ufw is installed but currently disabled.",
                    "Allow SSH first (ufw allow OpenSSH), then enable ufw."));
            }

            return;
        }

        if (text.Contains("TOOL=nft", StringComparison.Ordinal))
        {
            var hasRules = text.Contains("hook input", StringComparison.OrdinalIgnoreCase)
                && (text.Contains("policy drop", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("drop", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("reject", StringComparison.OrdinalIgnoreCase));
            if (hasRules)
            {
                findings.Add(new SecurityAuditFinding("firewall-status", "Firewall", SecurityFindingSeverity.Pass,
                    "Host firewall (nftables) is active",
                    "nftables contains inbound filtering rules."));
            }
            else
            {
                findings.Add(new SecurityAuditFinding("firewall-status", "Firewall", SecurityFindingSeverity.High,
                    "Host firewall (nftables) has no inbound filtering rules",
                    "nftables is installed but no input drop/reject policy or rules were detected.",
                    "Configure an nftables input chain with a default-drop policy allowing only required ports."));
            }

            return;
        }

        if (text.Contains("TOOL=iptables", StringComparison.Ordinal))
        {
            var hasInputRules = text.Contains("-P INPUT DROP", StringComparison.Ordinal)
                || text.Contains("-P INPUT REJECT", StringComparison.Ordinal)
                || text.Contains("-A INPUT ", StringComparison.Ordinal);
            if (hasInputRules)
            {
                findings.Add(new SecurityAuditFinding("firewall-status", "Firewall", SecurityFindingSeverity.Pass,
                    "Host firewall (iptables) has active input rules",
                    "iptables reports active INPUT chain rules or default-deny policy."));
            }
            else
            {
                findings.Add(new SecurityAuditFinding("firewall-status", "Firewall", SecurityFindingSeverity.High,
                    "Host firewall (iptables) has no active input rules",
                    "iptables INPUT policy is ACCEPT with no filtering rules.",
                    "Enable ufw or configure iptables/nftables inbound rules."));
            }

            return;
        }

        findings.Add(new SecurityAuditFinding("firewall-status", "Firewall", SecurityFindingSeverity.High,
            "No host firewall tool detected",
            "None of ufw, nftables, or iptables were found on this host.",
            "Install and configure ufw or nftables unless a dedicated upstream firewall isolates this server."));
    }

    private static void ParseFail2Ban(string text, List<SecurityAuditFinding> findings)
    {
        if (text.Contains("Status for the jail: sshd", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new SecurityAuditFinding("fail2ban-sshd", "Intrusion prevention", SecurityFindingSeverity.Pass,
                "fail2ban sshd jail is active",
                "Automated SSH brute-force banning is enabled."));
        }
        else if (text.Contains("F2B_NO_SSHD", StringComparison.Ordinal))
        {
            findings.Add(new SecurityAuditFinding("fail2ban-sshd", "Intrusion prevention", SecurityFindingSeverity.Warning,
                "fail2ban sshd jail is not active",
                "fail2ban is installed, but the sshd jail is not running or could not be queried.",
                "Enable [sshd] in /etc/fail2ban/jail.local and start fail2ban."));
        }
        else
        {
            findings.Add(new SecurityAuditFinding("fail2ban-sshd", "Intrusion prevention", SecurityFindingSeverity.Warning,
                "fail2ban is not installed",
                "No fail2ban daemon was found to automatically ban repeated SSH login failures.",
                "Install fail2ban and enable the sshd jail."));
        }
    }

    private static void ParseUnattended(string text, List<SecurityAuditFinding> findings)
    {
        var aptEnabled = text.Contains("Unattended-Upgrade", StringComparison.OrdinalIgnoreCase)
            && text.Contains("\"1\"", StringComparison.Ordinal);
        var dnfEnabled = text.Contains("DNF_AUTOMATIC=enabled", StringComparison.Ordinal);
        if (aptEnabled || dnfEnabled)
        {
            findings.Add(new SecurityAuditFinding("unattended-upgrades", "Updates", SecurityFindingSeverity.Pass,
                "Unattended security upgrades are enabled",
                aptEnabled ? "APT::Periodic::Unattended-Upgrade is enabled." : "dnf-automatic.timer is enabled."));
        }
        else
        {
            findings.Add(new SecurityAuditFinding("unattended-upgrades", "Updates", SecurityFindingSeverity.Warning,
                "Unattended security upgrades are not configured",
                "Automatic OS security patching was not detected on this server.",
                "Install unattended-upgrades (Debian/Ubuntu) or enable dnf-automatic.timer (RHEL/Fedora), or rely on scheduled scans here."));
        }
    }

    private static string Section(string output, string startMarker, string? endMarker)
    {
        var start = output.IndexOf(startMarker, StringComparison.Ordinal);
        if (start < 0) return string.Empty;
        start += startMarker.Length;
        if (endMarker is null) return output[start..];
        var end = output.IndexOf(endMarker, start, StringComparison.Ordinal);
        return end < 0 ? output[start..] : output[start..end];
    }
}
