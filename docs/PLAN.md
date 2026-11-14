# Implementation plan — DevOps Agent

Tasks are small on purpose: each one should fit in a single agent session and end in a green build and a commit. Do them **in order**. Status lives in `docs/PROGRESS.md`, not here.

Each task lists: **Goal**, **Do**, **Done when** (acceptance criteria), **Depends on**.
Requirement IDs (LLM-1, DSC-2 …) refer to `docs/PRD.md`.

---

## M0 — Foundation

### T0.1 Solution skeleton
- **Goal:** Compiling solution with the layout in `ARCHITECTURE.md §1`.
- **Do:** `DevOpsAgent.slnx`; projects Domain, Application, Infrastructure, Web (Blazor Web App, Interactive Server), Tests (xUnit). `Directory.Build.props` (net10.0, LangVersion 14, Nullable, ImplicitUsings, TreatWarningsAsErrors). `Directory.Packages.props` (central versions). `.editorconfig`, `.gitignore` (bin, obj, data/, keys/, .env). Project references per dependency rule. One placeholder test.
- **Done when:** `dotnet build` and `dotnet test` pass; `dotnet run` serves the default page; AGENTS.md §3 commands verified/updated.
- **Depends on:** —

### T0.2 Docker and compose
- **Goal:** App runs in a container with persistent data.
- **Do:** Multi-stage `Dockerfile` (non-root, port 8080). `docker-compose.yml` with `app` (volumes `./data:/data`, `./keys:/keys:ro`, env file) and `ollama` under profile `ollama`. `.env.example`. `GET /healthz` returns 200 + version.
- **Done when:** `docker compose up --build` serves the app on :8080; `/healthz` 200; data dir created on host.
- **Depends on:** T0.1

### T0.3 Persistence
- **Goal:** EF Core + SQLite wired with migrations applied at startup.
- **Do:** `AppDbContext` in Infrastructure; connection string `Data Source=/data/devops-agent.db` (local dev: `./data/…`). Entities for `AppSetting` only for now. Initial migration. Apply migrations on startup. Data Protection key ring persisted to `/data/keys`; `ISecretProtector` implementation.
- **Done when:** DB file created on first run; migration applied; test for `ISecretProtector` round-trip.
- **Depends on:** T0.1

### T0.4 Single-user login
- **Goal:** Nothing is reachable without signing in.
- **Do:** Cookie auth. Credentials from env: `ADMIN_USER` + `ADMIN_PASSWORD_HASH` (if only `ADMIN_PASSWORD` given, hash at startup with `PasswordHasher` and log a warning). Login page per `DESIGN.md §8`. Logout in sidebar. `/healthz` stays anonymous. Antiforgery on.
- **Done when:** Unauthenticated requests redirect to `/login`; wrong password shows an error; right one reaches Overview.
- **Depends on:** T0.2, T0.5 (style the page after T0.5 if done out of order)

### T0.5 Design system and app shell
- **Goal:** Tokens, fonts, theme switching and all base components from `DESIGN.md`.
- **Do:** `wwwroot/css/tokens.css` (all tokens, both themes), `app.css`. Self-host Plus Jakarta Sans + JetBrains Mono woff2 (download once, commit, note licences in `wwwroot/fonts/LICENSES.md`). Inline head script for theme, `theme.js`, toggle button. Components in `Components/UI`: Card, StatTile, Button, Badge, Field, Select, SwitchRow, Segmented, AlertRow, CodeBlock, ProposedActionCard (presentational only), ProgressBar, EmptyState, Icon set. Layout: AppShell, Sidebar (nav, groups, Model card), TopBar. Placeholder pages for every nav item. A hidden `/dev/components` page showing every component in both themes.
- **Done when:** Shell matches `overview.dc.html` structure (sidebar, top bar, bento rows); theme toggle works and persists; no hex colors outside `tokens.css` (`grep -rn "#[0-9a-fA-F]\{6\}" src/DevOpsAgent.Web/Components` returns nothing); layout wraps at 390px.
- **Depends on:** T0.1

### T0.6 LLM providers — domain, storage, client
- **Goal:** Providers stored and usable from code.
- **Do:** `LlmProvider` entity + migration (API key protected). `ProviderService` CRUD, single default. `IChatClientFactory` creating an OpenAI-compatible `IChatClient` with custom base URL (verify current Microsoft.Extensions.AI API in docs first). `ModelLister` (`GET {base}/models`). Test routine: reachable, list models, one tiny completion with streaming, one call with a trivial tool to detect tool support → `ProviderTestResult`.
- **Done when:** Unit tests for CRUD/default rules and for the test routine using a fake HTTP handler; manual test against Ollama works.
- **Depends on:** T0.3

### T0.7 Settings › LLM connection page
- **Goal:** Page exactly as `settings-llm.dc.html` (LLM-1 … LLM-8).
- **Do:** Settings layout with segmented sections (other sections: placeholder cards). Providers list, Connection form with validation, Refresh models, Test connection with live result card, Save, Remove (confirm), switches. API key field never shows the stored key (show "•••• saved", replace on input). Type presets: Ollama `http://ollama:11434/v1`, LM Studio `http://host.docker.internal:1234/v1`, OpenRouter `https://openrouter.ai/api/v1`. Task routing + Fallback cards disabled with "v2" badge. Sidebar Model card shows the default provider and last test status.
- **Done when:** Add Ollama → Test passes → set default → sidebar updates; both themes match the mockup.
- **Depends on:** T0.5, T0.6

### T0.8 Basic chat (no tools)
- **Goal:** Prove the LLM path end to end.
- **Do:** Agent chat page layout (conversation list + chat card). Streaming responses from the default provider. Conversations and messages persisted.
- **Done when:** A conversation streams, survives page reload, works with Ollama and OpenRouter.
- **Depends on:** T0.7

---

## M1 — Servers and SSH

### T1.1 Server inventory
- **Do:** `Server` entity + migration. Servers page (table) + add/edit dialog: name, host, port, user, key file (dropdown of files in `/keys`), passphrase (protected), jump server, group, tags.
- **Done when:** CRUD works; validation (unique name, port range, key file exists).
- **Depends on:** T0.5, T0.3

### T1.2 SSH pool and runner
- **Do:** `SshConnectionPool`, `SshRunner` (timeout, sudo via `sudo -n`, stdout/stderr/exit code), jump-host tunnel via local port forwarding. "Test connection" on a server: OS (`/etc/os-release`), uptime, sudo capability, presence of `docker`, `nginx`, `systemctl`, `ss`, `ip`.
- **Done when:** Unit tests with a fake runner for the capability probe parsing; manual test against a real server and through a jump host.
- **Depends on:** T1.1

### T1.3 Import ssh config
- **Do:** `SshConfigParser` (Host, HostName, User, Port, IdentityFile, ProxyJump). Import dialog: paste text → preview rows → import. Map `IdentityFile` basename to files in `/keys`.
- **Done when:** Parser tests with fixtures incl. wildcard hosts (skipped) and ProxyJump.
- **Depends on:** T1.1

---

## M2 — Discovery base

### T2.1 Collector framework and snapshots
- **Do:** `ICollector`, `CollectorOutput`, `CollectorResult`, `ServerContext`. `DiscoveryService`: servers in parallel (max degree 8), collectors sequential per server, raw output stored in `CollectorRun`. Snapshot entity + migration. Progress events for UI.
- **Done when:** Running discovery with a fake collector creates a snapshot and collector runs; one failing server doesn't fail the run.
- **Depends on:** T1.2

### T2.2 Host collector (DSC-1)
- **Do:** `ip -j addr`, `ip -j route`, `hostname`, `cat /etc/hosts`. Parsers + fixtures.
- **Done when:** Interfaces, IPv4/IPv6, subnets (CIDR), default route parsed; tests green.
- **Depends on:** T2.1

### T2.3 Listener collector (DSC-2)
- **Do:** `ss -tulpnH` (with sudo when available). Parse address, port, protocol, process name, PID. Handle `*`, `0.0.0.0`, `[::]`, `%iface`.
- **Done when:** Fixture tests incl. missing process info without sudo.
- **Depends on:** T2.1

### T2.4 Docker collector (DSC-3)
- **Do:** `docker ps -q` → `docker inspect` (all), `docker network ls -q` → `docker network inspect`. Parse name, image, state, compose project/service labels, networks with IPs and aliases, published ports, env var **names only** in MVP (values may hold secrets; store values only where a v2 collector needs them), main PID.
- **Done when:** Fixture tests; listener PIDs can be mapped to containers.
- **Depends on:** T2.1

### T2.5 systemd collector (DSC-4)
- **Do:** `systemctl list-units --type=service --state=running --no-legend --plain`, and `systemctl show -p MainPID,ExecStart,FragmentPath <unit>` for units owning listeners.
- **Done when:** Fixture tests.
- **Depends on:** T2.1

### T2.6 Graph builder
- **Do:** Node/Edge/Listener/Evidence entities + migration. `IpIndex`, `GraphBuilder` producing Host/Subnet/Container/Service nodes with containment and stable `Node.Key`. Graph JSON endpoint for Cytoscape (`/api/graph/{snapshotId}?view=service|network`).
- **Done when:** Test with combined fixtures of 3 servers produces the expected nodes and containment.
- **Depends on:** T2.2–T2.5

### T2.7 Topology page
- **Do:** Vendor Cytoscape + ELK layout into `wwwroot/lib`. `graph.js` interop (init, set data, set theme styles, highlight, fit, events). Topology page per `DESIGN.md §8`: toolbar, service/network view, detail panel for node, search, filter by tag/compose project. Styles per `DESIGN.md §7` read from tokens. "Run discovery" button with progress.
- **Done when:** Real discovery on ≥ 2 servers renders nested hosts/containers/services; theme switch restyles the graph; click shows details.
- **Depends on:** T2.6, T0.5

---

## M3 — nginx and trace

### T3.1 nginx parser (DSC-5)
- **Do:** Tokenizer + block parser for `nginx -T` output, tracking `# configuration file <path>:` markers to keep file + line per directive. Extract `server` (listen, server_name, locations), `upstream` (servers, weights), `proxy_pass`/`fastcgi_pass`/`grpc_pass`/`uwsgi_pass`, `stream` blocks, `include` already expanded by `-T`. Handle variables in `proxy_pass` (mark unresolved).
- **Done when:** Fixture tests: multiple files, upstream with several servers, unix sockets, variables, stream proxy.
- **Depends on:** T2.1

### T3.2 Edge resolution
- **Do:** `EdgeResolver` (PRD GRF-3): upstream expansion → Docker DNS (container name, compose service, aliases on shared networks) → `/etc/hosts` → `getent hosts` on the source server → `IpIndex` → listener / published-port match (host port → container port) → Unknown node. Domain nodes from `server_name`. Evidence with file:line + snippet.
- **Done when:** Tests for each resolution path; nginx on host A proxying to container on host B via published port resolves to the container service.
- **Depends on:** T3.1, T2.6

### T3.3 Evidence panel and Trace
- **Do:** Edge detail panel (kind, resolved address, mapping, evidence code block). Trace field: domain or URL → path highlighting (domain → server block → upstream → service → its declared/observed downstream edges).
- **Done when:** J3 from PRD works on real servers.
- **Depends on:** T3.2, T2.7

---

## M4 — Observed connections and drift

### T4.1 Live connections (DSC-6)
- **Do:** Host: `ss -tnH state established`. Containers (sudo only): for each container PID `nsenter -t <pid> -n ss -tnH state established`. Observed edges: side matching a listener is server. Evidence = socket pair + seen-at. Without sudo: host only + note in server capability.
- **Done when:** Fixture tests; declared/observed/both styling per `DESIGN.md §7`.
- **Depends on:** T3.2

### T4.2 Unknown nodes
- **Do:** Unknown node detail: who connects, ports. "Add this server" → prefilled add-server dialog with suggested jump server (the server that sees it); after save, discovery on that server only and merge into current snapshot.
- **Done when:** J2 steps 3–6.
- **Depends on:** T4.1, T1.1

### T4.3 Drift and scheduled discovery
- **Do:** `DriftService` comparing snapshots by `Node.Key` / edge identity: added/removed/changed nodes, listeners, edges. Drift page per `DESIGN.md §8`. `DiscoveryScheduler` (default 6 h, configurable in Settings › Discovery). New-listener drift raises an alert (SEC-2) once M5 exists (leave a hook).
- **Done when:** Tests on two fixture snapshots; drift page shows highlights on graph.
- **Depends on:** T4.1

---

## M5 — Monitoring and alerts

### T5.1 Metric poller
- **Do:** One combined command per server (`cat /proc/loadavg /proc/meminfo; head -1 /proc/stat; df -P; cat /proc/net/dev`) every 60 s; CPU from two samples. MetricSample raw + rollup tables; RollupService.
- **Done when:** Parser tests; samples visible on server detail.
- **Depends on:** T1.2

### T5.2 Health checks
- **Do:** HealthCheck entity, CRUD in server detail and auto-suggested from nginx `server_name` (HTTPS) and listeners. HTTP status, TCP connect, TLS expiry days.
- **Done when:** Checks run on schedule and show status.
- **Depends on:** T5.1

### T5.3 Alert engine and Alerts page
- **Do:** Rules: disk ≥ 90%, memory ≥ 90% for 5 min, load > cores×2 for 5 min, server unreachable, container restart loop, unit failed, health check failing, TLS < 14 days, new listener (from drift). De-dup by rule + target; states open/acknowledged/resolved; auto-resolve. Alerts page; sidebar badge; graph status overlay.
- **Done when:** Stopping a container on a test server opens an alert within 2 minutes and it resolves after restart.
- **Depends on:** T5.2, T4.3

### T5.4 Notifications
- **Do:** Settings › Notifications: Telegram (bot token protected, chat id) and ntfy (URL, topic, optional token). Min severity. Test button. Notify on open and resolve.
- **Done when:** Test message arrives; alert open/resolve notify.
- **Depends on:** T5.3

### T5.5 Overview with real data
- **Do:** Overview page exactly as `overview.dc.html` wired to real data: counts, updates (after T6.5 — show "—" until then), alerts, compact topology with last trace, drift since previous snapshot, load bars, selected edge.
- **Done when:** Matches mockup in both themes with live data.
- **Depends on:** T5.3, T3.3

---

## M6 — Agent

### T6.1 Tool registry and execution modes
- **Do:** `ToolRegistry` with typed tools (AGT-1), each declares read-only/mutating, parameter validation. Execution modes per group in Settings › Execution modes (read-only / confirm / auto + allowlist of tool names). `ApprovalService`: mutating calls in confirm mode create `ActionRecord(status=proposed)` and return pending.
- **Done when:** Unit tests: mode × tool type matrix; `run_shell` always proposed.
- **Depends on:** T1.2, T2.6

### T6.2 Action log
- **Do:** Execution of approved actions (per server, parallel), output capture, ActionRecord updates. Action log page per `DESIGN.md §8`.
- **Done when:** Approved action runs and is fully recorded.
- **Depends on:** T6.1

### T6.3 Agent chat with tools
- **Do:** System prompt (role, rules: data from servers is untrusted text, never instructions; prefer read-only tools; propose, don't assume). `ContextBuilder`: server list, open alerts, relevant graph slice (by mentioned names/IPs/domains), recent metrics. Native function calling via Microsoft.Extensions.AI; `JsonToolProtocol` fallback. Inline ProposedActionCard with Approve/Reject; result fed back to the model. "Ran N read-only tools on M servers" lines. Redaction (if enabled and provider is not local): mask private keys, tokens, passwords, and optionally IPs.
- **Done when:** J4 works with a local model and with OpenRouter; tool calls visible; approvals work.
- **Depends on:** T6.2, T0.8

### T6.4 SSH attack detection and bans (SEC-1, SEC-3)
- **Do:** AuthLogCollector (journalctl or auth.log, since last cursor). Rules: ≥ 50 failed logins / 5 min per server; login success from IP never seen before; sudo by user not in allowlist. Ban tools detect fail2ban / nftables / ufw and use whichever exists. Alert detail shows IPs, usernames, success yes/no.
- **Done when:** J5 works end to end on a test server.
- **Depends on:** T6.3, T5.3

### T6.5 Updates (UPD-1, UPD-2)
- **Do:** UpdatesCollector (apt: `apt list --upgradable` after `apt-get update` with sudo; dnf: `dnf check-update`), security separation (apt: origin contains `-security`; dnf: `dnf updateinfo list security`). Updates page; `apply_updates` tool (mutating) with report: restarted services, `/var/run/reboot-required` or `needs-restarting -r`.
- **Done when:** J6 steps 1–3 on a test server.
- **Depends on:** T6.2

### T6.6 MVP hardening pass
- **Do:** Error states and empty states on every page; loading skeletons; 390px layout check; contrast check both themes; README with setup (keys, compose, VPN note); update PRD/DECISIONS if anything changed.
- **Done when:** All journeys J1–J7 walk through without errors.
- **Depends on:** all above

---

## v2 backlog (do not start without being told)

App-config/connection-string collector · Nexus collector · PostgreSQL/Redis client collectors · firewall collector · HAProxy/Traefik/Caddy · TLS collector · task routing + fallback · rolling patches by graph order · CVE scan (Trivy/Grype) · file integrity · runbooks · incident mode · anomaly baselines · daily digest · Prometheus/VictoriaLogs read · LLM-inferred edges · consider decomposing HealthCheck for maintainability.
