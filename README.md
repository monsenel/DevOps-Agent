# DevOps Agent — build kit

Self-hosted, Dockerized web app that maps, monitors and operates your servers over SSH with an LLM.

This folder is the **planning and handoff kit** for building it with an AI coding agent (Codex, Antigravity, Claude Code, or any tool that can read files and run commands). It is designed so you can switch account, tool or model at any point and continue.

## Run the app

Requirements: Docker Compose v2, a Linux server reachable over SSH, its private key, and its SSH host key. Keep the app behind your VPN or WireGuard.

1. Create local data and key folders, then copy SSH material into `./keys`:

   ```bash
   mkdir -p data keys
   chmod 700 keys
   chmod 600 keys/your_private_key
   ```

   Put private identity files in `./keys`. Put verified OpenSSH host-key lines in `./keys/known_hosts` (for non-default ports, use OpenSSH's `[host]:port` format). The app mounts this directory read-only at `/keys`; configure each server and select its identity in **Servers**.

2. Configure the single-user login in `.env`. Start from `.env.example` and set a unique `ADMIN_USER` and `ADMIN_PASSWORD`. For deployment, prefer `ADMIN_PASSWORD_HASH`; keep `.env` private and never commit it.

3. Start the app:

   ```bash
   docker compose up --build -d
   ```

   Open `http://localhost:8080`, sign in, add a server group and server, then run discovery from **Topology**. `./data` stores the SQLite database and persistent data-protection keys. Back up that directory securely.

4. Add an LLM provider in **Settings → LLM** to enable Agent chat and proposed actions. Ollama can also run with `docker compose --profile ollama up --build -d`.

The app container has `host.docker.internal` mapped to the Docker host gateway. Use that name for SSH targets reachable through a host-published port, such as a local test lab; `127.0.0.1` inside the app container refers to the container itself.

### Test lab and safe first run

The disposable lab script (`dva-testlab.sh`, kept outside this repository) can create the staging SSH targets used for acceptance checks. Start and inspect only the lab containers you need, then add their SSH key and verified host-key entries as described above. For a host-published SSH port, use `host.docker.internal` as the server address from the app. Do not register `srv-search` when checking the Unknown-node journey; it should be discovered as an unregistered peer.

For a first run against real servers, add one server in **read-only** execution mode and run discovery before enabling Agent actions. Review the resulting inventory and graph, then configure notifications and an LLM provider as needed. Keep the app and its SSH access behind a VPN or WireGuard; do not expose the login or SSH keys to the public internet.

Notifications can use ntfy (server URL, topic, optional token) or Telegram (bot token and chat ID) under **Settings → Notifications**. Configure a destination and use **Test** before relying on alert delivery. Agent chat requires a provider under **Settings → LLM**; local Ollama can be started with the command above, or use an OpenAI-compatible endpoint such as LM Studio or OpenRouter.

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
