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
