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
- **Do:** Rules: disk ≥ 90%, memory ≥ 90% for 5 min, load > cores×2 for 5 min, server unreachable (`server-unreachable`), SSH authentication failed (`ssh-auth-failed`, D-039), container restart loop, unit failed, health check failing, TLS < 14 days, new listener (from drift). De-dup by rule + target; states open/acknowledged/resolved; auto-resolve. Alerts page; sidebar badge; graph status overlay.
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

## M7 — Post-MVP review findings

Raised by the 2026-11-15 MVP review. Small issues were fixed directly (`fix(review): …` commits); these are the larger ones. Ordered by severity. They fix gaps against existing PRD/PLAN requirements; they are not new features.

### T7.R1 SSH connection lifecycle (INV-5, NFR-2) — high
- **Do:** Per-host backoff: single-flight connect per server plus a short negative cache (30–60 s) for unreachable hosts, and a dead jump host short-circuits every server behind it instead of each timing out. Skip a server's remaining collectors after a connection-level failure; per-call deadline that includes connect time, so the metric (60 s) and alert (30 s) cycles finish within their interval. Lease/ref-count pooled clients so a client (or a jump tunnel) is never disposed while another caller is mid-command. Already done in review: keepalive 30 s, recycle after 3 consecutive command timeouts (`SshTimeoutTracker`), eviction only when connection fields change; cover that pool wiring with fake-transport tests.
- **Evidence (2026-11-15 live run):** with one TEST-NET entry, discovery took 3 min 33 s (Partial 3/1) and the 60 s metric cycle overran once (105 s).
- **Done when:** Unit tests with a fake transport for leases, single-flight and negative cache. On the disposable lab with one TEST-NET (192.0.2.1) inventory entry behind `srv-edge`: discovery of the other servers is not delayed by more than one connect timeout, and the 60 s metric cycle does not overrun.
- **Depends on:** —

### T7.R2 Stale nodes for unreachable servers (NFR-3) — high
- **Do:** When a server's core collectors fail, carry its nodes/edges/listeners forward from the last usable snapshot, marked `stale` with last-seen time. Topology renders them dimmed with a text badge (not colour-only). Drift ignores stale carry-overs. Until then, Partial snapshots still feed drift/alerts and can raise false new-listener alerts for servers that failed in the earlier run — compare only servers that succeeded in both snapshots.
- **Evidence (2026-11-15 live run):** the unreachable TEST-NET host appears as an ordinary Host node with no stale marker.
- **Done when:** Tests on fixture snapshots; on the lab, a server made unreachable keeps its last-known nodes visibly stale and no spurious drift/new-listener alerts.
- **Depends on:** —

### T7.R3 Container restart-loop alert rule (T5.3, MON-2) — high
- **Do:** Read container restart counts (`RestartCount`, state) read-only, keep per-container history, open an alert when restarts within a window exceed a threshold; auto-resolve when stable.
- **Done when:** Tests; on the lab, `worker-pg`/`worker-redis` on `srv-app` (observed restarting, RestartCount ≈ 70 during the review) raise a restart-loop alert.
- **Depends on:** —

### T7.R4 Agent action guardrails (AGT-3/4) — medium
- **Do:** `restart_service` refuses `sshd`/`ssh`/`docker` unless explicitly confirmed. Record in DECISIONS whether `restart_container` (added in D-024, not in AGT-1) stays. Done in review: Auto mode removed (D-026), `block_ip` guard (D-030).
- **Done when:** Policy matrix tests cover the new rules.
- **Depends on:** —

### T7.R5 Secret handling gaps (LLM-7, MON-6) — medium
- **Do:** Require HTTPS (or a private/loopback host) for endpoints that receive tokens or API keys. Redact secret-looking arguments in Docker `Cmd`/`Entrypoint`/labels and credentials in raw `nginx -T` output before raw collector output is stored. Done in review: ntfy topic protected and masked, notifications minimised (D-028).
- **Done when:** Tests; existing stored ntfy settings migrate without loss.
- **Depends on:** —

### T7.R6 nginx relative includes (T3.1, DSC-5) — medium
- **Do:** Resolve relative `include` patterns against the nginx prefix (`/etc/nginx`) before matching `# configuration file` markers; included files' root-level `server`/`upstream` blocks belong to the including context.
- **Done when:** Fixture with `include conf.d/*.conf;` parses all server blocks.
- **Depends on:** —

### T7.R7 Monitoring throughput — medium
- **Do:** Alert engine checks servers with bounded parallelism (already 8 at a time, T7.R1) and skips the auth-log scan when the status probe fails (delivered by T7.R17, D-037). Notifications go through a background channel so a down Telegram/ntfy endpoint doesn't stall evaluation. Push metric time predicates into SQL (decide the `DateTimeOffset` storage/converter approach in DECISIONS; migration if needed). De-duplicate sudo events seen in both journald and auth.log. The auth-log cursor uses the app clock while `--since` runs on the server clock and advances even when the journal query fails silently; fix both so events aren't skipped.
- **Done when:** Tests; on the lab with one unreachable entry, alert evaluation for the others stays within its 30 s cycle.
- **Depends on:** T7.R1

### T7.R8 Discovery run ownership — medium
- **Do:** One app-wide single-flight gate for discovery (all circuits + scheduler). UI-triggered runs execute in a host-owned scope, not the Blazor circuit, so closing the tab can't break the run.
- **Done when:** Tests for the gate; closing the browser mid-run leaves a Completed/Partial snapshot.
- **Depends on:** —

### T7.R9 systemd collector scope (T2.5, DSC-4) — medium
- **Do:** Map listener PIDs to units (e.g. `/proc/<pid>/cgroup`, read-only) and run `systemctl show` only for listener-owning units, with a fallback when `/proc` is restricted. Fix `SystemdCollectorTests` to assert the restriction.
- **Done when:** Fixture tests; live check on a real LXC/VM with systemd (not a privileged container on the dev host).
- **Depends on:** —

### T7.R10 Remaining MVP gaps — medium
- **Do:** Loading skeletons (T6.6). J6 step 3: re-run the server's health checks after `apply_updates`. Sidebar "Groups" card always says "No groups yet" (stub). Agent chat shows Markdown replies as raw text. dnf scans with sudo, verified on a dnf host. Subnet filter on Topology (GRF-10). SSH-config import group select offers groups in use (like the edit form).
- **Done when:** Each item verified in both themes / on a test server as applicable.
- **Depends on:** —

### T7.R11 Manual UI re-verification — medium
- **Do:** In a real browser (keyboard input), verify flows that the review's browser automation could not drive: T1.1 add/edit + validation (duplicate name, port 70000, missing key), T5.2 add a health check, T3.3 trace `api.test.local` with evidence. Check the review-changed UI: required server group, Settings corrupt-execution-modes notice, provider "re-enter API key" message, approver name, graph alert text marker, Unknown node labels. 390 px and both themes, contrast (NFR-10).
- **Done when:** Each flow passes or has a bug filed as a new T7.R task.
- **Depends on:** —

### T7.R14 Sudoers-pinnable privileged commands — medium
- **Do:** Run the app's privileged commands as direct argv (for example `sudo -n /usr/bin/ss -tulpnH`) instead of `sudo -n -- sh -c '<script>'`, splitting multi-step scripts into separate fixed commands or a root-owned helper script with typed arguments, so a sudoers entry can allow exactly those commands. Keep `AppSudoCommands` (D-031) in sync and update README › Sudo on managed servers with a ready-to-use sudoers file.
- **Done when:** A sudoers file listing only the app's commands works on the lab for discovery, scans and approved tools; the app still alerts on any other sudo.
- **Depends on:** —

### T7.R15 SSH client leases (T7.R1 gap, INV-5) — high
- **Do:** The lease/ref-count requirement of T7.R1 was not delivered. A pooled client (and a jump tunnel) must not be disposed while another caller is mid-command: count in-flight commands per `ConnectionEntry`; `Invalidate`/`Evict`/timeout recycling mark the entry retired and dispose it only when the count reaches zero (new callers get a fresh entry meanwhile). Recycle after consecutive timeouts only when no other command is in flight.
- **Evidence (2026-11-15 review, code):** `SshConnectionPool.cs` `Invalidate`/`RemoveConnection`/`Evict` dispose the transport immediately; `SshTimeoutTracker` is shared by every session on the entry, so three timeouts of short probes (metrics 30 s, status 20 s, auth scan 20 s) can close the client under a running `apt upgrade` (10 min) and kill its channel.
- **Done when:** Fake-transport tests: Evict, host edit and the timeout threshold during an in-flight command leave that command running and dispose the client afterwards; a jump tunnel outlives dependents' commands; no leak on cancellation.
- **Delivered:** Design in D-036; tests in `SshConnectionLeaseTests`; live results in the PROGRESS session log.
- **Depends on:** —

### T7.R16 Stale carry-forward correctness (T7.R2 gaps, NFR-3) — medium
- **Do:** (a) Fresh-to-stale edges are carried from the old snapshot without checking that the fresh server still reports them (a removed `proxy_pass` to a stale server is hidden until recovery), and a fresh server's edge to a stale server's other addresses (bare host has no `Addresses`) can resolve to a new Unknown node (added drift during the outage, removed on recovery): seed the IP index with the stale hosts' last-known addresses, then carry only stale-to-stale edges. (b) "Last seen" uses the previous snapshot's start time, which is wrong after a merge snapshot and is the run start, not the collection time: use the host collector run time of the snapshot where the server was last reachable. (c) New-listener alerts compare only servers whose listener collector succeeded in both snapshots and that are not stale in either (T7.R2 Do text); a server never discovered before must not raise one alert per listener on recovery. (d) Mark carried edges stale in the graph (dashed + text in the detail panel), check `opacity` of stale labels against NFR-10, pass the C# badge text to `graph.js` instead of parsing the date in JS, make `IGraphRepository.GetEntitiesAsync` non-default, correct D-033 consequences (observed connections become `unknown:observed:` nodes, not "removed").
- **Done when:** Tests on fixture snapshots for each point (multi-address stale host, merge-base last-seen, failed listener collector, recovery from "never discovered"); on the lab with a server that has two addresses, an outage and a recovery produce no added/removed Unknown nodes and no new-listener alerts.
- **Depends on:** —

### T7.R17 Cycle deadlines and alert stalls (T7.R1 gaps, NFR-2) — medium
- **Do:** A host that accepts connections but whose commands hang is never backed off and holds a slot: count consecutive command timeouts toward backoff (distinct from connect failures) or skip it for a cycle. Skip the auth-log scan when the status probe failed or timed out; a per-cycle deadline for the 30 s alert and 60 s metric cycles; the per-call deadline must include connect time (`SshRunner` times only the command); the container status query runs under its own limiter or with a short timeout so it cannot add 20 s to a hung host's slot; report a failed probe as "probe failed", not "could not reach the server".
- **Shutdown:** SIGTERM while a connect to the blackholed host is in flight does not stop the app promptly. Measured 2026-11-15 on the lab with a 192.0.2.1 entry behind `srv-edge` and Information logging: exit after 30.1 s (the host's default shutdown timeout) versus 5.9 s idle. The log shows a second `SSH connect attempt to blackhole` starting ~11 s after "Hosting stopping", at the moment the first attempt timed out, so some SSH caller's token is not cancelled when the host stops (the hosted services stop one after another, so a still-running service's cycle keeps its connect alive until its own turn), and the idle 5.9 s also needs explaining (tunnel/client disposal). Find which caller keeps connecting, make connects and tunnel disposal cancellation-aware, and make `SshConnectionPool.Dispose` not wait on in-flight attempts. Found in the T7.R15 live run: after `SshConnectionPool.Dispose` SSH.NET does not fail a command already running on a disposed client; the caller waits until the command's own timeout (120 s in the run), so in-flight commands must be cancelled by the host's stopping token, not by disposal.
- **Done when:** Tests with a fake transport whose commands hang: the cycle ends within its interval and the host is skipped after N timeouts; on the lab (`docker pause dva-srv-app`) the metric and alert cycles for the other servers stay within 60 s and 30 s; SIGTERM during a blackhole connect exits in under 8 s (owner, 2026-11-15; was 5 s) and logs no connect attempt after "Hosting stopping".
- **Delivered:** D-037; tests in `CommandTimeoutBackoffTests`, `MonitoringCycleDeadlineTests`, `SshShutdownTests`; live results in the PROGRESS session log. Done ahead of T7.R7 on the owner's order: only the R17 items (command-timeout backoff, auth scan skip, call and cycle deadlines, shutdown, `stop_grace_period`); alert-engine parallelism, the notification queue, metric SQL predicates and the auth-log cursor stay with T7.R7.
- **Depends on:** T7.R7 (overlapping alert-engine scope; do together)

### T7.R18 Restart-loop rule follow-ups (T7.R3, MON-2) — medium
- **Do:** (a) An unreachable server must not resolve an open restart-loop alert after 10 minutes on stale data: carry the open condition while the server is unreachable. (b) `docker inspect` failing for one container (removed between `ps` and `inspect`) drops the whole sample and triggers a needless sudo retry: tolerate partial output. (c) The status probe in `AlertSignalSource` ends in `|| true`, so a Docker permission failure looks like "no containers"; give it the same sudo fallback as the metric query. (d) Store only containers with a restart count above 0 or a non-running state, log (without contents) when stored history is corrupt (D-025), remove history of deleted servers, and verify parallel settings writes with a SQLite busy timeout. (e) Decide with the owner whether `created`, `paused` and `removing` should raise the High container-state alert; document that a recreate loop and `docker restart` are invisible to the rule.
- **Done when:** Tests for (a)–(d); owner decision recorded for (e).
- **Delivered (owner's scope, D-038):** (a) held as-of-last-sample when a server has no fresh sample, loop carried across outages until 10 quiet fresh minutes; (b) partial `inspect` tolerated, no sudo retry; (c) status probe reports Docker permission failures (`docker-status-unavailable`, Medium) with the sudo fallback and `AppSudoCommands` entry, no Docker / stopped daemon unchanged (the `|| true` stays on `systemctl`); (d) history older than 24 h pruned on read and write, corrupt history logged and treated as empty; (e) owner decision: `paused` Low, `created`/`removing` Medium only after 10 minutes in the state, `exited`/`dead` unchanged, recreate loops and manual `docker restart` documented as invisible. Tests in `RestartLoopFollowUpTests`.
- **Not done (not in the owner's list):** (d) "store only containers with a restart count above 0 or a non-running state", "remove history of deleted servers", and the SQLite busy-timeout check for parallel settings writes.
- **Depends on:** —

### T7.R19 SSH failure classification and reachability UI (T7.R1 gaps) — low
- **Do:** Only connection-level failures (`SshConnectionException`, `SocketException`, timeouts, host key mismatch) enter backoff; a missing key file, wrong passphrase or authentication failure is reported as such and does not hide a server for 15 minutes; Test connection shows the real cause (`LastFailure`) instead of only "unreachable since …". The first failure of a host is single-flight (callers queue for one connect timeout today). A cancelled caller must not clear a bypass caller's in-flight flag (claim token). Pool disposal at shutdown must not throw `ObjectDisposedException` or leak a late connection. Check the target's own backoff before connecting its jump host. Servers shows "Not tested" instead of "Reachable" for a server that was never contacted.
- **Done when:** Tests for each point with the fake transport.
- **Delivered (owner's scope, D-039):** login and key errors are `HostFailureKind.AuthFailed` ("auth failed", constant 1 minute retry, safe wording, alert title "SSH authentication failed", Servers badge "Auth failed"); Test connection shows the real cause; the first connect of a host is single-flight (`ConnectFlight`); pool shutdown audited (idempotent `Dispose`, a lock-order inversion fixed). Tests in `SshAuthFailureTests`, `SshAuthFailureConsumerTests`, `SshKeyLoadTests`, `SshSingleFlightTests`, `SshPoolShutdownRaceTests`.
- **Not done (not in the owner's list):** the claim token for a cancelled caller clearing a bypass caller's in-flight flag, checking the target's own backoff before connecting its jump host, the Servers page "Not tested" versus "Reachable" label. **These three moved to the v2 backlog on the owner's order (2026-11-15).**
- **Added afterwards (owner orders, D-039 amendments):** automatic retries of a host stop after three consecutive failed logins (paused until Retry now, Test connection or an edit of the connection settings), and a failed login is alerted under its own rule `ssh-auth-failed` instead of `server-unreachable`.
- **Depends on:** —

### T7.R20 Alerts must not resolve from stale data (T7.R17/R18 gaps, MON-2) — medium
- **Do:** An open alert resolves when its condition is missing from the evaluation, so every rule that is computed from a live probe resolves when the probe cannot run. Measured 2026-11-15 on the lab: `docker stop web` in srv-app opened a High `container-state` (exited) alert; `docker pause dva-srv-app` made the host unreachable and the alert resolved 30 s later (D-038 documents this for exited/dead); a `paused` alert from the container history stayed open for the whole 5.5 min outage, as designed. Hold, instead of resolving: `container-state` (exited/dead), `docker-status-unavailable`, `unit-failed`, the disk/memory/load thresholds (they read stored samples and need no probe, move them out of the probe-success branch) and the auth-scan failure paths of `ScanAsync` (they age the saved events by the wall clock; `GetCachedAsync` was fixed in review-r2). The container history: a latest sample older than 24 h or a corrupt value empties it and resolves the held loop/state alerts, a probe that exits 0 without its marker is read as "no containers", and a counter rise across a sample gap (app or server down for hours) opens a loop alert for restarts that were not seen to happen within 10 minutes. `EvaluateStates` ignores staleness, so a stale last sample can open a new `paused`/`created` alert. Decide with the owner which rules hold and for how long (a host that is gone for good must not hold alerts forever; an "unreachable" alert already covers it).
- **Done when:** Tests: an open alert of each rule stays open while its server is unreachable, auth-failed, in command-timeout backoff, probe-truncated or sample-less, and resolves on the first fresh sample that shows it gone; D-038 amended; live on the lab with `docker pause dva-srv-app`.
- **Depends on:** —

### T7.R21 Deadline and timeout semantics (T7.R17 gaps, NFR-2) — medium
- **Do:** (a) A command cut short by the call deadline (connect used most of it) counts as a command timeout toward the hung-host backoff (4 in a row) and the client recycle (3), so a slow-to-connect but healthy host can be backed off: count only commands that ran their full timeout. (b) A connect cut by a short caller deadline records a failed connect and backs the host off (Test connection and capability probes use 10 s against a 15 s connect timeout; a dependent's deadline can back off its jump host): record a connect failure from a deadline only when the deadline was at least the connect timeout. (c) Short timeouts of metric and probe commands count toward the same four as a legitimate 10-minute command (`apt upgrade`), so a busy host is backed off while an action runs: do not escalate while a long command is leased. (d) A timeout of a command that started before an edit counts against the new settings (no generation check), and one that started before a backoff escalates it after its window and clears a claimed retry. (e) A host whose commands time out reads "unreachable ... commands timed out" in the Servers badge and alert, although it answers on the network: give it its own wording. (f) Hosts that never got a slot in a cycle, and a probe that answered but whose sudo retry was cut by the cycle deadline, are reported as failed ("did not finish within the 25 s alert cycle deadline") and open a High alert: report "not tried this cycle" without an alert. (g) The alert cycle's deadline covers only the SSH phase: notifications (15 s per endpoint, sequential) and database writes are unbounded. (h) `AbandonedAttemptGrace` (5 s) extends a call past its deadline. (i) Retries every minute can slip to 90 s when a login takes longer than the 1 s slack (measured 89.7 s for one attempt after a restart).
- **Done when:** Tests with `HangLab` for each point; on the lab with the jump host paused (`docker pause dva-srv-edge`) every alert cycle ends within 25 s plus the database phase and every metric cycle within 50 s.
- **Depends on:** —

### T7.R22 Shutdown and lease hardening (T7.R15/R17 gaps, INV-5) — low
- **Do:** An approved action interrupted by SIGTERM is stored as Failed ("The application is shutting down"), and the updates scan turns it into a per-server error string, although D-037 says shutdown is never a failure: rethrow `SshShutdownException` (the action stays Running and the startup sweep marks it interrupted). Shutdown disposes pooled clients one after another: ~1 s per client with a frozen peer (2.1 s measured with three), so many frozen hosts could outlast the 40 s compose grace; dispose on pool threads with a bound. A command racing a plain `Dispose` can still surface `ObjectDisposedException`. A caller that missed a just-published client takes the flight lock, starts a second flight and retires the fresh client (one extra login): re-check `TryReuse` inside `_flightSync`. SIGTERM during discovery leaves its snapshot Running until the next start marks it Failed (T7.R8 owns the run). `HostOptions.ShutdownTimeout` is implicit (30 s) next to `stop_grace_period: 40s`. Dead code (`ConnectionEntry.Retain`); D-036 wording ("closed by its caller's own cancellation") and D-039's list of items not done are stale.
- **Done when:** Tests for the action and updates paths, parallel disposal and the reuse re-check; SIGTERM with three frozen clients exits in under 1 s.
- **Depends on:** —

### T7.R23 Auth-failed follow-ups (T7.R19 gaps, D-039) — low
- **Do:** The Topology stale badge says "stale · never discovered" for an auth-failed server without the cause; Test connection's key hint ("check the identity key file") is shown for a login failure on the jump host; a Data Protection decrypt failure of a stored passphrase is reported as "wrong passphrase or invalid key file" and counts toward the pause without contacting the server; a connect failure of another kind in between lifts the pause (three more logins per network flap; owner decision whether that is wanted); a failure recorded for old settings can slip in between the generation check and `RecordFailure`, and a flight can start with settings that were edited a moment before (re-check the generation under the lock).
- **Done when:** Tests per point; owner decision recorded for the flap policy.
- **Depends on:** —

### T7.R24 Optional sudo password per server (owner order, 2026-11-15) — medium
- **Do:** For hosts where NOPASSWD sudo is not possible: an optional "sudo password" per server in the server form, stored with Data Protection, never returned to the browser, masked. When set, sudo runs as `sudo -S -p '' …` with the password written to stdin; it is never on the command line, in logs, action output, alerts, notifications or anything sent to the LLM, and it is on the server's redaction list. A rejected password is a distinct "sudo auth failed" state: after one failure no sudo is used on that server until the password is edited or "Retry now" is pressed (pam_faillock); non-sudo collectors keep running. Without a password `sudo -n` is used exactly as before. The Servers page shows the mode per server ("sudo: NOPASSWD", "sudo: password", "sudo: none"). (Filed by the owner as "T7.R20"; that number was already taken by the stale-alerts task, so it is T7.R24.)
- **Done when:** Unit tests: the password never appears in any captured command, log or output; one failure stops further attempts; reset paths. Live on the lab: a second sudo user on srv-db that needs a password, discovery works with it, a wrong password causes exactly one failed attempt. DECISIONS entry: NOPASSWD restricted sudoers stays the recommended setup.
- **Depends on:** —

### T7.R12 Test gaps — low
- **Do:** Tests for `ChatService.SendAsync` (streaming + tool loop), discovery max degree 8, unix-socket edge resolution, dnf update parsing/reboot report, the no-sudo capability path (fake returning non-zero for `sudo -n true`).
- **Done when:** Tests green.
- **Depends on:** —

### T7.R13 Owner decisions — done
- Resolved 2026-11-15: read-only default (D-029), no server writes in read-only groups (D-027), Auto disabled (D-026), minimal notifications (D-028); `/dev/components` is Development-only and login is rate-limited (review fix).

---

## v2 backlog (do not start without being told)

App-config/connection-string collector · Nexus collector · PostgreSQL/Redis client collectors · firewall collector · HAProxy/Traefik/Caddy · TLS collector · task routing + fallback · rolling patches by graph order · CVE scan (Trivy/Grype) · file integrity · runbooks · incident mode · anomaly baselines · daily digest · Prometheus/VictoriaLogs read · LLM-inferred edges · consider decomposing HealthCheck for maintainability · execution mode "auto within allowlist" (removed from MVP, D-026; needs command pinning and per-tool scopes).

Deferred from T7.R19 (moved here on the owner's order, 2026-11-15; context in D-039):
- SSH claim-token fix: a cancelled caller must not clear the in-flight flag of a "Retry now" (bypass) caller that claimed the attempt.
- SSH jump order: check the target server's own backoff before connecting its jump host, so a server in backoff does not cost a jump-host connect.
- Servers page: show "Not tested" instead of "Reachable" for a server that was never contacted.
