# Architecture — DevOps Agent

One ASP.NET Core process in one container. It serves the Blazor UI (Interactive Server), runs background services (discovery scheduler, metric poller, health checks, alert engine, notifier), owns the SSH pool, and talks to the LLM endpoint.

```
Browser ──HTTPS/WebSocket──▶ DevOpsAgent.Web (Blazor Interactive Server + minimal APIs)
                                  │
                                  ├─ Application (use cases, interfaces)
                                  ├─ Domain (graph, servers, alerts, actions)
                                  └─ Infrastructure
                                        ├─ SSH pool (SSH.NET) ──SSH──▶ managed servers (public + private, via jump host)
                                        ├─ Collectors + parsers
                                        ├─ EF Core + SQLite (/data/devops-agent.db)
                                        ├─ LLM (Microsoft.Extensions.AI IChatClient) ──HTTP──▶ Ollama / LM Studio / OpenRouter
                                        └─ Notifiers ──HTTPS──▶ Telegram / ntfy
```

---

## 1. Solution layout

```
DevOpsAgent.slnx
Directory.Build.props          # net10.0, LangVersion 14, Nullable, TreatWarningsAsErrors
Directory.Packages.props       # central package versions
.editorconfig
Dockerfile
docker-compose.yml
.env.example
src/
  DevOpsAgent.Domain/
    Servers/        Server, ServerGroup, JumpRoute, SudoCapability
    Graph/          Snapshot, Node, NodeKind, Edge, EdgeKind, Evidence, Listener, IpIndex
    Monitoring/     MetricSample, HealthCheck, Alert, AlertSeverity, AlertState
    Agent/          Conversation, Message, ToolCall, ActionRecord, ExecutionMode
    Llm/            LlmProvider, ProviderType, ProviderTestResult
  DevOpsAgent.Application/
    Abstractions/   ISshRunner, ICollector, ISnapshotStore, IChatClientFactory, INotifier, IClock, ISecretProtector
    Discovery/      DiscoveryService, GraphBuilder, EdgeResolver, DriftService
    Monitoring/     MetricPoller, HealthCheckRunner, AlertEngine
    Agent/          AgentService, ToolRegistry, Tools/*, ContextBuilder, ApprovalService
    Llm/            ProviderService (CRUD, test, list models)
    Servers/        ServerService, SshConfigImporter
  DevOpsAgent.Infrastructure/
    Persistence/    AppDbContext, configurations, migrations
    Ssh/            SshConnectionPool, SshRunner, JumpTunnel
    Collectors/     HostCollector, ListenerCollector, DockerCollector, SystemdCollector,
                    NginxCollector, ConnectionCollector, AuthLogCollector, UpdatesCollector
    Parsers/        IpJsonParser, SsParser, DockerInspectParser, NginxConfigParser (tokenizer + AST),
                    SshConfigParser, AuthLogParser, AptParser, DnfParser
    Llm/            OpenAiCompatibleChatClientFactory, ModelLister, JsonToolProtocol
    Notifications/  TelegramNotifier, NtfyNotifier
    Security/       DataProtectionSecretProtector
  DevOpsAgent.Web/
    Program.cs
    Components/
      App.razor, Routes.razor
      Layout/       AppShell.razor, Sidebar.razor, TopBar.razor
      UI/           BentoGrid, Card, StatTile, Button, Badge, Switch, Field, Select, Segmented,
                    Tabs, ProposedActionCard, CodeBlock, EmptyState, Toast
      Pages/        Overview, Topology, Servers, ServerDetail, Alerts, Updates, Drift,
                    AgentChat, ActionLog, Settings/(Llm, Servers, ExecutionModes, Notifications, Discovery), Login
    Hosted/         DiscoveryScheduler, MetricPollerService, HealthCheckService, AlertService
    Api/            minimal API endpoints (health, graph JSON for Cytoscape)
    wwwroot/
      css/tokens.css, css/app.css
      fonts/        PlusJakartaSans-*.woff2, JetBrainsMono-*.woff2
      lib/cytoscape/, lib/elk/
      js/graph.js   (Cytoscape interop module)
      js/theme.js
tests/
  DevOpsAgent.Tests/
    Fixtures/       ip-addr.json, ss-tulpn.txt, docker-inspect.json, nginx-T.txt, auth.log, ...
    Parsers/, Discovery/, Agent/, Llm/
```

Dependency rule: `Domain` ← `Application` ← `Infrastructure` ← `Web`. `Web` may reference `Infrastructure` only for DI registration.

---

## 2. Key components

### SSH
- `SshConnectionPool`: one `SshClient` per server, lazily connected, health-checked, recreated on failure. Commands lease the client; eviction, failure and repeated timeouts retire it and it is disposed when the last lease ends, jump tunnels outlive their dependents (D-036).
- Time limits (D-037): `RunAsync`'s timeout is the deadline of the whole call, connect time included; a host whose commands time out four times in a row (one more than the three that recycle a client) enters the same per-host backoff as an unreachable host; metric (50 s) and alert (25 s) cycles have their own deadline; once the host is stopping (`IHostApplicationLifetime.ApplicationStopping`, re-delivered on a pool thread) the pool refuses new work and abandons connects and commands in flight with `SshShutdownException` (a cancellation, never a failure or an alert).
- Failure classification (D-039): a failed login (key rejected by the server, key file missing/unreadable/invalid, passphrase missing or wrong) is `HostFailureKind.AuthFailed`, a state of its own next to `Connect` and `CommandTimeout`: retried every minute, never doubled, for its first three consecutive failures and then paused (no automatic attempt at all until "Retry now", "Test connection" or an edit of the connection settings; in-memory, a restart allows three more), shown as "Auth failed" / "Auth failed — automatic retries paused" and alerted as "SSH authentication failed" under its own rule `ssh-auth-failed` (never `server-unreachable`). Only connection-level failures are "unreachable". Texts for these are fixed wording (`SshAuthFailedException`, `SshFailureClassifier`); no library message, key path, passphrase or user name is ever shown or stored.
- Connect attempts are single-flight (D-039): one attempt per server at a time, shared by every caller that arrives while it runs and has no failure on record to wait out (or by "Retry now" callers for a "Retry now" attempt); a caller's deadline or cancellation only ends its own wait, the last one to leave ends the attempt.
- Jump host: connect to the jump server, open `ForwardedPortLocal` to target `host:port`, connect the target client through `127.0.0.1:<local port>`.
- `ISshRunner.RunAsync(serverId, command, RunOptions { Sudo, Timeout }, ct)` → `CommandResult { ExitCode, StdOut, StdErr, Duration }`.
- Sudo: `sudo -n <cmd>`; capability detected once with `sudo -n true`. With a stored per-server sudo password (D-040, `SudoPasswordGate`): `LC_ALL=C sudo -S -p '' -- sh -c 'exec </dev/null; printf "%s\n" "<marker>"; <cmd>'` (probe: the same with `true`), password on stdin only; only the per-call random marker coming back (`OutputMarker`, read from stdout as it arrives) confirms the password, anything else without sudo's rejection lines is "outcome unknown" (`Server.SudoPasswordOutcomeUnknownAtUtc`, persisted, lifted by Retry now/Test connection or an edit, T7.R25); the attempt is written down before the password is sent (`SudoPasswordAttemptStartedAtUtc`) so one cut off by a crash becomes unknown; one password sudo per server at a time until it is confirmed or its outcome is stored; none starts with less than 20 s left of the call or cycle deadline (`SshRunOptions.NotAfterUtc`); a rejection (`Server.SudoPasswordRejectedAtUtc`, persisted) stops all sudo on that server (calls return exit 1 with fixed text, nothing is sent) until the password is edited or removed ("Retry now"/"Test connection" never retry a rejected password, D-040 amendment 1); a rejection also opens the Medium alert `sudo-auth-failed` (`Server.SudoAuthFailedSinceUtc`, kept across an edit, cleared by the next confirmed password sudo or by removing the password, D-040 amendments 2 and 4); every command's output from every server is redacted of every stored sudo password (`IKnownSecrets`, T7.R26), and so are alert text and LLM input; the Servers page follows background sudo changes through `ServerStateEvents`. Callers decide on sudo with `Server.CanUseSudo`.

### Collectors
```csharp
public interface ICollector
{
    string Name { get; }
    Task<bool> IsApplicableAsync(ServerContext ctx, CancellationToken ct);   // e.g. `command -v docker`
    Task<CollectorOutput> CollectAsync(ServerContext ctx, CancellationToken ct); // raw text, stored
    CollectorResult Parse(CollectorOutput raw);                               // pure, unit-tested
}
```
`Parse` is pure and deterministic: same raw input → same result. Raw output is stored with the snapshot (NFR-6).

### Graph building
1. Run all applicable collectors per server (servers in parallel, collectors sequential per server).
2. Build `IpIndex` from every host interface and every Docker network endpoint.
3. Create nodes (Host, Container, Service, Domain, Subnet).
4. Declared edges from nginx (and later app configs) via `EdgeResolver` (order in PRD GRF-3).
5. Observed edges from live connections: the side whose port matches a known listener is the server.
6. Unmatched addresses → `Unknown` node.
7. Persist snapshot; compute drift vs previous snapshot.

### LLM
- `IChatClientFactory.Create(LlmProvider)` → `IChatClient` using the OpenAI-compatible client with `Endpoint = BaseUrl`.
- Tool calling via Microsoft.Extensions.AI function invocation when the provider supports it; otherwise `JsonToolProtocol` (system prompt describes tools; model replies with a JSON block; app parses, executes, feeds result back).
- Mutating tools never auto-execute in confirm mode: the tool returns `PendingApproval { actionId }`, UI shows `ProposedActionCard`, approval executes and the result is appended to the conversation.

### Background services
| Service | Interval | Notes |
|---|---|---|
| DiscoveryScheduler | 6 h (configurable) | Also triggered manually |
| MetricPollerService | 60 s | One combined command per server reading `/proc/loadavg`, `/proc/meminfo`, `/proc/stat`, `df -P`, `/proc/net/dev` |
| HealthCheckService | 60 s | HTTP, TCP, TLS expiry |
| AlertService | on new data | Rules, de-dup, notify |
| RollupService | 1 h | 5-min rollups, prune raw > 24 h |

UI gets live updates through the Blazor circuit (services publish to an in-process event bus; components subscribe).

---

## 3. Data model (EF Core, SQLite)

| Entity | Key fields |
|---|---|
| Server | Id, Name, Host, Port, User, KeyFile, KeyPassphrase (protected), SudoPassword (protected, optional), SudoPasswordRejectedAt?, SudoAuthFailedSince?, JumpServerId?, Group, Tags (json), SudoCapable, LastSeenAt, OsInfo |
| LlmProvider | Id, Name, Type, BaseUrl, ApiKey (protected), Model, ContextWindow, Temperature, TimeoutSeconds, NativeToolCalling, Streaming, IsDefault, LastTest (json) |
| AppSetting | Key, Value (json) — execution modes, notification config, discovery interval, redaction toggle |
| Snapshot | Id, StartedAt, FinishedAt, Status, ServerIds (json) |
| CollectorRun | Id, SnapshotId, ServerId, Collector, RawOutput, ExitCode, Error, Duration |
| Node | Id, SnapshotId, Kind, Key (stable identity across snapshots), Name, ParentNodeId?, ServerId?, Attributes (json) |
| Listener | Id, NodeId, Address, Port, Protocol, Process, Pid |
| Edge | Id, SnapshotId, SourceNodeId, TargetNodeId, Port, Protocol, Kind |
| Evidence | Id, EdgeId, Collector, FilePath?, Line?, Snippet?, SocketPair?, SeenAt? |
| MetricSample | ServerId, Metric, Value, At (raw + rollup tables) |
| HealthCheck | Id, Name, Type, Target, IntervalSeconds, LastStatus, LastCheckedAt |
| Alert | Id, Rule, Severity, State, ServerId?, NodeKey?, Title, Details (json), FirstSeen, LastSeen, Count |
| Conversation / Message | Id, Title, CreatedAt / Id, ConversationId, Role, Content, ToolCalls (json) |
| ActionRecord | Id, ConversationId?, Tool, ServerIds, Commands (json), Status (proposed/approved/rejected/succeeded/failed), Output, ExitCodes, ProposedAt, DecidedAt, FinishedAt |

`Node.Key` is a stable identity (e.g. `host:srv-edge`, `container:srv-app-1/api`, `domain:api.example.com`) so drift compares snapshots by key.

---

## 4. Docker

- Multi-stage Dockerfile: `sdk:10.0` build → `aspnet:10.0` runtime, non-root user, `EXPOSE 8080`.
- `docker-compose.yml`:
  - `app`: ports `8080:8080`, volumes `./data:/data`, `./keys:/keys:ro`, env from `.env`.
  - `ollama` (profile `ollama`): `ollama/ollama`, volume `ollama:/root/.ollama`.
- `.env.example`: `ADMIN_USER`, `ADMIN_PASSWORD_HASH` (or `ADMIN_PASSWORD` hashed at first start), `ASPNETCORE_URLS=http://+:8080`.
- Health endpoint `GET /healthz`.
- Data Protection keys persisted to `/data/keys`.
