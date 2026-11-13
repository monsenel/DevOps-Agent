# Progress

**This file is the handoff.** Update it at the start of a task, at every checkpoint, and at the end. A new session (another account, another tool, another model) continues from here with no other context.

---

## Current

- **Task:** T0.5 Design system and app shell
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
- T0.3 persistence is committed and verified.
- Read the Overview and Settings mockups and added self-hosted WOFF2 fonts with their license texts.
- Implemented light/dark tokens, the interactive theme toggle, responsive shell and navigation, Overview structure, placeholder nav pages, all required UI primitives, and `/dev/components` previews for both themes.
- `dotnet build DevOpsAgent.slnx` passes with zero warnings.
What is next (exact next step):
- Launch the app and inspect the Overview plus component preview in both themes at desktop and 390px widths; fix any layout or accessibility issues, then rerun build/tests.
Files touched:
- `docs/PROGRESS.md`, `docs/DECISIONS.md`, `src/DevOpsAgent.Web/Components/**`, `src/DevOpsAgent.Web/wwwroot/**`.
Gotchas found:
- The Google Fonts CSS endpoint returned TTF assets; Fontsource packages provided the requested WOFF2 files and bundled SIL license texts.
- The .NET template's ReconnectModal included hard-coded colors; it was removed because the design system requires all UI colors to use `--dva-*` tokens.

## Blockers

_(none)_

---

## Task status

Statuses: `todo` · `in-progress` · `done` · `blocked`

| Task | Title | Status | Commit |
|---|---|---|---|
| T0.1 | Solution skeleton | done | `feat(T0.1): solution skeleton` |
| T0.3 | Persistence | done | `feat(T0.3): sqlite persistence` |
| T0.2 | Docker and compose | todo | |
| T0.3 | Persistence | todo | |
| T0.4 | Single-user login | todo | |
| T0.5 | Design system and app shell | in-progress | |
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
- 2026-11-13 · Codex · T0.3 · build/test pass; app startup created SQLite DB, applied migration, and wrote Data Protection key ring
- 2026-11-13 · Codex · T0.5 · started
