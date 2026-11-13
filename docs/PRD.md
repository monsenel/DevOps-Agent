# PRD — DevOps Agent

Version: 2.0 (web, Dockerized) · Owner: Mohsen · Date: 2026-11-13

DevOps Agent is a self-hosted web app, shipped as a Docker image, that connects to my servers over SSH, maps how they are wired together, monitors them, and carries out DevOps and security work through an LLM. It is a personal tool for one operator.

---

## 1. Problem

- The real topology lives in scattered configs: nginx upstreams, Docker networks, connection strings, firewall rules. Nobody has the whole picture.
- Private networks behind the public servers are invisible from outside, so external scanners cannot map them.
- Routine work (patching, log review, checking for attacks) is manual and easy to skip.
- Existing tools are either heavy (full observability stacks) or blind to config (generic SSH managers).

## 2. Goals

| ID | Goal | Measure |
|---|---|---|
| G1 | Map the infrastructure automatically | 10 servers produce a correct service graph with ports in under 60 seconds |
| G2 | Answer wiring questions from evidence | "Which port does nginx use to reach the backend?" answered with the config file and line |
| G3 | Notice problems before I do | Brute-force attempts, new open ports, full disks and expiring certificates raise an alert |
| G4 | Do routine ops through chat | Update, restart, block an IP, read logs from one screen |
| G5 | Work with any LLM | Ollama, LM Studio, OpenRouter, any OpenAI-compatible endpoint, configured in Settings |

## 3. Non-goals

- Multi-user, roles, SaaS.
- Installing an agent on managed servers.
- Replacing Prometheus/VictoriaLogs (v2 reads from them).
- Kubernetes.
- Hardening beyond a single-user login. The app runs behind a VPN/WireGuard, never exposed directly to the internet.

## 4. Deployment model

- One Docker image. `docker compose up` on a host that can reach the servers (e.g. a VM on the Proxmox host inside the private network).
- Volumes: `/data` (SQLite DB, snapshots), `/keys` (SSH private keys, read-only).
- Optional `ollama` service in the same compose file (profile `ollama`).
- Runs 24/7, so monitoring and scheduled discovery do not depend on a browser being open.
- Access: single admin account (username + password hash from environment), cookie auth.

## 5. Scope by phase

| Phase | Includes |
|---|---|
| **MVP** (M0–M6 in PLAN.md) | Docker/compose, login, design system (light/dark), Settings › LLM connection, server inventory + SSH (jump host), discovery (host, listeners, Docker, systemd, nginx), graph (service + network view), trace, evidence, live connections, Unknown nodes, snapshots + drift, monitoring + health checks + alerts + Telegram/ntfy, agent chat with typed tools + approval, action log, auth-log attack detection + IP ban, update listing + single-server apply |
| **v2** | Collectors for app config/connection strings, Nexus, PostgreSQL, Redis, firewall, HAProxy/Traefik/Caddy, TLS; task routing per provider; provider fallback; rolling patches; CVE scan; file integrity; runbooks; incident mode; anomaly baselines; daily digest; Prometheus/VictoriaLogs read |
| **Later** | Windows targets (WinRM), Proxmox API layer, Kubernetes, snapshots before risky changes |

## 6. Functional requirements (MVP)

### Settings › LLM connection
| ID | Requirement |
|---|---|
| LLM-1 | Add/edit/remove providers: type (Ollama, LM Studio, OpenRouter, OpenAI-compatible custom), display name, base URL, API key (optional), model, context window, temperature, timeout |
| LLM-2 | "Refresh" lists models from the endpoint (`GET {baseUrl}/models`) |
| LLM-3 | "Test connection": reachable, models listed, tool calling supported, streaming supported; result stored and shown |
| LLM-4 | Mark one provider as default for chat and tool calls |
| LLM-5 | Toggle native tool calling; when off, use a JSON-in-prompt tool protocol |
| LLM-6 | Toggle streaming |
| LLM-7 | API keys stored encrypted at rest (ASP.NET Data Protection, key ring on `/data`) and never returned to the browser in full |
| LLM-8 | Cloud-provider options shown but only "Redact secrets/IPs before sending" is functional in MVP; task routing and fallback cards render as "v2" (disabled) |

### Inventory and SSH
| ID | Requirement |
|---|---|
| INV-1 | Add a server: name, host, port, user, key file (picked from `/keys`), optional key passphrase |
| INV-2 | Optional jump server (another inventory server) — tunnel through it |
| INV-3 | Tags and group (prod/staging) |
| INV-4 | Import from pasted `~/.ssh/config` |
| INV-5 | Pooled SSH sessions, reconnect on failure, per-command timeout |
| INV-6 | Detect passwordless sudo; record it per server |

### Discovery
| ID | Requirement |
|---|---|
| DSC-1 | Host collector: `ip -j addr`, `ip -j route`, `hostname`, `/etc/hosts` |
| DSC-2 | Listener collector: `ss -tulpnH` → port, protocol, process, PID |
| DSC-3 | Docker collector: `docker ps`, `docker inspect`, `docker network inspect`, compose labels |
| DSC-4 | systemd collector: running services + unit files |
| DSC-5 | nginx collector: `nginx -T` → server blocks, listen, server_name, upstream, proxy_pass/fastcgi_pass/grpc_pass, stream blocks, with file + line |
| DSC-6 | Live connections: `ss -tnH state established` on host and inside each container's network namespace (requires sudo; degrade gracefully) |
| DSC-7 | Raw collector output stored per snapshot so parsing can be re-run |
| DSC-8 | Servers collected in parallel; one failing server does not fail the run |

### Graph
| ID | Requirement |
|---|---|
| GRF-1 | Nodes: Host, Subnet, Container, Service, Domain, Unknown. Containment: Host ⊃ Container ⊃ Service |
| GRF-2 | Edges: source, target, port, protocol, kind = declared / observed / inferred, with evidence |
| GRF-3 | Edge resolution order: nginx upstream names → Docker network DNS → `/etc/hosts` → `getent hosts` → global IP index → listener/published-port match → Unknown |
| GRF-4 | Service view (left-to-right: domain → proxy → app → data) and Network view (subnets as zones) |
| GRF-5 | Click node → details; click edge → evidence (file:line + snippet, or socket pair + last seen) |
| GRF-6 | Trace: enter a domain/URL, highlight full path |
| GRF-7 | Declared vs observed styling (solid = both, dashed warn = observed only, faded = declared only) |
| GRF-8 | Unknown node → "Add this server" (prefills host, suggests jump server) |
| GRF-9 | Snapshots per discovery run; drift diff between any two; scheduled discovery (default every 6 h) |
| GRF-10 | Search by name, IP, port; filter by tag/compose project/subnet |

### Monitoring and alerts
| ID | Requirement |
|---|---|
| MON-1 | Poll CPU, memory, disk, load, network every 60 s via `/proc` and `df` |
| MON-2 | systemd unit and container state, restart loops |
| MON-3 | Health checks: HTTP status, TCP port, TLS expiry |
| MON-4 | Threshold alerts with severity (high/medium/low), de-duplication, open/acknowledged/resolved |
| MON-5 | Live status overlay on graph |
| MON-6 | Notifications: Telegram bot and/or ntfy |
| MON-7 | Metrics kept raw 24 h, then 5-minute rollups for 30 days |

### Agent
| ID | Requirement |
|---|---|
| AGT-1 | Typed tools: `list_servers`, `query_graph`, `run_discovery`, `service_status`, `restart_service`, `docker_ps`, `docker_logs`, `tail_log`, `read_file`, `disk_usage`, `list_updates`, `apply_updates`, `block_ip`, `unblock_ip`, `list_bans`, `run_shell` |
| AGT-2 | Each tool declares read-only or mutating |
| AGT-3 | Execution modes per server group: read-only / confirm each change (default) / auto within allowlist |
| AGT-4 | Mutating calls produce a "Proposed action" card showing exact commands and servers; Approve/Reject |
| AGT-5 | `run_shell` always requires approval |
| AGT-6 | Action log: tool, servers, command, stdout/stderr, exit code, conversation, approved by, timestamps |
| AGT-7 | Context builder attaches relevant graph slice, open alerts, recent metrics — not raw dumps |
| AGT-8 | Streaming chat; conversation history persisted |

### Security detection (MVP)
| ID | Requirement |
|---|---|
| SEC-1 | Parse `journalctl -u ssh*` / `/var/log/auth.log`: failed-login bursts, logins from new IPs, sudo by unusual users |
| SEC-2 | Alert on new listeners vs previous snapshot |
| SEC-3 | Read fail2ban/CrowdSec bans if present; ban/unban through nftables/ufw/fail2ban (whichever exists) |

### Updates (MVP)
| ID | Requirement |
|---|---|
| UPD-1 | List pending updates (apt/dnf), security separated |
| UPD-2 | Apply on one server with approval; report restarted services and reboot-required |

## 7. Non-functional requirements

| ID | Requirement |
|---|---|
| NFR-1 | Agentless: only tools already on servers; collectors degrade when a tool or sudo is missing |
| NFR-2 | Discovery of 10 servers < 60 s |
| NFR-3 | Unreachable server → its nodes marked stale from last snapshot |
| NFR-4 | Discovery, monitoring, alerts work with no LLM configured |
| NFR-5 | Fully usable offline with a local model; no CDN assets |
| NFR-6 | Declared edges reproducible from stored raw output |
| NFR-7 | Container idles < 300 MB RAM with 20 servers |
| NFR-8 | Graph responsive at 500 nodes / 1,500 edges |
| NFR-9 | UI works at ≥ 1280 px and stays usable at 390 px (cards stack) |
| NFR-10 | Both themes pass WCAG AA contrast for text |

## 8. User journeys

### J1 — First run
1. `docker compose up`; open the app; log in with the admin credentials from `.env`.
2. Settings › LLM connection: add Ollama (`http://ollama:11434/v1`) or OpenRouter; Test connection shows reachable, models, tool calling.
3. Servers: paste `~/.ssh/config` or add servers; choose a jump server for private ones; key files picked from `/keys`.
4. The app tests each connection, shows OS, uptime, sudo availability, and which collectors will run with reduced detail.
5. Tag servers; set execution mode per group (default: confirm each change).
6. Run first discovery.

### J2 — Discover and expand the map
1. Run discovery on 10 servers; per-server progress by collector.
2. Service view: domains → nginx → backends → data stores, ports on edges.
3. Two Unknown nodes: private IPs backends talk to that aren't in inventory.
4. Click one: shows who connects to it and on which port; "Add this server" prefilled with jump host.
5. Added server is discovered alone; Unknown becomes a named host.
6. Network view shows public vs private subnets.

### J3 — Trace a request
1. Type `api.example.com` into Trace (or ask in chat).
2. Path highlighted: domain → nginx server block → upstream → api container → postgres + redis.
3. Click nginx→api edge: `proxy_pass` with `/etc/nginx/conf.d/api.conf:14`, resolved IP:port, Docker port mapping, observed connections count.

### J4 — Daily check
1. Overview shows server count, pending updates, open alerts, drift since yesterday, load per server.
2. Ask the agent "anything I should look at?" → ranked list from alerts, metrics, drift.
3. Follow-up "why is disk on srv-tools at 91%?" → read-only tools → top consumers.
4. Agent proposes cleanup; I approve; result in action log.

### J5 — Attack alert
1. Telegram: "srv-edge: 400 failed SSH logins in 5 min from 3 IPs".
2. Alert detail: IPs, usernames, any success, fail2ban state.
3. Agent checks all servers for successful logins, new listeners, authorized_keys changes.
4. Proposes `block_ip` ×2 for 24 h; I approve; bans confirmed; alert closed with timeline.

### J6 — Patch
1. Updates page lists pending updates, security first.
2. Apply on staging server; shows packages + command; approve.
3. Report: restarted services, reboot required, health checks re-run.
4. Reboot: agent uses graph to say what will be unreachable; I confirm.

### J7 — Drift
1. Scheduled discovery → "3 changes since yesterday".
2. Drift page: added listener :9000, changed nginx upstream, removed container.
3. Changes highlighted on graph.
4. Agent summarises; I mark expected ones.
