# Changelog

## 0.4.0 — 2026-11-24

v2 operations, discovery, LLM routing, and full M7 hardening release (1,121 tests passing, 0 build warnings, 0 new NuGet packages).

### New features
- **Per-conversation chat execution mode (T9.4):** `ChatMode` dropdown (`ReadOnly`, `Confirm`, `Auto`) in Agent chat persisted per conversation, replacing English keyword filtering with mode-based `SelectToolsForMode` (works seamlessly with Persian and natural-language prompts). In `Auto` mode, safe typed tools execute automatically on `Confirm`-mode server groups and log as `auto (chat)` while `run_shell`, `apply_updates`, critical `restart_service` units, and `ReadOnly` server groups remain strictly gated; includes ask-before-acting system prompt guardrails and `/agent-chat?prompt=…` draft pre-filling for Runbook links.
- **v2 Deterministic read-only collectors:** added `FirewallCollector` (`ufw`/`nft`/`iptables`/`firewalld`), `TlsCollector` (OpenSSL certificate metadata), `ReverseProxyCollector` (HAProxy, Traefik, Caddy), and `AppConfigCollector` (PostgreSQL, Redis, Nexus, and app connection strings with mandatory credential redaction via `AppConfigRedactor`), all exempt in `AppSudoCommands`.
- **LLM task routing, fallback & edge suggestions:** per-task provider routing (Chat, Quick/Summary, Security/Audit) via `LlmRoutingService`, automatic primary-to-fallback failover via `FallbackChatClient`, Settings › LLM controls, and read-only `LlmEdgeSuggestionService` for proposing candidate `inferred` graph edges without modifying persisted topology.
- **Daily digest:** scheduled UTC-hour status summary (`DailyDigestService`, `DailyDigestHostedService`, Settings › Notifications) sending D-028 aggregate-only counts over enabled Telegram/ntfy channels.
- **CVE scanning & File Integrity Monitoring:** deterministic read-only Trivy/Grype parser (`CveScanParser`, `CveScanService`, `scan_cve` tool) and SHA-256 baseline/drift monitor (`FileIntegrityService`, `check_file_integrity` tool, `file-integrity-changed` High alert with outage hold) covering `/etc/ssh/sshd_config`, `/etc/passwd`, `/etc/group`, `/etc/sudoers`, and `/root/.ssh/authorized_keys`.
- **Rolling patch waves:** topology-ordered update wave planner (`RollingUpdatePlanner`, Updates page preview panel) sequencing leaf/backend hosts → application tier → reverse proxies/edge last with per-wave verification gates.
- **Operational runbooks:** 5 built-in interactive checklists (`RunbookService`, `/runbooks` page, sidebar and top-bar navigation, `list_runbooks` tool) launching pre-filled Agent chat prompts.
- **Anomaly baselines, Incident Mode & external observability:** rolling mean/σ baseline detection (`AnomalyBaselineService`, `metric-anomaly` Medium alert), persistent **Incident Mode** (`IncidentModeService`, Alerts page toggle prioritizing High/Critical and suppressing Low notifications), and read-only Prometheus/VictoriaLogs client (`ExternalObservabilityService`, `query_observability` tool) with `LlmEndpointLocality.AllowsCredentials` enforcement and encrypted bearer tokens.

### Reliability, security & hardening
- **SSH lifecycle, deadlines, shutdown & auth hardening (T7.R19 v2, T7.R21–T7.R23):** claim-token protection between cancelled and bypass callers, target-backoff-before-jump-host short-circuit, `"Not tested"` label for never-contacted servers, `"is not answering commands"` timeout wording, alert-cycle slot-starvation and sudo-retry-cut holding without false High alerts, `SshShutdownException` propagation across actions/updates/scans, and 3-failure auth pause preserved across transient connect flaps.
- **Sudo password & `known_hosts` follow-ups (T7.R27, T7.R29):** persisted-pause retries and `pam_faillock` detection, unread-stdin detection (`sudo ran, password not checked`), interval-merged overlapping secret redaction (`KnownSecretRedaction`), `@revoked`/`@cert-authority` enforcement, fail-closed missing/unreadable configured `known_hosts`, `flushToDisk` before atomic rename, one-time key-accept confirmation tokens, multi-server `(Host, Port)` eviction, jump-host mismatch badge/wording, and per-`(host:port, keyType)` managed entries.
- **Pinnable sudoers profile (T7.R14):** restricted `/etc/sudoers.d/devops-agent` and typed root helper generator (`SudoersProfileGenerator`) with README deployment documentation.
- **Discovery & stale carry-forward fixes (T7.R9, T7.R16):** `/proc/<pid>/cgroup` listener-to-systemd unit mapping with fallback, multi-address stale IP seeding, host collector `LastSeen` timestamps, exclusion of stale/failed listener runs from new-listener drift alerts, dashed stale edge styling and C# badge rendering in `graph.js`, and non-default `IGraphRepository.GetEntitiesAsync`.
- **Chat, redaction, search, impact & test gaps (T7.R12, T7.R30):** extracted `ChatContentRedactor` (bare tokens, CLI flags, truncated PEM, >4 KB, and multi-word env coverage), real GIF header validation and 4 MB / 5-image conversation replay cap, per-message unredacted image confirmation guard for non-local providers, streaming multi-chunk & tool-call aggregation fixes, staleness & inventory-target warnings in `ImpactService`/`ActionImpact`, and lightweight `IGraphSearchRepository` search path with query length cap and cancellation cleanup.
- **UI & theme verification (T7.R11):** verified 0 hardcoded hex colors in `Components` and `app.css`, aligned all `var(--dva-*)` tokens across both light and dark themes (fixing 5 legacy undefined token references), verified semantic `<button>`/`<a>`/`<label>` controls, ≥ 44px touch targets, 390px overflow-safe responsive grids, and `/runbooks` top-bar mapping.

## 0.3.0 — 2026-11-23

Personal operations release: scheduled update alerts, expanded config scaffolding, proactive security hardening audit, and core M7 reliability fixes (1,000 tests passing, 0 build warnings).

### New features
- **Scheduled update scans & security alerts (T9.1):** automatic periodic OS update scans (`UpdateScanHostedService`, default every 24 h, configurable 0–168 h in Settings › Discovery, cache-only on read-only groups) and a `Medium` `security-updates-available` alert rule that notifies via Telegram/ntfy, holds across failed scans, and auto-resolves when 0 security packages remain.
- **Initial service config scaffolding (T9.2):** `edit_config` defaults expanded to `/etc/nginx/**`, `/etc/systemd/system/*.service` (`systemd-analyze verify` + `systemctl daemon-reload`), and `/opt|srv/**/compose.yml` / `docker-compose.yml` (`docker compose config -q`), with `create: true` for safely creating brand-new files (parent directory creation, symlink checks, automatic deletion on rollback).
- **Proactive security hardening audit (T9.3):** deterministic read-only `SecurityAuditService` inspecting `sshd` effective settings (`PermitRootLogin`, `PasswordAuthentication`, `PermitEmptyPasswords`, `MaxAuthTries`), host firewall (`ufw`/`nft`/`iptables`), `fail2ban` `sshd` jail, unattended security upgrades, and pending security updates; exposed via the read-only `security_audit` agent tool and a **Security hardening** card on Server detail.

### Reliability & safety
- **Critical service guardrails (T7.R4):** `restart_service` refuses `ssh`, `sshd`, `docker`, and `containerd` (including `.service`/`.socket` and `@` templates) unless `confirmCritical: true` is passed after explicit confirmation, enforced at both proposal and approval time.
- **Host-owned discovery (T7.R8):** app-wide single-flight `IDiscoveryCoordinator` executing in a host-owned DI scope bound to application shutdown so closing a browser tab never interrupts an in-progress discovery.
- **Outage-safe alert holding (T7.R20):** probe-driven alerts (`container-state`, `docker-status-unavailable`, `unit-failed`), resource thresholds on stale metrics, and failed auth-log scans stay held during outages or truncated probes (up to 24 h) instead of falsely resolving; restart-count increases across unsampled gaps no longer open fresh restart-loop alerts.
- **`edit_config` hardening (T7.R28):** requires a matching `Services` rule with a validation command; uses dot-prefixed temp and backup files so wildcard globs (`sites-enabled/*`) never include them; verifies staged file hash and non-symlink status as root; verifies and recovers on lost connection during apply; preserves CRLF and final-newline changes in unified diffs.
- **UI & updates polish (T7.R10):** `apply_updates` automatically re-runs the server's enabled health checks and appends results to the action output; live **Groups** list in the sidebar linking to `/servers?group=…`; SSH-config import offers groups in use; Topology adds a **Subnet** filter across all views.
- **Monitoring & secret handling (T7.R5–T7.R7):** HTTPS/private-host enforcement for endpoints receiving credentials; Docker `Cmd`/labels and raw `nginx -T` secret redaction before storage; relative nginx `include` resolution; bounded background notification queue; SQLite UTC-tick metric time filters; cross-source sudo event deduplication.

## 0.2.0 — 2026-11-16

- **Servers map view & SSH visibility (T8.11–T8.18):** Servers card map view grouped by role with aggregated port arrows, grouped unknown machines, real hostnames and service ports, nginx `set` variable resolution, BusyBox (Alpine) host/listener fallback, external client separation, and per-server inbound SSH session listing with allowed source IP/CIDR rules and map warnings.
- **Agent & workflow upgrades (T8.1–T8.10):** `edit_config` with backup, validation, reload, health check, and automatic rollback; approval card Impact preview and package-restart awareness; global top-bar search; automatic TOFU `known_hosts` with explicit key-change acceptance; chat attachments, reasoning effort selector, Markdown rendering, and configurable tool-call limits with Continue and loop guard; Updates select-all/security-only and package source listing; icon-only theme toggle.
- **SSH lifecycle, sudo password & container monitoring (T7.R15, T7.R17–T7.R19, T7.R24–T7.R26):** leased pooled SSH clients, command-timeout backoff, cycle deadlines and fast SIGTERM shutdown, container state alerts (`paused`, `created`/`removing` > 10 min) and Docker permission reporting, `ssh-auth-failed` rule with 3-attempt pause cap, and optional per-server stdin sudo password with marker confirmation, lockout protection, and cross-server redaction.
- Includes independent review fixes (r1–r4) for SSH backoff, health checks, restart-loop detection, cycle deadlines, sudo authentication window, and redaction.

## 0.1.2 — 2026-11-15

- T7.R1: per-host SSH backoff (1 min doubling to 15 min, reset on success); servers behind an unreachable jump host are reported unreachable without a connection attempt; alerts, metrics and health checks run 8 servers in parallel; "Retry now" on Servers bypasses the backoff. With a blackholed host, discovery takes ~15 s and metric cycles stay well under 60 s.
- T7.R2: an unreachable server's nodes, listeners and edges are carried forward from the last snapshot, dimmed and labelled "stale · last seen <time>"; outages and recoveries no longer produce drift or new-listener alerts.
- T7.R3: medium alert for containers that are restarting or restarted 3+ times in 10 minutes, from a read-only container query in the metric cycle; resolves after 10 quiet minutes or when the container is removed.

## 0.1.1 — 2026-11-15

Review and close-out of the 0.1.0 MVP. Journeys J1–J7 re-verified on the disposable lab in one clean run.

- Safety: Auto execution mode removed (stored Auto reads as Confirm); groups default to read-only and an unreadable setting fails closed; read-only groups never write on servers (cache-only update scans with "last refreshed"); approve/reject is an atomic transition from Proposed; approval previews show the exact command sent over SSH; `block_ip` refuses app, inventory, jump-host, operator and loopback addresses.
- Secrets: notifications carry only server, rule, severity and state; ntfy topic stored encrypted and masked; tool and action output redacted before it is stored, shown or sent; broader redaction (env/ini, JSON, Bearer, URL userinfo, PEM); `read_file`/`tail_log` refuse sensitive paths; HttpClient logging at Warning; proxy_pass credentials stripped from evidence.
- Alerts: SSH host-key change is a Critical alert distinct from unreachable; sudo alerts de-duplicated per user and exempt only the app's own fixed command lines (sudo's split log lines rejoined).
- Reliability: one database context per unit of work in parallel code; dead SSH sessions recycled after repeated timeouts and evicted only when connection settings change; failed or interrupted discovery runs never become the graph base and are swept on startup; per-server results from fan-out tools.
- UI: `/dev/components` only in Development; login rate-limited; trace and topology search submit on Enter; capability probe looks in `/usr/sbin` and `/sbin`; readable update report; graph alert markers in text; unused stylesheet and Bootstrap removed.
- Open items are tracked as T7.R1–T7.R14 in `docs/PLAN.md` (stale nodes, SSH backoff and restart-loop rule are the high ones).

## 0.1.0 — MVP — 2026-11-15

- Ship the self-hosted Docker app for SSH inventory, topology discovery, monitoring, alerts, update management, and approved agent actions.
- Verify journeys J1–J7 on the disposable staging lab; J4 used the configured OpenRouter provider and its approved `web` restart was recorded in the Action log.
- Verify a clean-clone Docker build, login, and `srv-edge` discovery. Systemd live verification is deferred to a real LXC/VM and does not block this release.
