# Progress

**This file is the handoff.** Update it at the start of a task, at every checkpoint, and at the end. A new session (another account, another tool, another model) continues from here with no other context.

---

## Current

- **Task:** T0.7 Settings › LLM connection page
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
- T0.6 provider domain, protected persistence, CRUD/default rules, chat client factory, model listing, and connection tests are committed and verified.
- The current task spec is the Settings › LLM connection page in `docs/PLAN.md`; the UI must match `settings-llm.dc.html` and LLM-1 through LLM-8.
- Added persisted test details (model count, streaming, tool calling, error summary) and model refresh using either an entered key or the selected provider's protected key.
- Implemented provider list and connection form, provider presets, field validation, refresh/test/save/remove actions, test result card, default selection, section placeholders, v2 routing/fallback cards, cloud redaction setting, and live sidebar model status.
- Verified saved credentials remain blank in the password field after reload; only a saved-key indicator is shown. Verified add/save/test/default flow against a local OpenAI-compatible fixture: endpoint reachable, one model listed, streaming and tool calling supported, sidebar changed to Connected.
- Verified light/dark theme switching and 390px layout without horizontal overflow. `dotnet build DevOpsAgent.slnx` passed with 0 warnings/errors, `dotnet test DevOpsAgent.slnx` passed (4/4), `git diff --check` and CSS color-token audit passed.
What is next (exact next step):
- Commit the completed T0.7 implementation as `feat(T0.7): llm connection settings`, then start T0.8 Basic chat (no tools) per `docs/PLAN.md`.
Files touched:
- `src/DevOpsAgent.Application/Llm/LlmProviderContracts.cs`
- `src/DevOpsAgent.Application/Llm/ProviderService.cs`
- `src/DevOpsAgent.Domain/Llm/LlmProvider.cs`
- `src/DevOpsAgent.Infrastructure/Migrations/20261113200840_PersistProviderTestSummary.*`
- `src/DevOpsAgent.Infrastructure/Persistence/AppDbContext.cs`
- `src/DevOpsAgent.Web/Components/Layout/Sidebar.razor`
- `src/DevOpsAgent.Web/Components/Layout/TopBar.razor`
- `src/DevOpsAgent.Web/Components/Pages/Settings.razor`
- `src/DevOpsAgent.Web/Components/UI/Field.razor`
- `src/DevOpsAgent.Web/Components/UI/SwitchRow.razor`
- `src/DevOpsAgent.Web/Components/_Imports.razor`
- `src/DevOpsAgent.Web/ProviderStatusState.cs`
- `src/DevOpsAgent.Web/Program.cs`
- `src/DevOpsAgent.Web/wwwroot/css/app.css`
- `tests/DevOpsAgent.Tests/LlmProviderTests.cs`
- `docs/PROGRESS.md`
Gotchas found:
- Keep API keys out of component state after loading; the form uses a blank password field plus an unspecific saved-key indicator, and blank input means retain the stored key.

## Blockers

_(none)_

---

## Task status

Statuses: `todo` · `in-progress` · `done` · `blocked`

| Task | Title | Status | Commit |
|---|---|---|---|
| T0.1 | Solution skeleton | done | `feat(T0.1): solution skeleton` |
| T0.2 | Docker and compose | done | `feat(T0.2): docker and compose` |
| T0.3 | Persistence | done | `feat(T0.3): sqlite persistence` |
| T0.4 | Single-user login | done | `feat(T0.4): single-user login` |
| T0.5 | Design system and app shell | done | `feat(T0.5): design system and app shell` |
| T0.6 | LLM providers — domain, storage, client | done | `feat(T0.6): llm providers backend` |
| T0.7 | Settings › LLM connection page | in-progress | |
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
- 2026-11-13 · Codex · T0.3 · build/test pass; app startup created SQLite DB, applied migration, and wrote Data Protection key ring
- 2026-11-13 · Codex · T0.5 · started
- 2026-11-13 · Codex · T0.5 · build/test pass; checked both themes and 390px layout in browser
- 2026-11-13 · Codex · T0.2 · started
- 2026-11-13 · Codex · T0.2 · Compose build and run verified; health endpoint, host DB/migration/key ring, and non-root process checked
- 2026-11-13 · Codex · T0.4 · started
- 2026-11-13 · Codex · T0.4 · build/test pass; login, logout, health/static access, and both themes verified
- 2026-11-13 · Codex · T0.6 · started
- 2026-11-13 · Codex · T0.6 · build/test pass; migration startup, fake HTTP routine, and Ollama qwen2.5:0.5b smoke verified
- 2026-11-13 · Codex · T0.7 · started
