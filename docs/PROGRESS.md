# Progress

**This file is the handoff.** Update it at the start of a task, at every checkpoint, and at the end. A new session (another account, another tool, another model) continues from here with no other context.

---

## Current

- **Task:** T0.8 Basic chat (no tools)
- **Status:** blocked
- **Last updated:** 2026-11-14 by resume session

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
- T0.8 implementation and local verification are complete; resumed build passed with 0 warnings/errors and tests passed (4/4).
- OpenRouter endpoint check is blocked: `.env`, `OPENROUTER_API_KEY`, and `OPENAI_API_KEY` are absent. Existing local fixture and Ollama checks are recorded in the prior session log.
- Untracked generated `graphify-out/` did not match the T0.8 WIP notes and was stashed as `orphaned WIP 2026-11-14`.
What is next (exact next step):
- Configure an OpenRouter provider and key in Settings, send a harmless prompt, and verify the real endpoint; if it passes, mark T0.8 done and commit `feat(T0.8): basic persisted chat`, otherwise fix the provider flow.
Files touched:
- `docs/PROGRESS.md`
Gotchas found:
- Stored API keys are not exposed in settings component state; use the app's blank replacement field and saved-key indicator.
- SQLite EF Core does not translate `DateTimeOffset` sorting; sort loaded conversation summaries in application memory.
- Newly created chat messages must be explicitly added to `ChatMessages`.

## Blockers

T0.8 OpenRouter live chat verification is blocked because `.env`, `OPENROUTER_API_KEY`, and `OPENAI_API_KEY` are absent. The generic OpenAI-compatible fixture and prior Ollama smoke test verified the local provider flow, but cannot confirm the real OpenRouter service response. Generated untracked Graphify output was stashed as `orphaned WIP 2026-11-14` during resume reconciliation.

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
| T0.7 | Settings › LLM connection page | done | `feat(T0.7): llm connection settings` |
| T0.8 | Basic chat (no tools) | blocked | |
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
- 2026-11-13 · Codex · T0.7 · build/test pass; settings save/test/default flow, saved-key privacy, both themes, and 390px layout verified
- 2026-11-13 · Codex · T0.8 · started
- 2026-11-13 · Codex · T0.8 · conversation persistence and provider-backed streaming service checkpointed; next implement the chat page
- 2026-11-14 · Codex · T0.8 · chat UI, streamed fixture response, reload persistence, theme and mobile layout verified; live OpenRouter check pending credentials
- 2026-11-14 · Codex / GPT-6 · resumed T0.8; build/test pass, OpenRouter credential blocker confirmed, orphaned Graphify output stashed
