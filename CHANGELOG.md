# Changelog

## 0.1.0 — MVP — 2026-11-15

- Ship the self-hosted Docker app for SSH inventory, topology discovery, monitoring, alerts, update management, and approved agent actions.
- Verify journeys J1–J7 on the disposable staging lab; J4 used the configured OpenRouter provider and its approved `web` restart was recorded in the Action log.
- Verify a clean-clone Docker build, login, and `srv-edge` discovery. Systemd live verification is deferred to a real LXC/VM and does not block this release.
