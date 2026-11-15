# Decisions

Append-only. To change a decision, add a new entry that supersedes the old one; do not edit old entries.

Format: `D-<number> · <date> · <title>` then Decision / Why / Consequences.

---

### D-001 · 2026-11-13 · Web app in Docker, not a desktop app
- **Decision:** Self-hosted web app shipped as one Docker image, run with docker compose.
- **Why:** Monitoring must run 24/7; no cross-platform desktop shell to maintain; runs inside the private network so fewer jump hosts.
- **Consequences:** Needs a login. Must not be exposed to the internet (VPN/WireGuard only).

### D-002 · 2026-11-13 · Blazor Web App, Interactive Server render mode
- **Why:** Single user, same network; live updates over the circuit; no separate API client layer.

### D-003 · 2026-11-13 · SQLite with EF Core
- **Why:** Single-user tool; no DB server to run; one file on `/data`.

### D-004 · 2026-11-13 · SSH.NET, agentless
- **Why:** Managed, no `ssh` binary needed in the image; port forwarding covers jump hosts. Nothing installed on managed servers.

### D-005 · 2026-11-13 · Microsoft.Extensions.AI with an OpenAI-compatible client
- **Why:** One abstraction for Ollama, LM Studio, OpenRouter and custom endpoints; built-in function invocation. JSON-in-prompt fallback for models without native tool calling.

### D-006 · 2026-11-13 · Deterministic parsers for all known configs
- **Why:** The graph must be exact and reproducible. The LLM only proposes `inferred` edges for configs without a collector (v2).

### D-007 · 2026-11-13 · Cytoscape.js for the graph, vendored
- **Why:** Compound (nested) nodes, ELK/dagre layouts, handles 500+ nodes. Vendored into `wwwroot/lib` so the app works offline.

### D-008 · 2026-11-13 · Bento design, light + dark, English LTR
- **Why:** Owner's chosen visual direction (see `DESIGN.md`). Personal tool, English UI.

### D-009 · 2026-11-13 · No Hangfire/Redis
- **Why:** Hosted services are enough for a single-process app.

### D-010 · 2026-11-13 · Secrets encrypted with ASP.NET Data Protection
- **Why:** API keys and key passphrases at rest in SQLite; key ring on `/data/keys`.

### D-011 · 2026-11-13 · Central package management for test dependencies
- **Decision:** Pin the initial test tooling centrally in `Directory.Packages.props`: Microsoft.NET.Test.Sdk 17.14.1, xunit 2.9.3, xunit.runner.visualstudio 3.1.4, and coverlet.collector 6.0.4.
- **Why:** T0.1 requires centrally managed package versions, and the .NET 10 xUnit template supplied these compatible versions.
- **Consequences:** New NuGet dependencies require a corresponding decision entry and package version in the central file.

### D-012 · 2026-11-13 · Map public NuGet packages to nuget.org
- **Decision:** Use package source mapping in the repository's `NuGet.Config` to resolve packages from nuget.org.
- **Why:** The development environment has multiple globally configured feeds; NuGet central package management raises NU1507 unless source mapping is configured.
- **Consequences:** Package restore remains independent of machine-wide feed configuration; private feeds must be explicitly mapped if future packages require them.

### D-013 · 2026-11-13 · EF Core 10 SQLite persistence
- **Decision:** Use Microsoft.EntityFrameworkCore.Sqlite, Microsoft.EntityFrameworkCore.Design, and the repository-local dotnet-ef tool at version 10.0.14 for persistence and migrations.
- **Why:** The official SQLite provider supports EF Core 10, and 10.0.14 is the latest stable EF Core 10 servicing release available for this session.
- **Consequences:** Keep EF Core packages on the same 10.0 servicing version and apply committed migrations at application startup.

### D-014 · 2026-11-13 · Self-host licensed UI fonts
- **Decision:** Vendor Plus Jakarta Sans and JetBrains Mono WOFF2 assets from Fontsource 5.3.0 with their SIL Open Font License 1.1 texts.
- **Why:** The interface must work offline and the approved design specifies these fonts.
- **Consequences:** Keep the font assets and license files in `src/DevOpsAgent.Web/wwwroot/fonts`; do not load fonts from a CDN at runtime.

### D-015 · 2026-11-13 · Match the container UID to bind-mounted data
- **Decision:** Run the app image as a non-root user and let Compose set its numeric UID/GID through `DVA_UID` and `DVA_GID` for the host-mounted data directory.
- **Why:** SQLite and Data Protection need write access to `./data`, whose ownership follows the local host user when Compose creates the bind path.
- **Consequences:** Set these values to the host user's numeric UID/GID when they differ from the `.env.example` defaults.

### D-016 · 2026-11-13 · Initialize bind-mount ownership before dropping privileges
- **Decision:** The Compose entrypoint starts with root only long enough to create and assign `/data` and `/data/keys` to `DVA_UID:DVA_GID`, then uses `setpriv` to run the web process as that non-root identity.
- **Why:** Compose creates fresh bind-mount directories as root on the target host, so a non-root process cannot initialize the required SQLite and Data Protection files by itself.
- **Consequences:** The application process remains non-root; only the small entrypoint initialization runs with elevated privileges.

### D-017 · 2026-11-13 · OpenAI-compatible chat adapter and injectable HTTP transport
- **Decision:** Use `Microsoft.Extensions.AI.Abstractions` and `Microsoft.Extensions.AI.OpenAI` 11.0.0 with the OpenAI .NET SDK. Create a chat client with `OpenAIClientOptions.Endpoint` from the saved provider base URL. Use the named `llm` `HttpClient` for both model listing and the SDK transport so connection tests can exercise the complete HTTP flow with a fake handler.
- **Why:** The current official adapter implements `IChatClient` for OpenAI-compatible endpoints; package 11.0.0 supports net10.0. Reusing the named transport keeps endpoint behavior and tests consistent.

### D-018 · 2026-11-14 · SSH.NET 2026 and pinned host keys
- **Decision:** Use `SSH.NET` 2026.0.0 for managed SSH, async command execution, and local forwarding. Verify every host key against exact host/port entries in `/keys/known_hosts`; reject unknown or changed keys.
- **Why:** This release targets .NET 8 and higher and documents cancellable async connect/command APIs. SSH.NET's host-key event defaults to trusting the received key, so the app must explicitly replace that behavior with pinned verification.
- **Consequences:** Mount `/keys/known_hosts` alongside the identity keys before connecting to inventory servers. Do not log passphrases, private key data, or host key material.

### D-019 · 2026-11-14 · Defer live SSH acceptance to keep discovery work moving
- **Decision:** Proceed with T2.1 using the implemented SSH abstractions and automated tests while deferring T1.2's live direct-host and jump-host checks.
- **Why:** No authorized test hosts, identity keys, or pinned `known_hosts` entries are available, and the owner directed that work continue.
- **Consequences:** Keep T1.2 marked blocked until both live paths are tested; do not treat automated tests as proof of live connectivity.

### D-020 · 2026-11-14 · Vendor Cytoscape graph runtime and ELK layout
- **Decision:** Vendor Cytoscape.js 3.35.0 (MIT), cytoscape-elk 2.4.0 (MIT), and elkjs 0.12.1 (EPL-2.0 OR GPL-3.0-or-later) under `src/DevOpsAgent.Web/wwwroot/lib`.
- **Why:** T2.7 requires an offline-capable compound graph with ELK layered layout; the versions were checked against the package manifests and adapter README.
- **Consequences:** Keep each package's license text beside its browser bundle. The app does not add a runtime npm install or package manifest.

### D-021 · 2026-11-14 · Fixed full-height desktop sidebar
- **Decision:** Supersede the desktop sidebar layout rule in `DESIGN.md` with a fixed, 248px-wide sidebar spanning the viewport height; keep it in normal flow and stacked above content below 800px.
- **Why:** The owner requested a fixed sidebar whose height matches the screen.
- **Consequences:** Offset desktop main content by the sidebar width. Allow the sidebar itself to scroll when its contents exceed the viewport.

### D-022 · 2026-11-14 · Keep a local Graphify architecture index
- **Decision:** Use Graphify's local knowledge graph to navigate first-party code relationships and review architecture changes. Exclude vendored browser libraries, `bin/`, `obj/`, and `*.min.js` through `.graphifyignore`. Keep generated `graphify-out/` output locally and ignore it in Git.
- **Why:** The graph helps trace first-party cross-file relationships, while third-party/minified bundles and generated build artifacts add opaque symbols and duplicate noise. Generated graph JSON/HTML are reproducible and should not grow repository history.
- **Consequences:** Rebuild the local graph after code changes when needed; do not commit `graphify-out/` artifacts.

### D-023 · 2026-11-14 · Reach host-published SSH targets from the app container
- **Decision:** Map `host.docker.internal` to Docker's host gateway for the app service.
- **Why:** On Linux, a disposable SSH lab may publish its port on the host loopback; the app container needs a stable route to that host port, and its own `127.0.0.1` cannot reach it.
- **Consequences:** Configure those inventory entries with `host.docker.internal` and the published port. A port bound only to Linux host loopback also needs a Docker-bridge-reachable bind or temporary bridge-scoped forward. Keep ordinary server routing and VPN requirements unchanged.


### D-024 · 2026-11-15 · Scope agent tools to intent and evidence
- **Decision:** Broad daily reviews use persisted inventory, alerts, latest metrics, recent graph, and snapshot drift without remote tool calls. Read-only questions expose only read-only tools; mutating tools are exposed only for matching requests, and arbitrary shell requires an explicit shell request. Disk diagnosis reports filesystem mounts and Docker usage separately and leaves unexplained usage unknown.
- **Why:** J4 acceptance showed that an OpenAI-compatible model can chain irrelevant tool calls, propose shell commands for read-only questions, and infer causes not supported by measured data.
- **Consequences:** Keep prompt-to-tool scoping covered by tests, preserve explicit approval for mutations, and do not infer disk causes from `df` alone.

### D-025 · 2026-11-15 · Logging abstractions in the Application layer
- **Decision:** Reference `Microsoft.Extensions.Logging.Abstractions` (10.0.14, same train as EF Core) from `DevOpsAgent.Application`.
- **Why:** Application services must log failures (corrupt settings, duplicate alert keys, partial tool failures) through `ILogger<T>` per the code conventions without depending on ASP.NET Core. The package is already part of the transitive graph of Infrastructure/Web, so nothing new ships.
- **Consequences:** Application services that log take `ILogger<T>` as a required constructor parameter (never optional, so logging cannot silently disappear); tests pass `NullLogger<T>.Instance`. Never log secrets or setting contents.

### D-026 · 2026-11-15 · Auto execution mode disabled for MVP
- **Decision:** Execution modes are `ReadOnly` and `Confirm` only. "Auto within allowlist" is removed from the Settings UI, from save/validation, and from every execution path (approval, agent dispatcher, Updates page); every mutating call needs explicit approval unless a read-only group blocks it. A stored Auto value (mode `2` in the `agent.execution-modes` setting) reads as `Confirm`, not as corrupt; stored allowlists are ignored. Genuinely unreadable, null or unknown values still fail closed to `ReadOnly`. Supersedes the auto-within-allowlist part of AGT-3 and T6.1.
- **Why:** Owner decision 2026-11-15. Server output is untrusted text and can carry prompt injection; letting a model-chosen tool call run without a human approving the exact command is an unacceptable risk for the MVP.
- **Consequences:** `ExecutionMode` has no Auto member and `ExecutionModePolicy` has no allowlist. Data written earlier keeps working (Auto becomes Confirm). "Auto within allowlist" moves to the v2 backlog and needs its own design (command pinning, per-tool scopes) before returning.


### D-027 · 2026-11-15 · Read-only groups never write on the server
- **Decision:** `ReadOnly` means no writes on the server at all, including refreshing package metadata. Update scans for servers in a read-only group (or when the execution-mode setting is unreadable and fails closed) run `apt list --upgradable` / `dnf -C check-update` / `dnf -C updateinfo list security` without sudo, and never `apt-get update`. The agent's read-only `list_updates` tool is cache-only on every group. Each scan records when the package lists were last refreshed (newest mtime under `/var/lib/apt/lists`, or the dnf solv cache), and the Updates page shows it, labelled "cached lists only" for read-only groups. Non-read-only groups keep refreshing lists with sudo during scans (T6.5).
- **Why:** Owner decision 2026-11-15. `apt-get update` rewrites the package cache; a read-only group must not be modified by the app.
- **Consequences:** Read-only scans can be stale; the last-refreshed time makes that visible. `UpdateScans` gains `CacheOnly` and `PackageListsRefreshedAtUtc` (migration `AddUpdateScanCacheInfo`, additive).

### D-028 · 2026-11-15 · Notification content minimised; ntfy topic is a secret
- **Decision:** Telegram and ntfy alert notifications carry only server name, rule, severity and state (`[High] srv-edge · ssh-unusual-sudo · opened`). Alert titles, targets and details never leave the app; they can hold IPs, user names, commands and sudo arguments. The ntfy topic is stored protected with Data Protection like the bot and ntfy tokens, is never returned to the browser (`HasNtfyTopic` only), and is entered in a masked field where blank keeps the stored value. Settings saved earlier with a plain-text topic are protected on first read without losing the topic.
- **Why:** Owner decision 2026-11-15. Notifications go to third-party services; anyone who knows an ntfy topic can read it.
- **Consequences:** Details are read in the app's Alerts page. Changing the topic requires re-entering it.

### D-029 · 2026-11-15 · Read-only is the default execution mode
- **Decision:** A server group without a stored execution mode, and every group while the stored setting is unreadable, is `ReadOnly`. Saving one group writes only that group's entry, so it can never change another group's mode. Supersedes the "confirm each change (default)" part of AGT-3.
- **Why:** Owner decision 2026-11-15. A newly added group, including `prod`, must not allow mutating actions until the owner opts in.
- **Consequences:** After upgrading, groups that never had a mode saved (previously Confirm) become read-only; switch a group to "Confirm each change" in Settings › Execution modes to allow approved actions there. Read-only groups also get cache-only update scans (D-027).

### D-030 · 2026-11-15 · block_ip refuses addresses that would lock out the app or the operator
- **Decision:** `block_ip` is denied when the address is loopback, unspecified, broadcast or multicast; one of the app host's own interface addresses; any inventory host (so every jump host), by literal IP or by name resolved from the app host; or a client address the signed-in operator has used to reach the app (recorded per authenticated request, last 32, in memory). The check runs when the action is proposed and again when it is approved.
- **Why:** Review finding (T7.R4): one mistaken or prompt-injected ban could cut the app off from a jump host and every server behind it, or lock the operator out.
- **Consequences:** Inventory names that only a jump host can resolve are not compared by name; their literal addresses still are. Operator addresses are forgotten on restart until the next request.

### D-031 · 2026-11-15 · Sudo alerts exempt only the app's own fixed command lines
- **Decision:** A sudo event by a server's inventory SSH user is exempt from the "sudo by non-allowlisted user" alert only when its command is the `sudo -n true` probe or exactly one of the command lines in `AppSudoCommands`. The list is generated from the same builders that send the commands, with sentinel values replaced by strictly typed holes (hex IDs, numbers, unit/package names, IP addresses, a quoted path). sudo's split `(command continued)` log lines are joined before matching. Supersedes the probe-only rule from b5ec62c.
- **Why:** Owner decision 2026-11-15: the probe-only rule made the app's routine `ss`, `nginx -T`, `nsenter`, scan and update commands raise the alert on every server, burying real signals.
- **Consequences:** Any other sudo by the app user still alerts. A test captures every sudo command the collectors, scanner and tools send and fails if one is missing from the list. Sudoers can pin only the fixed commands while everything runs through `sh -c` (T7.R14).

### D-032 · 2026-11-15 · Per-host SSH backoff and jump short-circuit
- **Decision:** `HostReachabilityTracker` (singleton `IHostReachability`, in memory) records connection-level failures per host: the host is skipped for 1 minute after a failure, doubling up to 15 minutes, and reset by the first success or by a settings change (`Evict`). Only one retry runs at a time; everyone else gets `HostUnreachableException` ("<server> is unreachable [via jump host X] (since …, next retry …)") at once. A server whose jump host is in backoff is unreachable without any connection attempt. `SshRunOptions.BypassBackoff` ("Test connection" and "Retry now", which is the same capability test) attempts the whole route. SSH.NET sits behind `ISshTransportFactory` so the pool's routing and backoff are tested with fakes. Alert evaluation, metrics and health checks run 8 servers in parallel; health checks of a server in backoff are recorded as not probed instead of waiting.
- **Why:** T7.R1 / owner scope 2026-11-15: one blackholed host or jump host made discovery take minutes and the metric cycle overrun.
- **Consequences:** Backoff state is lost on restart (the first cycle after a restart pays one connect timeout per dead host). A server's real SSH status can lag by up to its current backoff; "Retry now" checks immediately.

### D-033 · 2026-11-15 · Stale carry-forward for unreachable servers
- **Decision:** When a server is unreachable during discovery (`HostUnreachableException`), its nodes, listeners, edges and evidence are copied from the last usable snapshot into the new one and marked with node attributes `Stale: true` and `LastSeenUtc` (kept from the first stale copy, so repeated outages keep the real last-seen time). A server with no history keeps a bare host marked "never discovered". Drift compares node attributes without those two markers, so an outage or a recovery is not drift. The graph dims stale nodes and adds "stale · last seen <time>" to the label; the detail panel and the keyboard node list say the same in text. Docker's embedded DNS listener (127.0.0.11, random port per container start) is one listener identity per protocol for drift.
- **Why:** NFR-3 and T7.R2: an unreachable server's nodes vanished, producing "removed" drift and, on recovery, "added" drift and new-listener alerts.
- **Consequences:** Only unreachable servers are carried forward; a reachable server whose individual collector fails still loses that collector's nodes for the run. Observed connections that other, reachable servers make to a recovering server can still appear as removed until those clients reconnect.

### D-034 · 2026-11-15 · Container restart-loop rule
- **Decision:** Each metric cycle also runs a read-only container query (`docker ps -aq` + `docker inspect` ID, name, state, restart count; a marker when Docker is absent), without sudo, repeated with sudo only when Docker refuses and the server is sudo-capable (the command is on the `AppSudoCommands` list). The last 30 minutes of per-server container lists are kept in app settings. A container in state `restarting`, or whose restart count rose by 3 or more within 10 minutes, raises a medium `container-restart-loop` alert; it resolves 10 minutes after the last restart or when the container disappears. A recreated container (new ID, counter reset) is not counted as a restart. The High `container-state` alert no longer fires for `restarting` (it covers exited/dead containers).
- **Why:** T7.R3 / MON-2: restart loops were only visible as a generic "restarting" state alert.
- **Consequences:** One extra read-only SSH command per server per minute. History starts when the app starts, so restarts before that are not counted.
