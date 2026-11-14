# Progress

**This file is the handoff.** Update it at the start of a task, at every checkpoint, and at the end. A new session (another account, another tool, another model) continues from here with no other context.

---

## Current

- **Task:** T5.3 Alert engine and Alerts page
- **Status:** blocked
- **Last updated:** 2026-11-14 by Codex
T5.3 implementation is complete; only live container stop/restart acceptance remains. Exact next step: after the owner adds an authorized Docker-capable SSH server and its independently verified host key, run PLAN.md container stop/restart acceptance and confirm the alert opens within 2 minutes and resolves after restart. The local app is running on loopback for server setup.
## WIP notes

T5.3 implementation is complete: persistent de-duplicated alert lifecycle; scheduled read-only SSH status polling for Docker and failed systemd units; disk, sustained memory/load, SSH reachability, health-check, TLS expiry, and new-listener rules; Alerts page, sidebar count, and graph severity overlay. Build is clean with 0 warnings/errors, 90/90 tests pass, AddAlerts migration applied in isolated SQLite, and the seeded Alerts page was verified in light/dark. Exact next step: run the PLAN.md container-stop/restart acceptance against an authorized Docker-capable SSH server with a pinned host key; confirm alert opens within 2 minutes and resolves after restart. No such target/key is configured. The owner also reported that Add server does not appear to save. Reproduction showed the required private key field silently blocked submission because it had no validation message. Added a clear “Select a private key file.” validation message beneath the selector. The local app now uses a separate `data/ssh-keys` directory and `known_hosts` path so Data Protection keys are not offered as SSH identities. Build is clean and all 90 tests pass; browser verification confirms the missing-key message appears while the form stays open and no server is saved.

## Blockers

T1.2 live SSH acceptance is blocked because there is no configured test host or jump host and `/keys` has no identity or `known_hosts` files. Fake-runner tests, exact host/port pin tests, route-cycle rejection, and isolated app startup are verified. No real SSH connection was attempted. To unblock, provide an authorized direct host and jump-host route with identity keys and independently verified `/keys/known_hosts` entries.

Per owner direction, T1.2 live verification is deferred while T2.1 proceeds; return to those checks when authorized test hosts and keys are available.

T2.7 live acceptance is blocked by the same missing authorized SSH targets and pinned host keys. The topology UI was verified with a temporary two-host persisted graph, but that does not satisfy the plan's real two-server discovery criterion. Return to T2.7 once live SSH verification can run.

T3.3's implementation and synthetic trace tests are complete, but PLAN requires J3 to work on real servers. Verification is blocked by the missing authorized SSH targets and pinned host keys recorded under T1.2/T2.7. Resume real trace/evidence verification when those are available; do not treat the synthetic UI check as satisfying J3.

The prior M5 deferral has been lifted by the owner's instruction to continue task by task. T5.1 and T5.2 are complete. T5.3 implementation is complete, but live acceptance is blocked: no authorized Docker-capable SSH target and pinned host key are configured. Continue when those are available; do not run container-stop tests against owner servers without authorization. The owner authorized the live acceptance attempt on 2026-11-14; inspection found `/keys` empty and the local app database contained zero server records, so no remote command was run. The app is now running locally with a dedicated SSH-key directory; wait for the owner to add the authorized test server and pinned host key before remote acceptance.

T6.4 is deferred because it depends on incomplete T6.3 and blocked T5.3. Resume after those dependencies are completed.

T6.6 remains blocked because T6.3 and T6.4 acceptance is unresolved; it depends on all preceding tasks and requires end-to-end journey checks.

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
| T0.8 | Basic chat (no tools) | done | `feat(T0.8): basic chat with Ollama and OpenRouter` |
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
| T5.1 | Metric poller | done | `feat(T5.1): metric polling and server detail` |
| T5.2 | Health checks | done | `feat(T5.2): scheduled health checks` |
| T5.3 | Alert engine and Alerts page | blocked | `wip(T5.3): alert engine and monitoring UI` |
| T5.4 | Notifications | todo | |
| T5.5 | Overview with real data | todo | |
| T6.1 | Tool registry and execution modes | done | `feat(T6.1): tool policies and approvals` |
| T6.2 | Action log | done | `feat(T6.2): approved action execution and log` |
| T6.3 | Agent chat with tools | done | `feat(T6.3): complete J4 tool and approval flow` |
| T6.4 | SSH attack detection and bans | blocked | |
| T6.5 | Updates | done | `feat(T6.5): updates inventory and approval flow` |
| T6.6 | MVP hardening pass | blocked | |

Recommended order inside M0: T0.1 → T0.3 → T0.5 → T0.2 → T0.4 → T0.6 → T0.7 → T0.8.

---

## Session log

One line per session: date · tool/model · tasks touched · result.

- 2026-11-14 · Codex / GPT-6 · started T0.8 · owner supplied an OpenRouter key; checking local Ollama and resuming live basic-chat acceptance in temporary app storage
- 2026-11-14 · Codex / GPT-6 · T0.8 · fixed native tool schema lifetime; build passed with 0 warnings/errors and 78/78 tests; qwen2.5:0.5b and OpenRouter free route streamed replies and survived reload
- 2026-11-14 · Codex / GPT-6 · T6.3 · fixed streamed JSON fallback dispatch; build passed with 0 warnings/errors and 79/79 tests; J4 tool calls and explicit approval passed with OpenRouter and Ollama
- 2026-11-14 · Codex / GPT-6 · started owner-requested sidebar layout · desktop sidebar must be fixed/full-viewport-height; preserve mobile stacking and reconcile DESIGN.md rule
- 2026-11-14 · Codex / GPT-6 · sidebar layout · desktop full-height fixed sidebar verified in light/dark; mobile stack verified at 390px; build clean and 79/79 tests passed
- 2026-11-14 · Codex / GPT-6 · T6.3 cleanup · stopped temporary app, SSH daemon, and Ollama container; removed temporary DB, encrypted key ring, SSH keys, and test files; next run Graphify incremental update
- 2026-11-14 · Codex / GPT-6 · resumed T6.3 · fixed full-height sidebar verified and committed; Graphify incremental refresh completed (5,415 nodes, 18,841 edges, 299 communities); next resume T5.1 when M5 is requested
- 2026-11-14 · Codex / GPT-6 · resumed T5.1 · clean tree; build passed with 0 warnings/errors and tests passed (79/79); owner lifted M5 deferral
- 2026-11-14 · Codex / GPT-6 · T5.1 · combined read-only polling, CPU/network deltas, raw samples, five-minute rollups, 24-hour raw retention, and server detail complete; SQLite migration/startup verified; values shown in both themes, mobile layout checked; build clean and 84/84 tests pass
- 2026-11-14 · Codex / GPT-6 · T5.1 follow-up · enforced PRD's 30-day rollup retention; build clean and 84/84 tests pass
- 2026-11-14 · Codex / GPT-6 · started T5.2 · health check CRUD, scheduled HTTP/TCP/TLS probes, and server-detail status

- 2026-11-14 · Codex / GPT-6 · T6.5 · J6 steps 1–3 passed on disposable Debian apt target; security and standard updates classified, explicit approval executed both, report exit 0/no restarts/reboot false, follow-up scan showed 0 pending; temporary test environment removed

- 2026-11-14 · Codex / GPT-6 · resumed T6.5 · clean tree; build passed with 0 warnings/errors and tests passed (77/77); J6 still needs an authorized apt/dnf SSH target; Graphify code-only update completed (5,388 nodes, 18,826 edges, 325 communities)

- 2026-11-14 · Codex / GPT-6 · resumed T6.5 · clean tree; build passed with 0 warnings/errors and tests passed (77/77); J6 SSH target remains the next dependency
- 2026-11-14 · Codex / GPT-6 · Graphify · code-only graph refreshed to 5,388 nodes, 18,826 edges, 325 communities; semantic docs and HTML skipped

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
- 2026-11-14 · Codex / GPT-6 · resumed T6.2 · started approved-action execution and action log
- 2026-11-14 · Codex / GPT-6 · T6.2 · per-server approved execution, persisted output and Action log verified in both themes; fresh migration, clean build and 66/66 tests pass
- 2026-11-14 · Codex / GPT-6 · resumed T6.3 · started agent tool chat integration
- 2026-11-14 · Codex / GPT-6 · T6.3 · context, native/JSON tool loops, read-only dispatch, approvals, redaction and inline review implemented; 72 tests pass; live J4 blocked by unavailable local Ollama/OpenRouter credentials and deferred M5 data
- 2026-11-14 · Codex / GPT-6 · started T6.5 after deferring blocked T6.3 live acceptance and dependency-blocked T6.4
- 2026-11-14 · Codex / GPT-6 · T6.5 · started update inventory and apply reporting implementation
- 2026-11-14 · Codex / GPT-6 · T6.5 · apt/dnf parsers, persisted scans, security classification, single-server approval flow and restart/reboot report added; migration verified in isolated SQLite; build clean and 77/77 tests pass; J6 live SSH acceptance blocked by unavailable authorized test server
- 2026-11-14 · Codex / GPT-6 · refreshed Graphify after T6.5; 5,591 nodes, 19,025 edges, 291 communities; fixed one C# parser ambiguity and reverified build/tests

- 2026-11-14 · Codex / GPT-6 · resumed T5.2 · health checks implemented; build clean, 87/87 tests pass, migration applied in isolated SQLite, detail UI verified in light/dark and 390px; started T5.3
- 2026-11-14 · Codex / GPT-6 · started T5.3 · inspect metric/discovery/health contracts, then implement deduplicated alert lifecycle and UI; live container test needs an authorized host

- 2026-11-14 · Codex / GPT-6 · T5.3 · alert engine, rule signals, lifecycle, Alerts UI/sidebar badge/graph overlay implemented; 90/90 tests pass, 0-warning build, migration/runtime UI checked; container stop/restart acceptance blocked by missing authorized SSH target and pinned key

- 2026-11-14 · Codex / GPT-6 · resumed T5.3 · confirmed blocked live acceptance; rebuilt Graphify (5,894 nodes, 20,083 edges, 306 communities), integrity check found 60 self-loops and no dangling/missing endpoints

- 2026-11-14 · Codex / GPT-6 · T5.3 · owner authorized live acceptance; checked `/keys` and app inventory (empty); no SSH target or pinned host key exists locally, so remote test cannot run

- 2026-11-14 · Codex / GPT-6 · T5.3 · fixed missing private-key validation feedback in server form; clean build, 90/90 tests; verified in browser; local app running on loopback with temporary admin password and isolated SSH key directory
