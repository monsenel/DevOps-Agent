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
- **Do:** Rules: disk ≥ 90%, memory ≥ 90% for 5 min, load > cores×2 for 5 min, server unreachable (`server-unreachable`), SSH authentication failed (`ssh-auth-failed`, D-039), sudo password rejected (`sudo-auth-failed`, Medium, D-040), container restart loop, unit failed, health check failing, TLS < 14 days, new listener (from drift). De-dup by rule + target; states open/acknowledged/resolved; auto-resolve. Alerts page; sidebar badge; graph status overlay.
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
- **Delivered:** D-040; `SudoPasswordGate` + `SshRunner` (stdin via `CreateInputStream`, one password sudo per server for a 15 s authentication window, persisted rejection, redaction), `ISshRunner.ProbeSudoAsync`, `Server.CanUseSudo`, migration `AddServerSudoPassword`, Servers page field/label/Retry now; tests in `SudoPasswordTests`; live results in the PROGRESS session log. Added afterwards (owner orders, D-040 amendments): a rejected sudo password is never retried by Retry now or Test connection, and it opens a Medium `sudo-auth-failed` alert.
- **Depends on:** —

### T7.R25 A sudo password attempt with an unknown outcome is tried again (T7.R24 gap, D-040) — high — done (D-040 amendment 4)
- **Do:** When a password sudo times out, is cancelled or loses its connection after the password was written, its result is abandoned (`SshConnectionPool` session `ExecuteAsync`), `SudoPasswordGate.IsPasswordRejected` never sees sudo's answer, the turn is released at once and the next sudo on that server sends the same password again. Measured 2026-11-16 on the lab (srv-db, user `dvapw`, isolated DB, build before review-r3): one wrong password, `docker pause dva-srv-db` 0.6 s after sudo logged `pam_unix(sudo:auth): authentication failure` (inside PAM's failure delay), unpaused 35 s later: a second `authentication failure` 0.1 s after the unpause, only then was the rejection recorded. Two attempts for one wrong password are four pam_faillock failures with sudo's default `passwd_tries=3`. Triggers: the alert cycle's 25 s deadline and its cancellation, a call deadline mostly used up waiting for the turn or connecting (the command can start with less than PAM's 2-3 s delay left), a frozen host or a dropped connection, SIGTERM during sudo (nothing is persisted, so the restarted app tries again). Options, owner decision: (a) an outcome that is unknown within `AuthenticationWindow` of the send pauses the password like a rejection (persisted, own wording "outcome unknown"), at the cost of pausing a correct password after a network blip; (b) the same, but only while the password has never worked since it was entered (persisted "verified" flag; after a success an unknown outcome is not a pause); (c) mitigations for both: do not start a password sudo with less than a few seconds of deadline left, and after an unknown outcome keep the turn until the window ends. Recommended: (b) + (c). Same task: rejection detection matches three exact stderr lines and fails open on anything else (check the pam_faillock "account is locked" output on the lab with `passwd_tries=1` and with the default); when sudo does not read stdin (a NOPASSWD rule for that command, `timestamp_type=global`) the password line stays on the command's stdin (`apt-get install -y` in `apply_updates` can read it; `</dev/null` inside the command would change the logged command and the D-031 exemption).
- **Done when:** Tests for a timeout, a cancellation and a dropped connection after the password was written (one attempt, then paused or held per the decision); decision recorded in D-040; the live repro above shows one `authentication failure`.
- **Depends on:** —

### T7.R26 Sudo password redaction is per server (T7.R24 gap, D-040) — medium — done (D-040 amendment 5)
- **Do:** A server's output is redacted only with that server's own password. Measured 2026-11-16: srv-db registered twice (`dvapw` with the password, `dva` without), one failed SSH login with the password typed as the user name: the `dvapw` entry's auth-scan state holds `[REDACTED]`, the `dva` entry's (`AppSettings` `security.auth-log.<id>`) holds it in plain text (SQLite WAL); an agent tool such as `tail_log /var/log/auth.log` on any other server would send it to the model. Redact every stored sudo password (cached by protected value) from every server's results. Also: a one-character password replaces that character in all output (refuse very short passwords or skip redaction below a length, owner decision); the Servers page does not refresh the sudo column when a rejection happens in the background (it reads "sudo: password" until the next reload; its buttons stay safe); `ServerRepository.GetAsync` reloads a tracked entity and would drop unsaved edits of a caller (no caller does that today).
- **Done when:** Tests: the password of server A is redacted in results and stored scan state of server B; the live measurement repeated shows no plain text in the database.
- **Depends on:** —

### T7.R27 Sudo password follow-ups from review-r4 (D-040 amendments 4–5) — medium
- **Do:** Found by the 2026-11-16 review of T7.R25/R26 (none reproduced live; each is traced in code). (1) The transport writes the password after `SshCommand.ExecuteAsync` returns; the pool abandons a call at its deadline and the caller then records "unknown", but nothing stops a transport task that is still blocked in the (synchronous) channel open from writing afterwards. It holds today only because `Stream.WriteAsync` refuses a cancelled token (`SshTransport.cs`, `SshConnectionPool.cs`); add a per-attempt armed/sent/abandoned state so that is explicit. (2) Only three stderr lines count as a rejection: pam_faillock's lockout wording ("account validation failure, is your account locked?") is "unknown", and Retry now then sends the password again; owner decision: any non-zero exit without the marker, from a command that ran to its end, is a rejection (not lifted by Retry now). (3) A rejection whose save fails is read as "unknown" after a restart, so Retry now resends it; retry the save until it succeeds before the turn is released. (4) A NOPASSWD rule or a cached ticket prints the marker without the password being read: the password counts as confirmed, the alert resolves and the label says "sudo: password" for a password that may be wrong; say "sudo ran, password not checked". (5) edit_config's base hash is taken over redacted text, so a file that holds a stored password can never be edited ("changed"); hash on the server. (6) Overlapping secrets leak a fragment (`abcdefgh` and `fghijk` in `abcdefghijk`); exception text in `AgentToolDispatcher` and `ActionExecutionService` is not passed through redaction; the runner's gate dependency is optional; nothing scrubs alerts, action log, chat and auth-log state written before a password existed.
- **Done when:** A test per item (a transport that writes after the timeout, a lockout line, a failing save then a restart, NOPASSWD with a wrong password, a file holding the password, overlapping passwords).
- **Depends on:** owner decision on (2) and (4)

### T7.R28 edit_config hardening from review-r4 (D-043) — medium
- **Do:** (1) A path matched by `ConfigEdit:AllowedPaths` but by no `Services` rule is written with no validation and no reload and reported healthy (`/etc/**` + `sshd_config`): refuse it, or require a validation command. (2) A health check that could not run (no `curl`, no graph yet, no domain in the file) counts as healthy: say "unverified" and decide whether that rolls back. (3) The root step does not check the staged content's hash or that it is not a link: pass the new hash in. (4) A lost connection or missing exit status after the `mv` leaves an unvalidated file and the staging directory is removed under the running step: add a verify step that compares the live file with the new hash and the backup, roll back when validation fails, report "unknown, check" not "failed". (5) Temporary file and backup sit beside the file: a killed write leaves `x.dva-XXXXXX`, and with `include …/sites-enabled/*` they are loaded, so `nginx -t` fails for every later reload; use dot-prefixed temporary names and decide where backups go. (6) `UnifiedDiff` converts CRLF to LF and shows an empty diff for a final-newline change; a file that is not valid UTF-8 cannot round-trip (it fails closed on the hash). Delivered in review-r4 (not part of this task): link refusal, restore reported only when the copy worked, rollback when the health check cannot be completed, narrower sudo-alert exemption.
- **Done when:** Tests for each case in a real shell where possible; the lab edit of D-043 repeated.
- **Depends on:** —

### T7.R29 known_hosts follow-ups from review-r4 (D-041) — medium
- **Do:** (1) `@revoked` and `@cert-authority` lines of the owner's file are skipped: a revoked key is accepted and the host may count as unlisted; refuse revoked keys. (2) When the owner's `Ssh:KnownHostsFile` is configured but the file is missing or unreadable (a missing mount), every host it pinned becomes trusted on first use: log it and refuse first use then. (3) `WriteManaged` does not flush to disk before the rename; after a power loss the file can be empty. (4) "Accept new key" asks for the confirmation in the page only; the service checks the fingerprint but not a one-time token from the dialog. (5) After Accept new key the other inventory entries of the same host keep their Critical alert until their backoff ends; clear them with the accepted host. (6) A target behind a jump host whose key changed shows "unreachable", not the jump host's mismatch (T7.R23). (7) A host that offers a second key type is a mismatch; accepting replaces the entry, so the two types flip-flop: keep one entry per type.
- **Done when:** Tests for each; owner decision on (7).
- **Depends on:** —

### T7.R30 Chat attachments, redaction and impact accuracy from review-r4 (D-042, D-044) — medium
- **Do:** (1) Images cannot be redacted and are sent to cloud providers even with "Redact secrets before sending" on (D-042 says so, the picker does not): owner decision (refuse images while redaction is on, or confirm per message). (2) `ChatContentRedactor` misses `dbpass=…`, bare provider tokens (`AKIA…`, `ghp_…`, `xox…`, JWT), `Basic <base64>`, `--password x`, a PEM without its END line, and values over 4096 characters; `DB_PASSWORD=a b c` keeps `b c`. (3) Every earlier attachment is replayed on every turn and loaded eagerly: cap the total per conversation or replay only the current turn. (4) A text file starting `GIF8` is taken as an image (prefix only); `GetMultipleFiles` / `OpenReadStream` failures in `AgentChat.razor` are not caught; the picked files are dropped when the service refuses them. (5) Impact: dependents and domains are not checked for staleness; `block_ip` / `unblock_ip` ignore the address (an inventoried server's own address is not flagged); package rules: `openssh-server` on Debian is unit `ssh`, `nodejs` matches a `node` process, `postgresql-client-*` matches `postgresql`, `moby-containerd`/`moby-runc` and `mariadb-server` are missing, compose services are treated as host services; the Action log shows the current graph's impact for an old action without saying so; an observed inbound connection to an unregistered local listener is stored as the server depending on the client. (6) Global search: carried-forward stale nodes are listed as current, there is no query length cap, the previous debounce token is not disposed, and every keystroke loads all edges and evidence.
- **Done when:** A test per item.
- **Depends on:** owner decision on (1)

### T7.R12 Test gaps — low
- **Do:** Tests for `ChatService.SendAsync` (streaming + tool loop), discovery max degree 8, unix-socket edge resolution, dnf update parsing/reboot report, the no-sudo capability path (fake returning non-zero for `sudo -n true`).
- **Done when:** Tests green.
- **Depends on:** —

### T7.R13 Owner decisions — done
- Resolved 2026-11-15: read-only default (D-029), no server writes in read-only groups (D-027), Auto disabled (D-026), minimal notifications (D-028); `/dev/components` is Development-only and login is rate-limited (review fix).

---

## M8 — Owner feedback round (v0.2.0)

Owner order 2026-11-16: T8.1–T8.9 in order, one commit each, AGENTS.md §2, DESIGN.md for all UI; build 0 warnings, all tests green, tag `v0.2.0` locally (no push). Lab and isolated `/tmp` DB only.

### T8.1 Bugs seen in screenshots
- **Do:** Settings › LLM connection: the Providers list and the Connection form always show the same data (seen: an empty list while the form showed a provider "Ollama" of type OpenRouter with an empty Base URL); with no providers, an empty state and an empty "Add provider" form. Updates: on read-only groups, selection checkboxes and Apply are hidden or disabled with the reason shown ("read-only group"). Updates header: separator between the scan time and "Package lists last refreshed".
- **Done when:** Tests for the list/form state and the read-only rule; checked in both themes.
- **Depends on:** —

### T8.2 Global search (top bar)
- **Do:** Searches servers (name, host, IP), nodes (containers, services, domains), listeners (port) and open alerts. Results dropdown grouped by type, keyboard navigable, Enter opens the item (server detail, Topology focused on the node, alert detail). Works from every page. Debounced, server-side, at most 20 results.
- **Done when:** Search service tests (matching, grouping, limit 20); keyboard navigation and Enter verified in a browser.
- **Depends on:** —

### T8.3 Theme toggle as an icon button
- **Do:** Sun/moon inline SVG, aria-label "Switch to dark theme" / "Switch to light theme", 44 px target, tooltip, no text label.
- **Done when:** Both states checked in a browser, keyboard focus ring visible.
- **Depends on:** —

### T8.4 Automatic known_hosts (trust on first use)
- **Do:** On the first successful connection to a server (jump targets included) record its host key in the app-managed known_hosts file; show the fingerprint and "first seen" on the server page. A changed key still raises the Critical "host key changed" alert and blocks connections; "Accept new key" on the server page replaces the stored key after showing old and new fingerprints and a confirmation. Keys added by hand are still honoured. DECISIONS entry on the TOFU trade-off.
- **Done when:** Tests (first use recorded, mismatch still refused, accept replaces, manual keys kept); live on the lab with a fresh known_hosts.
- **Depends on:** —

### T8.5 Chat: attachments and effort
- **Do:** Attach files to a message (text, logs, configs, JSON, YAML; images only if the model supports them), 2 MB per file, 5 files per message; text attachments go through the same redaction as tool output before being stored or sent to a cloud provider; chips on the message. Effort selector (Low / Medium / High) per conversation, default Medium, mapped to the provider's reasoning-effort parameter where supported (exact field from OpenRouter's and Ollama's current docs), hidden when the model does not support it. Assistant messages rendered as Markdown (code blocks, lists, tables; noted in T7.R10).
- **Done when:** Tests for limits, redaction, effort mapping and Markdown rendering (no raw HTML).
- **Depends on:** —

### T8.6 Updates: select all and sources
- **Do:** "Select all" per server table (and per group) and "Select security only". Above each server's table its package sources: apt (`/etc/apt/sources.list`, `/etc/apt/sources.list.d/*`, deb822 `.sources`): URI, suite, components, signed-by; dnf (`dnf repolist -v`): id, name, baseurl/metalink. Read-only commands only. The Source column shows the repository origin (apt origin/label from `apt-cache policy` or the list file name) so Docker's repo and Ubuntu's "jammy" are distinguishable.
- **Done when:** Parser tests with fixtures (one-line, deb822, dnf); live on the lab.
- **Depends on:** T8.1

### T8.7 Impact preview on every proposed action
- **Do:** Every approval card (restart_service, restart_container, apply_updates, block_ip, run_shell, edit_config) shows "Impact" from the latest graph: services and servers depending on the target (inbound declared and observed edges, 2 levels) and domains routed to it through nginx. Stale target or graph older than 24 h is said in the card. The system prompt tells the agent to call query_graph and mention impact before proposing a mutating action.
- **Done when:** Impact service tests on a fixture graph; card checked in a browser.
- **Depends on:** —

### T8.8 edit_config tool
- **Do:** Mutating, always needs approval, never on read-only groups. Input: server, path, and full new content or a unified diff; the card shows the diff. Execution: back up (`path.bak-<timestamp>`), write atomically, run the validation command if known (nginx: `nginx -t`; others configurable, else none), reload the service, health check (related health checks, else the service's listener). On any failure restore the backup, reload again and report what failed. Allowed paths: configurable allowlist (default `/etc/nginx/**`); the sensitive-path deny list still applies.
- **Done when:** Tests (allowlist, deny list, read-only refusal, diff apply, rollback on each failing step); live on the lab: wrong upstream port for api.test.local on srv-edge → `nginx -t` passes, health check fails → automatic rollback; then a valid change succeeds.
- **Depends on:** T8.7

### T8.9 Dependency awareness for updates
- **Do:** The apply_updates approval card lists the services on that server the package changes touch (docker-ce → all containers; nginx → its domains), from the graph and package names; warns when a package restarts Docker or nginx.
- **Done when:** Tests on a fixture graph; card checked in a browser.
- **Depends on:** T8.7

### T8.10 Agent tool-call limit — done (D-045)
- **Do:** The limit today is `MaxToolRounds = 5` model rounds of up to 8 calls each (`ChatService`), and hitting it ends the turn with a fixed sentence. Replace it with a limit on **tool calls per message**, configurable in Settings › LLM connection (default 15, max 50). The chat effort may lower it (Low 8, Medium 15, High 30), never above the setting. When it is reached the agent makes one final model call with tools disabled, asking for what it found, what is still unknown and what it would do next; "Tool limit reached (N/N)" is shown above that summary, with a **Continue** button below it that resumes the task with a fresh budget. Loop guard: the same tool with identical arguments requested 3 times in one turn stops the turn early; the summary explains it and it is not a normal limit (no Continue). The tool calls of each turn are listed in a collapsible "Ran N tools on M servers".
- **Done when:** Unit tests for the limit (setting, effort, cap), the summary call (tools disabled), Continue and the loop guard; checked in a browser in both themes.
- **Depends on:** T8.5

### T8.11 Servers map view (owner request, 2026-11-16) — done (D-046)
- **Do:** A third Topology view, **Servers**, shown by default, drawn like the owner's reference picture: each server is one card with a coloured border by role (reverse proxy, application, data, Git/CI), a header with a role icon and the server name, a caption above it ("role • IP • OS"), and its services and containers as a stacked list of rows inside. Domains are grouped in one dashed "Internet / Clients" card. Connections are drawn between cards, one dashed arrow per pair of servers labelled with all its ports ("5432, 6379"); selecting it lists the underlying service connections and their evidence. Selection, search, trace, filters, alerts and stale markers keep working; Service and Network views are unchanged.
- **Done when:** Checked on the lab in a browser in both themes; build 0 warnings, tests green.
- **Depends on:** T2.x graph, T8.2


### T8.12 One box per unknown machine in the Servers view (owner request, 2026-11-16) — done (D-047)
- **Do:** In the Servers view, all Unknown endpoints with the same address are one dashed card (a VM not in the inventory, usually with several containers/services): caption "Not in inventory • <address>", header "Unknown host" with the address, one row per port (":8080", or "no port"); duplicates of the same address and port from different observers are one row. Edges to it are merged per server pair like other cards. Selecting the card offers "Add this server" and lists all its connections; selecting a row shows that endpoint as today.
- **Done when:** Checked in a browser on a graph with several ports per unknown address; build 0 warnings, tests green.
- **Depends on:** T8.11

### T8.13 Names and ports on the Servers view cards (owner request, 2026-11-16) — done (D-048)
- **Do:** Rows of a server card show the ports the service or container answers on next to its name ("api · :8080", at most three, then "+N"), from its listeners, published host ports and the connections it receives. The card caption adds the machine's real hostname (from host discovery) when it differs from the inventory name. An Unknown host card whose address belongs to a server in the inventory is named after that server: merged into its card when the server is on the map, otherwise titled with the server name and captioned "In inventory, not discovered yet • <address>".
- **Done when:** GraphBuilder test for the hostname attribute; checked in a browser on the lab and on a copy of the owner's graph; build 0 warnings, tests green.
- **Depends on:** T8.12

### T8.14 nginx `set` variables in proxy targets (owner bug, 2026-11-16) — done (D-049)
- **Do:** `proxy_pass $var;` (and the other proxy directives) whose variable is assigned with `set $var <value>;` in the enclosing location, server or http block resolve to that value, so the edge goes to the real backend (owner: `set $map_app_upstream http://192.168.20.14:3030;` left Kartio-nginx unconnected to the map server). Nearest scope wins; a variable set to different values in the same scope, set inside `if`, or built from other variables stays unresolved as today. The evidence names the `set` line.
- **Done when:** Parser tests with fixtures (resolved, nested scope, conflicting values, unknown variable); the owner's map links Kartio-nginx to map after a discovery.
- **Depends on:** T2.x nginx collector

### T8.15 BusyBox hosts (Alpine): addresses and listeners (owner bug, 2026-11-16) — done (D-050)
- **Do:** The host collector falls back to the text output of `ip addr`/`ip route` when `ip -j` is not supported (BusyBox), and the listener collector falls back to `netstat -tulpn` when `ss` is missing; both read-only, parsed deterministically, with fixtures of real BusyBox output.
- **Done when:** Parser tests on BusyBox fixtures; an Alpine server gets its addresses and its nginx row on the map.
- **Depends on:** T8.14

### T8.16 External clients are not a hop on the Servers view (owner report, 2026-11-16) — done (D-051)
- **Do:** An Unknown address that only connects *to* inventory servers (observed inbound connections; the unknown node is the source of every edge) is an external client, not a backend: its card is titled "External client" (caption "Not in inventory • <address>"), shows ONE row "client · N connections" instead of one row per ephemeral source port, and is placed in the first layer (left) like Internet / Clients, so it no longer sits between two servers and looks like a hop. Unknown addresses that are connected to (nginx upstreams, observed outbound connections) keep their port rows. A mixed address keeps its target ports and gets the extra client row.
- **Done when:** Checked in a browser on a copy of the owner's graph (93.118.144.130: SSH to both servers, 443, 21116); build 0 warnings, tests green.
- **Depends on:** T8.12

### T8.17 The agent's own SSH session is not a connection on the map (owner report, 2026-11-16)
- **Do:** The connection collector reads `$SSH_CLIENT` of its own SSH session (read-only) and leaves out the established connections that are that session: remote address = the client address, local port = the SSH port. They no longer become Unknown/External client nodes with a port-22 edge (owner: the external client "between nginx and the destination" with SSH 22 to the map server is wrong; the map is only reached through nginx). A note on the host says "SSH sessions of DevOps Agent from <address> are not drawn". Everything else, including other clients' SSH, stays.
- **Done when:** Collector/parser tests; on the lab the Unknown 10.88.10.1 → srv-edge :22 card is gone after a discovery.
- **Depends on:** T8.16
---

## v2 backlog (do not start without being told)

App-config/connection-string collector · Nexus collector · PostgreSQL/Redis client collectors · firewall collector · HAProxy/Traefik/Caddy · TLS collector · task routing + fallback · rolling patches by graph order · CVE scan (Trivy/Grype) · file integrity · runbooks · incident mode · anomaly baselines · daily digest · Prometheus/VictoriaLogs read · LLM-inferred edges · consider decomposing HealthCheck for maintainability · execution mode "auto within allowlist" (removed from MVP, D-026; needs command pinning and per-tool scopes).

Deferred from T7.R19 (moved here on the owner's order, 2026-11-15; context in D-039):
- SSH claim-token fix: a cancelled caller must not clear the in-flight flag of a "Retry now" (bypass) caller that claimed the attempt.
- SSH jump order: check the target server's own backoff before connecting its jump host, so a server in backoff does not cost a jump-host connect.
- Servers page: show "Not tested" instead of "Reachable" for a server that was never contacted.
