# Progress

**This file is the handoff.** Update it at the start of a task, at every checkpoint, and at the end. A new session (another account, another tool, another model) continues from here with no other context.

---

## Current

- **Task:** T0.3 Persistence
- **Status:** in-progress
- **Last updated:** 2026-11-13 by implementation session

## WIP notes

<!-- Fill while a task is in progress. Clear when it is done.
What is done so far:
-
What is next (exact next step):
-
Files touched:
-
Gotchas found:
-
-->

What is done so far:
- T0.1 solution skeleton is committed and verified.
- T0.3 `AppDbContext`, `AppSetting`, SQLite configuration, startup migration application, persisted Data Protection keys, and `ISecretProtector` are implemented.
- Initial EF migration is generated; local `dotnet-ef` is pinned to match EF Core 10.0.14.
- Added a Data Protection secret round-trip test.
What is next (exact next step):
- Run build and tests, then launch the app with a temporary local data path and confirm the SQLite database and key-ring files are created.
Files touched:
- `Directory.Packages.props`, `docs/DECISIONS.md`, `.config/dotnet-tools.json`, `src/DevOpsAgent.Domain/Settings/AppSetting.cs`, `src/DevOpsAgent.Application/Abstractions/ISecretProtector.cs`, `src/DevOpsAgent.Infrastructure/**`, `src/DevOpsAgent.Web/**`, `tests/DevOpsAgent.Tests/SecretProtectorTests.cs`.
Gotchas found:
- The globally installed EF tool was 10.0.13; a repository-local 10.0.14 tool removes the runtime/tool mismatch.

## Blockers

_(none)_

---

## Task status

Statuses: `todo` · `in-progress` · `done` · `blocked`

| Task | Title | Status | Commit |
|---|---|---|---|
| T0.1 | Solution skeleton | done | `feat(T0.1): solution skeleton` |
| T0.3 | Persistence | in-progress | |
| T0.2 | Docker and compose | todo | |
| T0.3 | Persistence | todo | |
| T0.4 | Single-user login | todo | |
| T0.5 | Design system and app shell | todo | |
| T0.6 | LLM providers — domain, storage, client | todo | |
| T0.7 | Settings › LLM connection page | todo | |
| T0.8 | Basic chat (no tools) | todo | |
| T1.1 | Server inventory | todo | |
| T1.2 | SSH pool and runner | todo | |
| T1.3 | Import ssh config | todo | |
| T2.1 | Collector framework and snapshots | todo | |
| T2.2 | Host collector | todo | |
| T2.3 | Listener collector | todo | |
| T2.4 | Docker collector | todo | |
| T2.5 | systemd collector | todo | |
| T2.6 | Graph builder | todo | |
| T2.7 | Topology page | todo | |
| T3.1 | nginx parser | todo | |
| T3.2 | Edge resolution | todo | |
| T3.3 | Evidence panel and Trace | todo | |
| T4.1 | Live connections | todo | |
| T4.2 | Unknown nodes | todo | |
| T4.3 | Drift and scheduled discovery | todo | |
| T5.1 | Metric poller | todo | |
| T5.2 | Health checks | todo | |
| T5.3 | Alert engine and Alerts page | todo | |
| T5.4 | Notifications | todo | |
| T5.5 | Overview with real data | todo | |
| T6.1 | Tool registry and execution modes | todo | |
| T6.2 | Action log | todo | |
| T6.3 | Agent chat with tools | todo | |
| T6.4 | SSH attack detection and bans | todo | |
| T6.5 | Updates | todo | |
| T6.6 | MVP hardening pass | todo | |

Recommended order inside M0: T0.1 → T0.3 → T0.5 → T0.2 → T0.4 → T0.6 → T0.7 → T0.8.

---

## Session log

One line per session: date · tool/model · tasks touched · result.

- 2026-11-13 · planning · docs created · ready for T0.1
- 2026-11-13 · Codex · T0.1 · solution skeleton complete; build/test pass and default page returns HTTP 200
- 2026-11-13 · Codex · T0.3 · started
