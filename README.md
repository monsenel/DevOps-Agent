# DevOps Agent — build kit

Self-hosted, Dockerized web app that maps, monitors and operates your servers over SSH with an LLM.

This folder is the **planning and handoff kit** for building it with an AI coding agent (Codex, Antigravity, Claude Code, or any tool that can read files and run commands). It is designed so you can switch account, tool or model at any point and continue.

## Files

| File | What it is |
|---|---|
| `AGENTS.md` | Rules for the agent: read order, work loop, stack, conventions. Read automatically by tools that support `AGENTS.md`. |
| `GEMINI.md` | Pointer to `AGENTS.md` for tools that look for `GEMINI.md`. |
| `docs/PRD.md` | Product requirements and user journeys (web/Docker version). |
| `docs/ARCHITECTURE.md` | Solution layout, components, data model, Docker. |
| `docs/DESIGN.md` | Bento design system: tokens (light/dark), typography, components, graph styling, page specs. |
| `docs/design/mockups/` | Approved mockups (templated HTML) for Overview and Settings › LLM. |
| `docs/PLAN.md` | 35 small tasks in 7 milestones with acceptance criteria. |
| `docs/PROGRESS.md` | **The handoff file.** Current task, WIP notes, status of every task, session log. |
| `docs/DECISIONS.md` | Append-only decision log. |
| `prompts/` | Prompts to paste into the agent. |

## How to use

1. Create an empty git repo and copy this folder's contents into its root.
2. First session: paste `prompts/00-kickoff.md`.
3. When the agent stops: paste `prompts/02-continue.md`.
4. About to hit a usage limit: paste `prompts/03-before-you-stop.md` **before** it happens.
5. New account, new tool or new model: open the same repo and paste `prompts/01-resume.md`.
6. End of each milestone: run `prompts/04-milestone-review.md`, ideally with a different model.
7. Red build: `prompts/05-fix-build.md`. UI looks off: `prompts/06-ui-conformance.md`.

## Rules for switching accounts or tools

- **Always work from the same git repository** (push to a private remote so any machine/account can pull it). The repo is the only memory.
- Make sure the last session committed and left a clear "exact next step" in `docs/PROGRESS.md`. If it was cut off mid-task, `01-resume.md` tells the new session how to recover from uncommitted changes and `wip(...)` commits.
- Do not run two agents on the same repo at the same time.

## What you need to provide

- At M1: one or two test servers and SSH key files placed in `./keys` (never committed).
- At T0.6/T0.7: an Ollama instance (`docker compose --profile ollama up`) and/or an OpenRouter key.
- At T5.4: a Telegram bot token + chat id, or an ntfy topic.
- Keep the deployed app behind a VPN/WireGuard; it holds SSH keys to all your servers.
