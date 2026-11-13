# Progress

**This file is the handoff.** Update it at the start of a task, at every checkpoint, and at the end. A new session (another account, another tool, another model) continues from here with no other context.

---

## Current

- **Task:** T1.2 SSH pool and runner
- **Status:** blocked
- **Last updated:** 2026-11-14 by Codex

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
- T1.1 Server inventory is complete and committed; build/test pass (7/7).
- T1.2 scope: SSH connection pool, timeout-aware command runner with optional `sudo -n`, jump-host local forwarding, and server capability probe.
- Checked current official SSH.NET docs: stable `SSH.NET` 2026.0.0 targets .NET 8+; async connect/command, command timeout/status, and local port forwarding APIs are available.
- Confirmed SSH.NET's `HostKeyEventArgs.CanTrust` defaults to true. The connection layer must override this and verify against pinned host keys.
- Added SSH.NET 2026.0.0 with decision D-018 (exact host/port host-key pins in `/keys/known_hosts`); package restore and build pass with zero warnings/errors.
- Implemented the connection pool, reconnect behavior, recursive jump-host tunnels, command runner, sudo wrapping, and OS/uptime/sudo/tool capability probe.
- Added the Servers test-connection dialog with OS, uptime, sudo, tool, and actionable pinned-host-key failure results.
- Added tests for exact host/port/key pin matching, mismatch reporting, and jump-route cycles; final build passes with zero warnings/errors and tests pass (13/13).
- Verified dependency injection and migrations at app startup with an isolated SQLite database; checked both themes and the 390px layout.
What is next (exact next step):
- T1.2 cannot be marked done until a real direct host and a host reached through a jump server can be tested. Provide an authorized test target, identity keys, and verified `/keys/known_hosts` entries, then run both connection tests.
Files touched:
- `docs/PROGRESS.md`
- `docs/DECISIONS.md`
- `Directory.Packages.props`
- `src/DevOpsAgent.Infrastructure/DevOpsAgent.Infrastructure.csproj`
- `src/DevOpsAgent.Application/Abstractions/ISshRunner.cs`
- `src/DevOpsAgent.Application/Servers/ServerTestContracts.cs`
- `src/DevOpsAgent.Application/Servers/ServerCapabilityService.cs`
- `src/DevOpsAgent.Application/Servers/ServerService.cs`
- `src/DevOpsAgent.Domain/Servers/Server.cs`
- `src/DevOpsAgent.Infrastructure/Ssh/*`
- `src/DevOpsAgent.Infrastructure/Persistence/ServerKeyStore.cs`
- `src/DevOpsAgent.Infrastructure/Persistence/PersistenceServiceCollectionExtensions.cs`
- `src/DevOpsAgent.Web/Program.cs`
- `src/DevOpsAgent.Web/Components/Pages/Servers.razor`
- `src/DevOpsAgent.Web/wwwroot/css/app.css`
- `tests/DevOpsAgent.Tests/ServerInventoryTests.cs`
- `tests/DevOpsAgent.Tests/SshAndCapabilityTests.cs`
Gotchas found:
- T0.8 remains blocked on missing OpenRouter credentials.
- T1.2 live verification needs a real direct target, jump-host target, identity keys, and pinned host-key entries; none are configured in the repository or `/keys`.

## Blockers

T0.8 OpenRouter live chat verification is blocked because `.env`, `OPENROUTER_API_KEY`, and `OPENAI_API_KEY` are absent. The generic OpenAI-compatible fixture and prior Ollama smoke test verified the local provider flow, but cannot confirm the real OpenRouter service response. Generated untracked Graphify output was stashed as `orphaned WIP 2026-11-14` during resume reconciliation.

T1.2 live SSH acceptance is blocked because there is no configured test host or jump host and `/keys` has no identity or `known_hosts` files. Fake-runner tests, exact host/port pin tests, route-cycle rejection, and isolated app startup are verified. No real SSH connection was attempted. To unblock, provide an authorized direct host and jump-host route with identity keys and independently verified `/keys/known_hosts` entries.

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
- 2026-11-14 · Codex · T1.1 · server CRUD, migration startup, jump guard, both themes, and 390px layout verified; build/test pass (7/7)
- 2026-11-14 · Codex · T1.2 · started
- 2026-11-14 · Codex / GPT-6 · resumed T1.2; SSH pool, command runner, capability UI and safeguards implemented; build/test pass (13/13), live SSH blocked by missing test targets
