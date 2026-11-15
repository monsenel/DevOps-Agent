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

The app container has `host.docker.internal` mapped to the Docker host gateway. Use that name for SSH targets reachable through a host-published port, such as a local test lab; `127.0.0.1` inside the app container refers to the container itself. On Linux, a port published only on host loopback (`127.0.0.1`) is not reachable through the Docker gateway; bind the test port to the Docker gateway address or use a temporary forward bound only to that bridge.

### Test lab and safe first run

The disposable lab script (`tools/dva-testlab.sh`) can create the staging SSH targets used for acceptance checks. It uses fixed addresses on 10.88.10.0/24 (public), 10.88.20.0/24 (private) and 10.88.30.0/24 (egress). `LAB_DIR` (default `$PWD/dva-testlab-fixed-data`) controls where its data and generated SSH key go; run it from a directory outside this repository, or set `LAB_DIR`, so they never land in the repo. Start and inspect only the lab containers you need, then add their SSH key and verified host-key entries as described above. For a host-published SSH port, use `host.docker.internal` as the server address from the app. Do not register `srv-search` when checking the Unknown-node journey; it should be discovered as an unregistered peer.

For a first run against real servers, add one server in **read-only** execution mode and run discovery before enabling Agent actions. Review the resulting inventory and graph, then configure notifications and an LLM provider as needed. Keep the app and its SSH access behind a VPN or WireGuard; do not expose the login or SSH keys to the public internet.

Notifications can use ntfy (server URL, topic, optional token) or Telegram (bot token and chat ID) under **Settings → Notifications**. Configure a destination and use **Test** before relying on alert delivery. Notifications carry only server name, rule, severity and state; details stay in the Alerts page. The app's own sudo use by a server's SSH user (its `sudo -n true` probe and the fixed command lines listed below) does not raise the "sudo by non-allowlisted user" alert; any other sudo by that user does. Only add the user to `SecurityMonitoring__SudoAllowlist__0` if you accept losing that signal.

### Sudo on managed servers

Give the app a dedicated SSH user. It runs every privileged command as `sudo -n -- sh -c '<command>'`; the complete list lives in `src/DevOpsAgent.Infrastructure/Monitoring/AppSudoCommands.cs`:

| Used by | Command (inside `sh -c`) |
|---|---|
| Capability probe | `true` (as `sudo -n true`) |
| Listener collector | `ss -tulpnH` |
| nginx collector | `if command -v nginx >/dev/null 2>&1; then nginx -T; else /usr/sbin/nginx -T; fi` |
| Docker collector | `docker info >/dev/null 2>&1`, `docker ps -q`, `docker network ls -q`, `docker inspect <ids>`, `docker network inspect <ids>` |
| Connection collector | `nsenter -t <pid> -n ss -tnH state established` |
| Security scan | journal/auth.log read plus fail2ban/nftables/ufw status (`AuthLogScanner.BuildCommand`) |
| Update scan (confirm groups only) | `apt-get update` then `apt list --upgradable` (`UpdatesCollector.AptScanCommand`) |
| Agent tools (after approval, or read-only `disk_usage`) | `systemctl restart -- '<unit>'`, package install with restart/reboot report, fail2ban/ufw/nftables ban and unban, `df`/`du` |

On real servers, restrict the user's sudoers entry to these commands instead of `NOPASSWD: ALL`. The fixed ones can be pinned exactly, for example `deploy ALL=(root) NOPASSWD: /bin/sh -c ss -tulpnH`. The ones with variable parts (IDs, PIDs, the scan cursor, unit and package names, addresses) cannot be pinned safely while they run through `sh -c`, because a sudoers wildcard would also match extra shell commands; for those, rely on read-only execution mode, the approval step and the sudo alert, and track sudoers hardening as T7.R14.

A restricted `NOPASSWD` sudoers entry stays the recommended setup (D-040). Only where that is not possible, enter a **Sudo password** in the server's settings: it is stored encrypted, never shown again, and sudo then runs as `LC_ALL=C sudo -S -p '' -- sh -c '<command>'` with the password written to the command's stdin (never on the command line, in logs, action output, alerts, notifications or anything sent to the LLM; it is removed from everything the server sends back). A rejected password is tried **once**: after that no sudo is used on that server (the Servers page shows "sudo: password — rejected") until you edit or remove the password. **Retry now** and Test connection do not try a rejected password again (Retry now is disabled for it, with the reason shown), because each wrong password can count twice in pam_faillock and a common lockout limit is 3. Collectors that need no sudo keep running. If the server uses pam_faillock, also add `Defaults:<user> passwd_tries=1` to the user's sudoers entry: with sudo's default of three tries a wrong password counts twice in pam_faillock (the second, empty try fails too), with one try it counts once. The **Sudo** column shows which mode each server uses: `sudo: NOPASSWD`, `sudo: password` or `sudo: none`. Agent chat requires a provider under **Settings → LLM**; local Ollama can be started with the command above, or use an OpenAI-compatible endpoint such as LM Studio or OpenRouter.

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
