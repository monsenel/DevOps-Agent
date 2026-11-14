# Progress

**This file is the handoff.** Update it at the start of a task, at every checkpoint, and at the end. A new session (another account, another tool, another model) continues from here with no other context.

---

## Current

- **Task:** T6.1 Tool registry and execution modes
- **Status:** done
- **Last updated:** 2026-11-14 by Codex

## WIP notes

No active WIP. T6.1 is complete. T5.1 (and dependent M5 tasks T5.2–T5.5) remain deferred at the owner's direction.

## Blockers

T0.8 OpenRouter live chat verification is blocked because `.env`, `OPENROUTER_API_KEY`, and `OPENAI_API_KEY` are absent. The generic OpenAI-compatible fixture and prior Ollama smoke test verified the local provider flow, but cannot confirm the real OpenRouter service response.

T1.2 live SSH acceptance is blocked because there is no configured test host or jump host and `/keys` has no identity or `known_hosts` files. Fake-runner tests, exact host/port pin tests, route-cycle rejection, and isolated app startup are verified. No real SSH connection was attempted. To unblock, provide an authorized direct host and jump-host route with identity keys and independently verified `/keys/known_hosts` entries.

Per owner direction, T1.2 live verification is deferred while T2.1 proceeds; return to those checks when authorized test hosts and keys are available.

T2.7 live acceptance is blocked by the same missing authorized SSH targets and pinned host keys. The topology UI was verified with a temporary two-host persisted graph, but that does not satisfy the plan's real two-server discovery criterion. Return to T2.7 once live SSH verification can run.

T3.3's implementation and synthetic trace tests are complete, but PLAN requires J3 to work on real servers. Verification is blocked by the missing authorized SSH targets and pinned host keys recorded under T1.2/T2.7. Resume real trace/evidence verification when those are available; do not treat the synthetic UI check as satisfying J3.

T5.1–T5.5 are deferred at the owner's direction to skip M5 and start M6. T5.1 is marked blocked; T5.2–T5.5 remain todo because they depend on M5 monitoring data and checks. Revisit M5 when the owner requests it.

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
| T1.1 | Server inventory | done | `feat(T1.1): server inventory` |
| T1.2 | SSH pool and runner | blocked | |
| T1.3 | Import ssh config | done | `feat(T1.3): import ssh config` |
| T2.1 | Collector framework and snapshots | done | `feat(T2.1): collector framework and snapshots` |
| T2.2 | Host collector | done | `feat(T2.2): host collector` |
| T2.3 | Listener collector | done | `feat(T2.3): listener collector` |
| T2.4 | Docker collector | done | `feat(T2.4): docker collector` |
| T2.5 | systemd collector | done | `feat(T2.5): systemd collector` |
| T2.6 | Graph builder | done | `feat(T2.6): graph builder` |
| T2.7 | Topology page | blocked | `wip(T2.7): topology page and graph interop` |
| T3.1 | nginx parser | done | `feat(T3.1): nginx parser` |
| T3.2 | Edge resolution | done | `feat(T3.2): nginx edge resolution` |
| T3.3 | Evidence panel and Trace | blocked | `wip(T3.3): trace evidence UI` |
| T4.1 | Live connections | done | `feat(T4.1): observed connections` |
| T4.2 | Unknown nodes | done | `feat(T4.2): unknown node expansion` |
| T4.3 | Drift and scheduled discovery | done | `feat(T4.3): drift and scheduled discovery` |
| T5.1 | Metric poller | blocked | |
| T5.2 | Health checks | todo | |
| T5.3 | Alert engine and Alerts page | todo | |
| T5.4 | Notifications | todo | |
| T5.5 | Overview with real data | todo | |
| T6.1 | Tool registry and execution modes | done | `feat(T6.1): tool policies and approvals` |
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
- 2026-11-14 · Codex · T1.1 · server CRUD, migration startup, jump guard, both themes, and 390px layout verified; build/test pass (7/7)
- 2026-11-14 · Codex · T1.2 · started
- 2026-11-14 · Codex / GPT-6 · resumed T1.2; SSH pool, command runner, capability UI and safeguards implemented; build/test pass (13/13), live SSH blocked by missing test targets
- 2026-11-14 · Codex · T1.3 · started
- 2026-11-14 · Codex / GPT-6 · T1.3 · parser fixtures and import dialog complete; paste/preview/import and jump mapping verified, both themes and 390px checked, build/test pass (15/15)
- 2026-11-14 · Codex / GPT-6 · resumed T1.2 · refreshed Graphify output (869 nodes, 1,687 links); build/test pass; SSH blocker unchanged
- 2026-11-14 · Codex / GPT-6 · resumed T1.2 · build/test pass (15/15); real SSH and jump-host verification remains blocked by missing test targets and keys
- 2026-11-14 · Codex / GPT-6 · resumed T2.1 · owner directed defer T1.2 live check and proceed with collector framework
- 2026-11-14 · Codex / GPT-6 · T2.1 · snapshot/run persistence, bounded server parallelism, progress events, failure isolation; build/test pass (18/18)
- 2026-11-14 · Codex / GPT-6 · T2.2 · started
- 2026-11-14 · Codex / GPT-6 · T2.2 · host discovery parser and read-only SSH collector; fixture coverage for interfaces, address families, CIDR and default routes; build/test pass (21/21)
- 2026-11-14 · Codex / GPT-6 · T2.3 · started
- 2026-11-14 · Codex / GPT-6 · T2.3 · listener parser and collector; fixture coverage for endpoints, wildcard/scoped addresses, missing process info, sudo behavior; build/test pass (23/23)
- 2026-11-14 · Codex / GPT-6 · T2.4 · started
- 2026-11-14 · Codex / GPT-6 · T2.4 · Docker container/network collection, env value redaction, ID validation and fixture parser; build/test pass (26/26)
- 2026-11-14 · Codex / GPT-6 · T2.5 · started
- 2026-11-14 · Codex / GPT-6 · T2.5 · chained listener results into systemd ownership matching and fixture-backed unit parsing; build/test pass (28/28)
- 2026-11-14 · Codex / GPT-6 · T2.6 · started
- 2026-11-14 · Codex / GPT-6 · T2.6 · graph entities/migration, stable-key builder and IP index, discovery integration, Cytoscape-compatible service/network API; migration applied to temp SQLite; build/test pass (31/31)
- 2026-11-14 · Codex / GPT-6 · T2.7 · started
- 2026-11-14 · Codex / GPT-6 · resumed T2.7 · clean working tree; build passed with zero warnings/errors and tests passed (31/31); reviewed topology UI requirements and implementation contracts
- 2026-11-14 · Codex / GPT-6 · T2.7 · topology UI and graph interop implemented; synthetic two-host browser check passed, real SSH acceptance blocked by missing targets/keys; build/test pass (31/31)
- 2026-11-14 · Codex / GPT-6 · T3.1 · started
- 2026-11-14 · Codex / GPT-6 · T3.1 · deterministic nginx tokenizer/block parser and fixture coverage complete; build/test pass (33/33)
- 2026-11-14 · Codex / GPT-6 · T3.2 · started
- 2026-11-14 · Codex / GPT-6 · resumed T3.2 · edge resolver, graph evidence, and path tests; build clean, 39/39 tests pass
- 2026-11-14 · Codex / GPT-6 · T3.2 · completed
- 2026-11-14 · Codex / GPT-6 · T3.3 · started
- 2026-11-14 · Codex / GPT-6 · resumed T3.3 · edge evidence, trace graph and path highlighting implemented; build clean, 40/40 tests pass, light/dark UI checked; real-server J3 verification blocked
- 2026-11-14 · Codex / GPT-6 · T4.1 · started after deferring T3.3's real-server acceptance
- 2026-11-14 · Codex / GPT-6 · T4.1 · host/container connection parsing, observed-edge ownership and evidence; build clean, 45/45 tests pass
- 2026-11-14 · Codex / GPT-6 · T4.2 · started
- 2026-11-14 · Codex / GPT-6 · resumed T4.2 · Unknown detail, prefilled inventory flow, targeted discovery/merge; build clean, 47/47 tests pass and both themes checked
- 2026-11-14 · Codex / GPT-6 · T4.2 · complete; synthetic J2 flow verified, live SSH unavailable
- 2026-11-14 · Codex / GPT-6 · resumed T4.3 · started drift comparison and scheduled discovery
- 2026-11-14 · Codex / GPT-6 · T4.3 · drift service, persistent schedule and initial UI implemented; SQLite ordering adjusted, build clean and 49/49 tests pass
- 2026-11-14 · Codex / GPT-6 · T4.3 · complete; synthetic snapshots showed add/remove/change graph highlights, schedule save/readback and both themes verified; build clean and 49/49 tests pass
- 2026-11-14 · Codex / GPT-6 · resumed T6.1 · owner directed skipping M5; initial build passed with 0 warnings/errors and tests passed (49/49)
- 2026-11-14 · Codex / GPT-6 · T6.1 · typed tool registry and policy/approval matrix; migration applied, settings checked in both themes, build clean and 62/62 tests pass
