# Changelog

## Unreleased

Independent review of 0.1.2 (T7.R1–T7.R3):

- Health checks of a server in SSH backoff are skipped and keep their last result instead of being stored as failing (they raised a High alert for sites that may be fine).
- Restart-loop alert: one restart after an earlier, resolved loop no longer reopens it; a container needs two consecutive `restarting` samples to alert (D-035).
- A connect that fails after the server was edited no longer puts the new settings into backoff; editing a jump server resets the backoff of the servers behind it.
- Drift no longer throws when two Docker DNS listeners share one identity.

Independent review of the SSH lifecycle work (T7.R15, T7.R17–T7.R19) and the auth-retry cap:

- A command's start no longer blocks the cycle's thread: with a frozen jump host the alert cycle took 31 s (deadline 25 s, interval 30 s); it now ends within its deadline.
- Security alerts of a host whose status probe fails stay open instead of ageing out after 5 minutes.
- A server that closes the connection during login (sshd MaxAuthTries, login penalties) counts as a failed login, so the three-attempt cap applies to it too.
- Health checks of a host whose login failed keep running (they use no SSH).
- A container query that fails half-way is a failed sample; a stored container history with a missing field is reported as corrupt instead of stopping every alert evaluation.

## 0.1.2 — 2026-11-15

- T7.R1: per-host SSH backoff (1 min doubling to 15 min, reset on success); servers behind an unreachable jump host are reported unreachable without a connection attempt; alerts, metrics and health checks run 8 servers in parallel; "Retry now" on Servers bypasses the backoff. With a blackholed host, discovery takes ~15 s and metric cycles stay well under 60 s.
- T7.R2: an unreachable server's nodes, listeners and edges are carried forward from the last snapshot, dimmed and labelled "stale · last seen <time>"; outages and recoveries no longer produce drift or new-listener alerts.
- T7.R3: medium alert for containers that are restarting or restarted 3+ times in 10 minutes, from a read-only container query in the metric cycle; resolves after 10 quiet minutes or when the container is removed.

## 0.1.1 — 2026-11-15

Review and close-out of the 0.1.0 MVP. Journeys J1–J7 re-verified on the disposable lab in one clean run.

- Safety: Auto execution mode removed (stored Auto reads as Confirm); groups default to read-only and an unreadable setting fails closed; read-only groups never write on servers (cache-only update scans with "last refreshed"); approve/reject is an atomic transition from Proposed; approval previews show the exact command sent over SSH; `block_ip` refuses app, inventory, jump-host, operator and loopback addresses.
- Secrets: notifications carry only server, rule, severity and state; ntfy topic stored encrypted and masked; tool and action output redacted before it is stored, shown or sent; broader redaction (env/ini, JSON, Bearer, URL userinfo, PEM); `read_file`/`tail_log` refuse sensitive paths; HttpClient logging at Warning; proxy_pass credentials stripped from evidence.
- Alerts: SSH host-key change is a Critical alert distinct from unreachable; sudo alerts de-duplicated per user and exempt only the app's own fixed command lines (sudo's split log lines rejoined).
- Reliability: one database context per unit of work in parallel code; dead SSH sessions recycled after repeated timeouts and evicted only when connection settings change; failed or interrupted discovery runs never become the graph base and are swept on startup; per-server results from fan-out tools.
- UI: `/dev/components` only in Development; login rate-limited; trace and topology search submit on Enter; capability probe looks in `/usr/sbin` and `/sbin`; readable update report; graph alert markers in text; unused stylesheet and Bootstrap removed.
- Open items are tracked as T7.R1–T7.R14 in `docs/PLAN.md` (stale nodes, SSH backoff and restart-loop rule are the high ones).

## 0.1.0 — MVP — 2026-11-15

- Ship the self-hosted Docker app for SSH inventory, topology discovery, monitoring, alerts, update management, and approved agent actions.
- Verify journeys J1–J7 on the disposable staging lab; J4 used the configured OpenRouter provider and its approved `web` restart was recorded in the Action log.
- Verify a clean-clone Docker build, login, and `srv-edge` discovery. Systemd live verification is deferred to a real LXC/VM and does not block this release.
