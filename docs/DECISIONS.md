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
