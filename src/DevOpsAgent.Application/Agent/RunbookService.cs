namespace DevOpsAgent.Application.Agent;

public sealed record RunbookStep(int Number, string Title, string Detail, string? SuggestedTool = null);

public sealed record RunbookDefinition(
    string Id,
    string Title,
    string Category,
    string Summary,
    string EstimatedRisk,
    IReadOnlyList<RunbookStep> Steps,
    string AgentPrompt);

/// <summary>
/// Built-in operational runbooks with step-by-step checklists and pre-filled Agent chat prompts.
/// </summary>
public sealed class RunbookService
{
    private static readonly IReadOnlyList<RunbookDefinition> BuiltInRunbooks =
    [
        new(
            Id: "nginx-reverse-proxy-tls",
            Title: "Nginx Reverse Proxy + TLS Setup",
            Category: "Networking",
            Summary: "Configure an Nginx virtual host with upstream proxying, security headers, TLS certificates, validation, and safe reload.",
            EstimatedRisk: "Medium",
            Steps:
            [
                new(1, "Inspect existing Nginx config and listeners", "Verify port 80/443 bindings and confirm the upstream backend port is reachable from the proxy host.", "query_graph"),
                new(2, "Check backend service health", "Confirm the target application container or systemd service is running and healthy before exposing it.", "service_status"),
                new(3, "Draft virtual host under /etc/nginx/sites-available/", "Define server_name, proxy_pass, X-Forwarded-* headers, and TLS certificate paths (fullchain.pem / privkey.pem).", "edit_config"),
                new(4, "Validate with nginx -t and reload", "Apply through edit_config so backup, nginx -t validation, reload, and automatic rollback protect existing sites.", "edit_config"),
                new(5, "Verify HTTPS endpoint and TLS expiry", "Add or run an HTTPS health check and run discovery to confirm the new domain edge appears on the Topology map.", "run_discovery")
            ],
            AgentPrompt: "Guide me through the Nginx Reverse Proxy + TLS Setup runbook. First ask me for the target server, domain name, upstream backend address/port, and TLS certificate path, then inspect the server read-only before proposing any edit_config changes."),

        new(
            Id: "new-server-security-hardening",
            Title: "New Server Security Hardening",
            Category: "Security",
            Summary: "Audit a newly provisioned Linux host for SSH hardening, firewall rules, fail2ban, unattended updates, CVEs, and file integrity baselines.",
            EstimatedRisk: "Medium",
            Steps:
            [
                new(1, "Run proactive security hardening audit", "Check sshd PermitRootLogin, PasswordAuthentication, firewall status, fail2ban, and unattended security updates.", "security_audit"),
                new(2, "Capture initial File Integrity Monitoring baseline", "Record SHA-256 baselines for /etc/ssh/sshd_config, /etc/passwd, /etc/group, /etc/sudoers, and /root/.ssh/authorized_keys.", "check_file_integrity"),
                new(3, "Scan for known CVEs and pending OS security updates", "Run a read-only Trivy/Grype CVE scan and check cached package updates.", "scan_cve"),
                new(4, "Verify restricted sudoers drop-in", "Ensure the SSH user has a minimal NOPASSWD sudoers file rather than unrestricted ALL access."),
                new(5, "Confirm SSH key login before tightening sshd_config", "Verify a second SSH session works before disabling password authentication or root login.")
            ],
            AgentPrompt: "Run the New Server Security Hardening runbook on my target server. Ask which server to inspect first, then run security_audit, check_file_integrity, scan_cve, and list_updates read-only and give me a prioritized hardening checklist."),

        new(
            Id: "docker-compose-service-setup",
            Title: "Docker Compose Service Setup",
            Category: "Containers",
            Summary: "Deploy or update a Docker Compose stack under /opt or /srv with syntax validation, resource checks, and discovery verification.",
            EstimatedRisk: "Medium",
            Steps:
            [
                new(1, "Check disk capacity and existing containers", "Confirm free disk space on / and /var/lib/docker and verify no port conflicts exist.", "disk_usage"),
                new(2, "Review existing Docker containers and networks", "List running containers so the new service joins the right private bridge network.", "docker_ps"),
                new(3, "Create or update compose.yml via edit_config", "Place the compose file under /opt/<app>/compose.yml or /srv/<app>/compose.yml so docker compose config -q validates it before applying.", "edit_config"),
                new(4, "Start or restart containers with explicit approval", "Bring up the stack and tail container logs to verify clean startup.", "docker_logs"),
                new(5, "Re-run discovery", "Confirm the new container and its network edges are mapped accurately in Topology.", "run_discovery")
            ],
            AgentPrompt: "Help me execute the Docker Compose Service Setup runbook. Ask for the target server, service name, image, ports, and path under /opt or /srv, then inspect disk_usage and docker_ps before proposing a validated edit_config change."),

        new(
            Id: "safe-rolling-patch",
            Title: "Safe Rolling Patch",
            Category: "Maintenance",
            Summary: "Apply OS security updates wave-by-wave in topology order (leaves/backends first, reverse proxies last) with health checks between waves.",
            EstimatedRisk: "High",
            Steps:
            [
                new(1, "Review pending updates and topology wave order", "Check the Rolling patch plan on the Updates page and verify which servers have pending security updates.", "list_updates"),
                new(2, "Verify baseline health before Wave 1", "Confirm there are no Critical open alerts and all existing health checks are passing.", "query_graph"),
                new(3, "Patch Wave 1 (leaf / non-proxied backends)", "Apply approved updates one server at a time and check whether a reboot is required.", "apply_updates"),
                new(4, "Verify Wave 1 health gate", "Confirm systemd services, containers, and HTTP/TCP health checks are green before advancing.", "service_status"),
                new(5, "Advance through application tier and finish with edge proxies", "Patch reverse proxies last so traffic routing stays verified at every step.")
            ],
            AgentPrompt: "Guide me through the Safe Rolling Patch runbook. Check the discovery graph and pending updates read-only first, present the wave-by-wave patch order (backends first, reverse proxies last), and ask for my approval before proposing any apply_updates action."),

        new(
            Id: "incident-triage",
            Title: "Incident Triage",
            Category: "Incident Response",
            Summary: "Stabilize an active outage: enable Incident Mode, isolate the blast radius in Topology, inspect failing services/logs, and verify recovery.",
            EstimatedRisk: "Low",
            Steps:
            [
                new(1, "Enable Incident Mode on Alerts", "Prioritize High/Critical alerts and suppress Low-severity notification noise during triage."),
                new(2, "Identify affected services and upstream blast radius", "Query the topology graph to find which domains and proxies depend on the failing host or service.", "query_graph"),
                new(3, "Inspect failing service state and recent logs", "Check systemd unit status, container states, and tail the latest error logs.", "tail_log"),
                new(4, "Check external metrics/logs and recent file drift", "Query Prometheus/VictoriaLogs if configured and check file integrity for unexpected config changes.", "query_observability"),
                new(5, "Mitigate with approval and confirm recovery", "Propose minimal targeted remediation (restart/rollback), confirm alerts clear, then end Incident Mode.")
            ],
            AgentPrompt: "Start the Incident Triage runbook. Summarize current open High/Critical alerts, inspect the topology blast radius and recent logs read-only for the affected server, and suggest the safest next diagnostic or remediation step before making any changes.")
    ];

    public IReadOnlyList<RunbookDefinition> List(string? query = null)
    {
        if (string.IsNullOrWhiteSpace(query)) return BuiltInRunbooks;
        var term = query.Trim();
        return BuiltInRunbooks
            .Where(item =>
                item.Id.Contains(term, StringComparison.OrdinalIgnoreCase)
                || item.Title.Contains(term, StringComparison.OrdinalIgnoreCase)
                || item.Category.Contains(term, StringComparison.OrdinalIgnoreCase)
                || item.Summary.Contains(term, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    public RunbookDefinition? GetById(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return BuiltInRunbooks.FirstOrDefault(item => string.Equals(item.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public static string BuildChatUrl(RunbookDefinition runbook, string? serverName = null)
    {
        ArgumentNullException.ThrowIfNull(runbook);
        var prompt = string.IsNullOrWhiteSpace(serverName)
            ? runbook.AgentPrompt
            : $"Target server: {serverName.Trim()}. {runbook.AgentPrompt}";
        return $"/agent-chat?prompt={Uri.EscapeDataString(prompt)}";
    }
}
