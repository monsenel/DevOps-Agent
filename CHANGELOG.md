# Changelog

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
