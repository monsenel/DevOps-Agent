# AGENTS.md — DevOps Agent

You are building **DevOps Agent**: a self-hosted, Dockerized web app that connects to the owner's Linux servers over SSH, discovers how they are wired together (nginx → backends → databases, across private networks), shows that as an interactive graph, monitors the servers, and performs DevOps/security work through an LLM (local Ollama, LM Studio, OpenRouter, or any OpenAI-compatible endpoint).

Single user. Personal tool. Not a SaaS.

---

## 1. Read order (every session, no exceptions)

1. `AGENTS.md` (this file)
2. `docs/PROGRESS.md` — current task, status of every task, WIP notes, blockers
3. `docs/PLAN.md` — the definition and acceptance criteria of the current task
4. Only the docs the current task needs:
   - `docs/PRD.md` — product requirements and user journeys
   - `docs/ARCHITECTURE.md` — solution layout, boundaries, data model
   - `docs/DESIGN.md` — UI design system (mandatory for any UI work)
   - `docs/DECISIONS.md` — decisions already made; do not re-litigate them
5. `git log --oneline -20` and `git status`

**The repository is the memory.** Assume you have no knowledge of any previous session. Anything not written in these files or in git does not exist.

---

## 2. Work loop

Work on **one task at a time**, in the order given in `docs/PLAN.md`.

1. **Start**: set the task to `in-progress` in `docs/PROGRESS.md`, write today's date in the session log, commit (`chore(progress): start T<id>`).
2. **Build**: implement only what the task defines. Small, compiling steps.
3. **Checkpoint**: on long tasks, every meaningful step, update the `WIP notes` section in `PROGRESS.md` (what is done, what is next, files touched) and commit with `wip(T<id>): <what>`. A session can end at any moment (rate limit, account switch). The next session must be able to continue from your WIP notes alone.
4. **Verify**: `dotnet build` with zero warnings-as-errors failures, `dotnet test` green, and every acceptance criterion in `PLAN.md` checked. For UI tasks also check against `docs/DESIGN.md` (both themes).
5. **Finish**: mark the task `done` in `PROGRESS.md`, clear `WIP notes`, add one line to the session log, commit `feat(T<id>): <summary>`.
6. Continue with the next `todo` task unless told to stop.

**Never** start a new task with uncommitted changes. **Never** mark a task `done` if build or tests fail.

If blocked (missing information, an external dependency, an ambiguous requirement):
- Write the blocker in `PROGRESS.md › Blockers` with what you tried.
- If a reasonable default exists, take it, record it in `docs/DECISIONS.md`, and continue.
- Otherwise move to the next task that does not depend on the blocked one.

---

## 3. Commands

```bash
dotnet build DevOpsAgent.slnx
dotnet test DevOpsAgent.slnx
dotnet run --project src/DevOpsAgent.Web
docker compose up --build            # app on http://localhost:8080
docker compose --profile ollama up   # app + local Ollama
dotnet ef migrations add <Name> -p src/DevOpsAgent.Infrastructure -s src/DevOpsAgent.Web
```

If the solution format or a command differs once the skeleton exists, update this section in the same commit.

---

## 4. Tech stack (fixed — see DECISIONS.md)

- .NET 10, C# 14, ASP.NET Core, **Blazor Web App with Interactive Server render mode**
- EF Core + **SQLite** (file on the `/data` volume)
- **SSH.NET** for SSH (managed, no `ssh` binary needed)
- **Microsoft.Extensions.AI** (`IChatClient`) with an OpenAI-compatible client and configurable base URL
- **Cytoscape.js** (+ cytoscape-elk or cytoscape-dagre) for the graph, via JS interop, vendored into `wwwroot/lib`
- Background work: `BackgroundService` / hosted services. No Hangfire, no Redis.
- Tests: xUnit, FluentAssertions-free (use xUnit `Assert`), collector parsers tested against fixture files
- Container: multi-stage Dockerfile, `mcr.microsoft.com/dotnet/aspnet:10.0` runtime

**Before using any version-specific API** (Microsoft.Extensions.AI, SSH.NET, EF Core 10, Blazor .NET 10 render modes), check the current official docs or the package's README. Do not rely on memory. If an API differs from what these docs describe, follow the real API and note it in `DECISIONS.md`.

---

## 5. Code conventions

- Nullable enabled, `TreatWarningsAsErrors` on, file-scoped namespaces, implicit usings.
- Clean Architecture: `Domain` has no dependencies; `Application` depends on `Domain`; `Infrastructure` implements `Application` interfaces; `Web` composes everything. See `ARCHITECTURE.md`.
- Async all the way, `CancellationToken` on every async public method.
- Options pattern for configuration; no magic strings for config keys.
- `ILogger<T>` with structured logging; never log secrets (API keys, private keys, passwords).
- Collectors are **deterministic parsers**. Never use the LLM to parse nginx, `ss`, Docker, or any config that has a collector.
- Every parser gets unit tests with real-looking captured output under `tests/DevOpsAgent.Tests/Fixtures/`.
- Remote commands sent by collectors must be **read-only**.
- No new NuGet/npm dependency without a line in `DECISIONS.md`.

---

## 6. UI rules (summary — full spec in `docs/DESIGN.md`)

- Bento layout: rounded cards (24px radius) on a soft page background, 20px gaps.
- Light and dark themes from day one. **Colors only through CSS custom properties** (`var(--dva-*)`). No hex values in components.
- Fonts: Plus Jakarta Sans (UI), JetBrains Mono (IPs, ports, paths, code). Self-hosted in `wwwroot/fonts` (the app may run without internet).
- One accent (green). One emphasised tile per row at most.
- Touch targets ≥ 44px, visible focus ring, real `<button>`/`<a>`/`<label>` elements.
- UI language: English, LTR.
- Reference mockups: `docs/design/mockups/*.dc.html` (templated HTML; `{{token}}` placeholders map to the token table in `DESIGN.md`).

---

## 7. Do not

- Add features, pages or settings that are not in `PLAN.md`.
- Change a decision in `DECISIONS.md` without writing a new entry that supersedes it.
- Delete or weaken a test to make it pass.
- Upgrade packages or the target framework as a side effect.
- Commit secrets, real server IPs, or private keys. Use `.env` (git-ignored) and `.env.example`.
- Run destructive commands on remote servers from tests. Tests never open real SSH connections.
- Never stop, restart, or modify anything on servers tagged `prod`, and never propose using those servers for tests.
- Use disposable containers for alert tests that need a runtime environment.
