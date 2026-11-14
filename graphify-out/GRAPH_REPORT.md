# Graph Report - DevOps-Agent  (2026-11-14)

## Corpus Check
- 13 files · ~153,742 words
- Verdict: corpus is large enough that graph structure adds value.

## Summary
- 5990 nodes · 20300 edges · 203 communities (143 shown, 60 thin omitted)
- Extraction: 88% EXTRACTED · 12% INFERRED · 0% AMBIGUOUS · INFERRED: 2342 edges (avg confidence: 0.84)
- Token cost: 2,450 input · 730 output

## Community Hubs (Navigation)
- ELK Layout Engine
- App Db Context
- ELK Layout Engine
- Execution Policy
- ELK Layout Engine
- ELK Layout Engine
- Llm Provider
- Ssh And Capability Tests
- Discovery Service Tests
- Execution Policy
- ELK Layout Engine
- Nginx Collector
- Persistence Components
- Llm Components
- Unit Test1
- ELK Layout Engine
- ELK Layout Engine
- ELK Layout Engine
- ELK Layout Engine
- Tool Registry
- Servers Components
- ELK Layout Engine
- Pages Components
- Tool Registry
- Discovery Service Tests
- ELK Layout Engine
- Execution Policy
- ELK Layout Engine
- ELK Layout Engine
- ELK Layout Engine
- Settings Components
- Imports Components
- Update Package
- ELK Layout Engine
- Cytoscape Graph Library
- ELK Layout Engine
- Persistence Components
- ELK Layout Engine
- ELK Layout Engine
- Cytoscape Graph Library
- Pages Components
- ELK Layout Engine
- Web UI Components
- Discovery Components
- ELK Layout Engine
- Migrations Components
- Pages Topology
- Component Preview
- Execution Policy
- Agent Chat
- ELK Layout Engine
- Pages Settings
- Discovery Components
- Nginx Collector Parser
- Web UI Components
- ELK Layout Engine
- ELK Layout Engine
- Topology Components
- ELK Layout Engine
- ELK Layout Engine
- Pages Servers
- Pages Components
- Ssh And Capability Tests
- ELK Layout Engine
- Open Ai Compatible Chat Client
- ELK Layout Engine
- Docker Compose Configuration
- ELK Layout Engine
- ELK Layout Engine
- Execution Policy
- ELK Layout Engine
- Drift Service
- ELK Layout Engine
- Edge Resolver Tests
- Docs Components
- Layout Sidebar
- Monitoring Components
- ELK Layout Engine
- Topology Components
- 20261113191218 Initial Create Designer
- A R C H I
- Cytoscape Graph Library
- ELK Layout Engine
- Hosted Components
- Cytoscape Graph Library
- Chat Components
- Dev Ops Agent Tests Components
- Collector Run
- Data Protection Secret Protector
- ELK Layout Engine
- Chat Components
- Dev Ops Agent Tests
- ELK Layout Engine
- ELK Layout Engine
- Component Preview
- Monitoring Components
- Collectors Components
- Cytoscape ELK Layout
- Host Collector Parser
- ELK Layout Engine
- ELK Layout Engine
- Cytoscape Graph Library
- ELK Layout Engine
- ELK Layout Engine
- Ssh Components
- Docker Collector Parser
- Ssh Config Parser
- Chat Conversation
- Agent Chat
- ELK Layout Engine
- ELK Layout Engine
- Pages Alerts
- Docker Compose Configuration
- Servers Components
- Pages Home
- Graph Trace Resolver
- Launch Settings
- Graph Node Kind
- ELK Layout Engine
- Home Components
- Graph Node Kind
- Cytoscape Graph Library
- Cytoscape Graph Library
- ELK Layout Engine
- ELK Layout Engine
- ELK Layout Engine
- Web UI Components
- Top Bar
- Discovery Snapshot
- App Setting
- Graph Edge Kind
- Json Tool Protocol
- Nginx Configuration
- Fixtures Components
- Collector Run
- And Ssh Authorized
- Graph Edge Kind
- Nginx Collector Parser
- ELK Layout Engine
- App Setting
- And T5.3 Alert
- Components App
- Persistence Components
- Persistence Options
- Js Theme
- Main Layout
- Configured Telegram Ntfy
- Pages Login
- Pages Logout
- App Shell
- Server Inventory Options
- Pages Components
- Page Placeholder
- Main Layout
- Login Components
- Logout Components
- Cytoscape Graph Library
- App Shell
- Unit Test1
- Docker Entrypoint
- UI Field
- UI Select
- Switch Row
- ELK Layout Engine
- devopsagent web authentication
- P L A N
- Dnf Security
- I Model Lister
- I Secret Protector
- I Server Key Store
- I Server Repository
- I Ssh Connection Pool
- Llm Provider Repository
- Docs PLAN
- T5.5 Overview With
- Code Block
- Progress Bar
- Segmented Components
- favicon Components
- Ssh Connection Options
- Metrics Sample 1
- Ssh Runner
- Ssh Run Options
- docker network ls
- docker ps
- Metrics Sample 2
- Metrics Sample 2
- Metrics Sample 2
- Metrics Sample 2
- Metrics Sample 2
- Remote Service Status
- Remote Service Status

## God Nodes (most connected - your core abstractions)
1. `KC()` - 389 edges
2. `Inb()` - 388 edges
3. `jQb()` - 357 edges
4. `Qdb()` - 353 edges
5. `ND()` - 216 edges
6. `Zmb()` - 182 edges
7. `Zfb()` - 157 edges
8. `mQb()` - 144 edges
9. `bnb()` - 131 edges
10. `Guid` - 127 edges

## Surprising Connections (you probably didn't know these)
- `Collectors and Deterministic Parsing` --semantically_similar_to--> `Deterministic Collectors and Parsers`  [INFERRED] [semantically similar]
  docs/ARCHITECTURE.md → AGENTS.md
- `SqliteTestDatabase` --references--> `AppDbContext`  [EXTRACTED]
  tests/DevOpsAgent.Tests/GraphBuilderTests.cs → src/DevOpsAgent.Infrastructure/Persistence/AppDbContext.cs
- `SqliteTestDatabase` --references--> `AppDbContext`  [EXTRACTED]
  tests/DevOpsAgent.Tests/SshAndCapabilityTests.cs → src/DevOpsAgent.Infrastructure/Persistence/AppDbContext.cs
- `TestSecretProtector` --implements--> `ISecretProtector`  [EXTRACTED]
  tests/DevOpsAgent.Tests/ServerInventoryTests.cs → src/DevOpsAgent.Application/Abstractions/ISecretProtector.cs
- `FakeSshRunner` --implements--> `ISshRunner`  [EXTRACTED]
  tests/DevOpsAgent.Tests/SshAndCapabilityTests.cs → src/DevOpsAgent.Application/Abstractions/ISshRunner.cs

## Import Cycles
- None detected.

## Hyperedges (group relationships)
- **Components of the DevOps Agent Solution** — docs_architecture_clean_architecture_dependency_rule, docs_architecture_ssh_connection_pool_and_jump_host, docs_architecture_collectors_and_deterministic_parsing, docs_architecture_graph_building_and_edge_resolution, docs_architecture_llm_tool_calling_and_approval, docs_architecture_sqlite_data_model, docs_architecture_background_services [EXTRACTED 1.00]
- **Shared Server Metrics Format** — tests_devopsagent_tests_fixtures_metrics_sample_1_load_average, tests_devopsagent_tests_fixtures_metrics_sample_1_memory_metrics, tests_devopsagent_tests_fixtures_metrics_sample_1_cpu_statistics, tests_devopsagent_tests_fixtures_metrics_sample_1_disk_usage, tests_devopsagent_tests_fixtures_metrics_sample_1_network_statistics, tests_devopsagent_tests_fixtures_metrics_sample_1_cpu_cores, tests_devopsagent_tests_fixtures_metrics_sample_2_load_average, tests_devopsagent_tests_fixtures_metrics_sample_2_memory_metrics, tests_devopsagent_tests_fixtures_metrics_sample_2_cpu_statistics, tests_devopsagent_tests_fixtures_metrics_sample_2_disk_usage, tests_devopsagent_tests_fixtures_metrics_sample_2_network_statistics, tests_devopsagent_tests_fixtures_metrics_sample_2_cpu_cores [INFERRED 0.85]

## Communities (203 total, 60 thin omitted)

### Community 0 - "ELK Layout Engine"
Cohesion: 0.01
Nodes (290): _9b(), aee(), Age(), Ahe(), aSc(), Atc(), aYb(), b5c() (+282 more)

### Community 1 - "App Db Context"
Cohesion: 0.03
Nodes (68): CollectorOutput, CollectorResult, IsSuccess, ICollector, Name, ServerContext, Server, SshRunner (+60 more)

### Community 2 - "ELK Layout Engine"
Cohesion: 0.03
Nodes (155): _2d(), _3d(), _5c(), a5b(), a6c(), ACd(), Add(), awd() (+147 more)

### Community 3 - "Execution Policy"
Cohesion: 0.03
Nodes (52): IHealthCheckRepository, AlertCondition, AlertDto, IAlertSignalSource, HealthCheckDto, HealthCheckInput, HealthCheckSuggestion, HealthProbeResult (+44 more)

### Community 4 - "ELK Layout Engine"
Cohesion: 0.03
Nodes (138): b(), c(), A8c(), aGd(), ahb(), ald(), ANd(), BDb() (+130 more)

### Community 5 - "ELK Layout Engine"
Cohesion: 0.02
Nodes (125): _8b(), a7c(), ABb(), Aed(), ahc(), aQd(), Ard(), AUd() (+117 more)

### Community 6 - "Llm Provider"
Cohesion: 0.04
Nodes (54): IActionRecordRepository, IAppSettingRepository, IServerRepository, ActionExecutionService, ActionLogEntry, ActionServerResult, AgentToolDispatcher, ApprovalService (+46 more)

### Community 7 - "Ssh And Capability Tests"
Cohesion: 0.04
Nodes (120): AAb(), aCb(), AOb(), B2c(), b4c(), $Bb(), Bgc(), cCb() (+112 more)

### Community 8 - "Discovery Service Tests"
Cohesion: 0.04
Nodes (116): A2b(), a3c(), A8b(), a_c(), aIc(), Ayc(), B8b(), bXb() (+108 more)

### Community 9 - "Execution Policy"
Cohesion: 0.03
Nodes (38): AlertService, IAlertNotificationService, INotifier, NotificationEndpoint, NotificationSettings, NotificationSettingsInput, NotificationService, StoredSettings (+30 more)

### Community 10 - "ELK Layout Engine"
Cohesion: 0.03
Nodes (114): $2b(), a7b(), Anc(), Awc(), axc(), b6b(), bId(), Bmc() (+106 more)

### Community 11 - "Nginx Collector"
Cohesion: 0.03
Nodes (107): _6d(), _7d(), a7d(), b7d(), b9d(), Bfe(), c0d(), c3d() (+99 more)

### Community 12 - "Persistence Components"
Cohesion: 0.04
Nodes (43): DiscoveryScheduleService, AppDbContext, ActionRecords, AppSettings, ChatConversations, ChatMessages, CollectorRuns, GraphEdges (+35 more)

### Community 13 - "Llm Components"
Cohesion: 0.05
Nodes (43): IChatClientFactory, ILlmProviderRepository, IModelLister, ISecretProtector, LlmProviderInput, LlmProviderSummary, ProviderService, LlmProvider (+35 more)

### Community 14 - "Unit Test1"
Cohesion: 0.05
Nodes (60): IUpdatePackageRepository, IUpdateScanRepository, IUpdatesCollector, AgentToolResult, ApplyUpdatesArgs, BlockIpArgs, DiskUsageArgs, DockerLogsArgs (+52 more)

### Community 15 - "ELK Layout Engine"
Cohesion: 0.04
Nodes (93): A1d(), a6d(), A9d(), A_d(), $ae(), AFd(), bBb(), BFd() (+85 more)

### Community 16 - "ELK Layout Engine"
Cohesion: 0.04
Nodes (90): Zu(), A0d(), Apb(), B5b(), bC(), dPb(), dqd(), Dvb() (+82 more)

### Community 17 - "ELK Layout Engine"
Cohesion: 0.05
Nodes (88): a0b(), Amd(), asb(), __b(), b9b(), B$b(), bhd(), Bmd() (+80 more)

### Community 18 - "ELK Layout Engine"
Cohesion: 0.09
Nodes (86): a3b(), Aac(), as(), bac(), bec(), bic(), Bjc(), BUc() (+78 more)

### Community 19 - "Tool Registry"
Cohesion: 0.05
Nodes (51): IGraphRepository, DockerDiscoveryResult, GraphBuilder, ServerGraphState, GraphBuildResult, ServerDiscoveryData, GraphView, All (+43 more)

### Community 20 - "Servers Components"
Cohesion: 0.06
Nodes (32): IServerKeyStore, ServerCapabilityService, ServerInput, ServerSummary, ServerService, Server, CreatedAtUtc, Group (+24 more)

### Community 21 - "ELK Layout Engine"
Cohesion: 0.04
Nodes (82): AD(), aec(), ATb(), atd(), auc(), bD(), $$c(), cec() (+74 more)

### Community 22 - "Pages Components"
Cohesion: 0.06
Nodes (14): DevOpsAgent.Infrastructure.Collectors, DevOpsAgent.Domain.Agent, DevOpsAgent.Application.Discovery, DevOpsAgent.Domain.Servers, DevOpsAgent.Domain.Graph, DevOpsAgent.Domain.Monitoring, DevOpsAgent.Application.Updates, DevOpsAgent.Infrastructure.Parsers (+6 more)

### Community 23 - "Tool Registry"
Cohesion: 0.06
Nodes (25): DiscoveryProgress, ISnapshotRepository, AgentContextBuilder, ChatConversationSummary, ChatMessageView, DiscoveryService, SnapshotSummary, Snapshot (+17 more)

### Community 24 - "Discovery Service Tests"
Cohesion: 0.08
Nodes (75): _0b(), A6b(), A9b(), a$b(), Acc(), B0b(), bsc(), CGc() (+67 more)

### Community 25 - "ELK Layout Engine"
Cohesion: 0.04
Nodes (75): a2d(), ABd(), Aie(), Aj(), bj(), bzd(), cj(), Coe() (+67 more)

### Community 26 - "Execution Policy"
Cohesion: 0.05
Nodes (28): IMetricRepository, MetricValue, ParsedMetrics, ServerMetrics, MetricService, RollupService, MetricRollup, Average (+20 more)

### Community 27 - "ELK Layout Engine"
Cohesion: 0.03
Nodes (68): ate(), aue(), ave(), Bse(), Bte(), Bue(), Bve(), Cve() (+60 more)

### Community 28 - "ELK Layout Engine"
Cohesion: 0.11
Nodes (63): _1d(), ale(), b2d(), Bh(), Ble(), Bre(), Cle(), Dle() (+55 more)

### Community 29 - "ELK Layout Engine"
Cohesion: 0.04
Nodes (59): Aeb(), agb(), avd(), Be(), bEb(), Bvb(), cDb(), DIb() (+51 more)

### Community 30 - "Settings Components"
Cohesion: 0.09
Nodes (56): Aje(), Bje(), Bke(), Cje(), Dje(), Eje(), eke(), Eoe() (+48 more)

### Community 31 - "Imports Components"
Cohesion: 0.13
Nodes (6): DevOpsAgent.Application.Servers, DevOpsAgent.Infrastructure.Persistence, DevOpsAgent.Infrastructure.Migrations, DevOpsAgent.Tests, DevOpsAgent.Domain.Discovery, AppDbContextModelSnapshot

### Community 32 - "Update Package"
Cohesion: 0.05
Nodes (55): aAe(), aId(), Bnd(), Bo(), bq(), bt(), CId(), Co() (+47 more)

### Community 33 - "ELK Layout Engine"
Cohesion: 0.05
Nodes (51): axb(), Bed(), bQd(), Dmd(), fj(), fk(), FLc(), fmd() (+43 more)

### Community 34 - "Cytoscape Graph Library"
Cohesion: 0.05
Nodes (22): a(), bh(), bs(), Eh(), fh(), fr(), gh(), Jc() (+14 more)

### Community 35 - "ELK Layout Engine"
Cohesion: 0.05
Nodes (50): a3d(), b3d(), bgb(), CHd(), cJc(), cjd(), Cn(), d6b() (+42 more)

### Community 37 - "ELK Layout Engine"
Cohesion: 0.05
Nodes (49): aDb(), Afb(), azb(), CFb(), CNd(), cqd(), CYb(), dFb() (+41 more)

### Community 38 - "ELK Layout Engine"
Cohesion: 0.05
Nodes (47): Afe(), AXd(), aZd(), Bk(), C5c(), cB(), d2d(), Di() (+39 more)

### Community 39 - "Cytoscape Graph Library"
Cohesion: 0.10
Nodes (46): aKc(), aNb(), aRc(), bib(), bJb(), Bkc(), bRc(), cJb() (+38 more)

### Community 40 - "Pages Components"
Cohesion: 0.04
Nodes (37): ActionLogEntry, route:/action-log, route:/agent-chat, route:/updates, ApproveAsync, LoadAsync, OnInitializedAsync, CodeBlock (+29 more)

### Community 41 - "ELK Layout Engine"
Cohesion: 0.06
Nodes (44): Bmb(), C0b(), c1b(), C4b(), D3b(), Dwd(), E2b(), E3b() (+36 more)

### Community 42 - "Web UI Components"
Cohesion: 0.08
Nodes (22): DevOpsAgent.Application.Notifications, DevOpsAgent.Application.Monitoring, DevOpsAgent.Application.Abstractions, DevOpsAgent.Application.Chat, DevOpsAgent.Application.Llm, DevOpsAgent.Application.Servers, DevOpsAgent.Domain.Agent, DevOpsAgent.Domain.Llm (+14 more)

### Community 43 - "Discovery Components"
Cohesion: 0.13
Nodes (12): ConnectionDiscoveryResult, EstablishedConnectionInfo, DockerContainerInfo, DockerNetworkEndpoint, DockerPublishedPort, EdgeResolution, EdgeResolver, NginxEndpoint (+4 more)

### Community 44 - "ELK Layout Engine"
Cohesion: 0.10
Nodes (42): Awe(), aye(), bye(), Cye(), cze(), dq(), dye(), dze() (+34 more)

### Community 45 - "Migrations Components"
Cohesion: 0.08
Nodes (13): InitialCreate, AddLlmProviders, PersistProviderTestSummary, AddChatConversations, AddServerInventory, AddDiscoverySnapshots, AddTopologyGraph, AddAgentActions (+5 more)

### Community 46 - "Pages Topology"
Cohesion: 0.05
Nodes (39): DiscoveryProgress, GraphEvidenceDto, GraphListenerDto, IJSObjectReference, route:/topology, AddUnknownServerAsync, ChangeViewAsync, ClearTraceAsync (+31 more)

### Community 47 - "Component Preview"
Cohesion: 0.06
Nodes (40): AB(), akd(), bKd(), bOb(), eud(), GBb(), hBb(), HKd() (+32 more)

### Community 48 - "Execution Policy"
Cohesion: 0.07
Nodes (22): IAlertRepository, Alert, AcknowledgedAtUtc, Count, Details, FirstSeenAtUtc, Id, Key (+14 more)

### Community 49 - "Agent Chat"
Cohesion: 0.08
Nodes (37): $6b(), amb(), Asd(), bsb(), Bsd(), C2b(), CMb(), D1b() (+29 more)

### Community 50 - "ELK Layout Engine"
Cohesion: 0.08
Nodes (37): ARb(), CIc(), cuc(), dGb(), eed(), eGb(), er(), etb() (+29 more)

### Community 51 - "Pages Settings"
Cohesion: 0.06
Nodes (32): ExecutionMode, ExecutionModePolicy, Field, LlmProviderSummary, PagePlaceholder, route:/settings, route:/settings/llm, AddProviderAsync (+24 more)

### Community 52 - "Discovery Components"
Cohesion: 0.12
Nodes (17): DriftChange, DriftChangeType, Added, Changed, Removed, DriftEntityType, Edge, Listener (+9 more)

### Community 53 - "Nginx Collector Parser"
Cohesion: 0.11
Nodes (19): NginxDiscoveryResult, NameResolutions, NginxLocationInfo, NginxProxyInfo, NginxServerInfo, NginxSourceValue, NginxUpstreamInfo, NginxUpstreamServerInfo (+11 more)

### Community 54 - "Web UI Components"
Cohesion: 0.08
Nodes (9): DevOpsAgent.Web, DevOpsAgent.Application.Chat, DevOpsAgent.Infrastructure.Notifications, DevOpsAgent.Application.Llm, DevOpsAgent.Infrastructure.Llm, DevOpsAgent.Domain.Llm, DevOpsAgent.Application.Agent, System.Net.Http.Json (+1 more)

### Community 55 - "ELK Layout Engine"
Cohesion: 0.08
Nodes (33): m(), w(), _7b(), amc(), b1b(), C8b(), Dl(), dNc() (+25 more)

### Community 56 - "ELK Layout Engine"
Cohesion: 0.07
Nodes (33): _2c(), _3c(), aZc(), B3c(), BZc(), cZc(), E2c(), F1c() (+25 more)

### Community 57 - "Topology Components"
Cohesion: 0.08
Nodes (32): _5b(), Boc(), COc(), dOc(), ehd(), Eoc(), epc(), ESb() (+24 more)

### Community 58 - "ELK Layout Engine"
Cohesion: 0.10
Nodes (32): AJc(), Aoc(), B7b(), Bfb(), EFb(), Ejd(), EKc(), Fid() (+24 more)

### Community 59 - "ELK Layout Engine"
Cohesion: 0.10
Nodes (32): Al(), Alc(), ar(), b4b(), blc(), bld(), Clc(), dlc() (+24 more)

### Community 60 - "Pages Servers"
Cohesion: 0.06
Nodes (28): DataAnnotationsValidator, EditForm, InputNumber, InputSelect, InputText, Microsoft.AspNetCore.Components, route:/servers, ServerSummary (+20 more)

### Community 61 - "Pages Components"
Cohesion: 0.08
Nodes (14): DevOpsAgent.Web.Pages, DevOpsAgent.Web.Authentication, AdminAuthenticationOptions, Password, PasswordHash, User, AdminCredentialVerifier, IsConfigured (+6 more)

### Community 62 - "Ssh And Capability Tests"
Cohesion: 0.10
Nodes (30): Adc(), AHd(), Ao(), cc(), ey(), GHd(), Gsb(), hg() (+22 more)

### Community 63 - "ELK Layout Engine"
Cohesion: 0.12
Nodes (30): APd(), Az(), BPd(), CPd(), cz(), ggb(), mPd(), nb() (+22 more)

### Community 64 - "Open Ai Compatible Chat Client"
Cohesion: 0.07
Nodes (24): InputCheckbox, route:/servers/{ServerId:guid}, AddSuggestionAsync, BackToServers, CloseHealthForm, DeleteHealthCheckAsync, EditHealthCheck, Format (+16 more)

### Community 65 - "ELK Layout Engine"
Cohesion: 0.09
Nodes (27): bz(), d3c(), dA(), Dz(), fq(), fUd(), Gud(), iDb() (+19 more)

### Community 66 - "Docker Compose Configuration"
Cohesion: 0.08
Nodes (25): build, env_file, environment, ports, restart, user, volumes, context (+17 more)

### Community 67 - "ELK Layout Engine"
Cohesion: 0.10
Nodes (26): $4b(), B2b(), D5b(), Dyd(), E9c(), F9c(), FRb(), Goe() (+18 more)

### Community 68 - "ELK Layout Engine"
Cohesion: 0.08
Nodes (25): AJb(), cKb(), ckd(), cld(), FRd(), grd(), Gvb(), HMb() (+17 more)

### Community 69 - "Execution Policy"
Cohesion: 0.13
Nodes (15): IChatConversationRepository, ChatConversation, CreatedAtUtc, Id, Messages, Title, UpdatedAtUtc, ChatMessageRecord (+7 more)

### Community 70 - "ELK Layout Engine"
Cohesion: 0.17
Nodes (24): aib(), cib(), Egd(), fib(), gyb(), Hyb(), Iob(), JOb() (+16 more)

### Community 71 - "Drift Service"
Cohesion: 0.11
Nodes (24): c_c(), D4c(), DPd(), f4c(), f5c(), g3c(), h4c(), h5c() (+16 more)

### Community 72 - "ELK Layout Engine"
Cohesion: 0.11
Nodes (24): dlb(), Efc(), Fgc(), g6b(), GFc(), gub(), Guc(), h6b() (+16 more)

### Community 73 - "Edge Resolver Tests"
Cohesion: 0.09
Nodes (16): Decisions, Design system, Accessibility requirements, Bento dashboard design, Cytoscape graph styling and layouts, Light and dark themes with CSS tokens, 1. DEFINITIONS, 2. GRANT OF RIGHTS (+8 more)

### Community 74 - "Docs Components"
Cohesion: 0.10
Nodes (20): DevOps Agent, Background Services, Clean Architecture Dependency Rule, Collectors and Deterministic Parsing, DevOps Agent Architecture, Graph Building and Edge Resolution, LLM Tool Calling and Approval, SQLite Data Model (+12 more)

### Community 75 - "Layout Sidebar"
Cohesion: 0.10
Nodes (13): AntiforgeryToken, Authorized, AuthorizeView, IDisposable, NavLink, ConnectionEntry, Client, JumpConnection (+5 more)

### Community 76 - "Monitoring Components"
Cohesion: 0.19
Nodes (3): MetricsParser, CollectionCommand, MetricsParserTests

### Community 77 - "ELK Layout Engine"
Cohesion: 0.10
Nodes (21): ace(), Bae(), ep(), gne(), Ine(), jne(), K2d(), Kne() (+13 more)

### Community 78 - "Topology Components"
Cohesion: 0.10
Nodes (21): aLb(), apc(), BLb(), BPc(), cpc(), DPc(), eZb(), i7b() (+13 more)

### Community 79 - "20261113191218 Initial Create Designer"
Cohesion: 0.15
Nodes (7): SshHostKeyMismatchException, Fingerprint, Host, Port, KnownHostEntry, KnownHostsVerifier, SshConnectionPool

### Community 80 - "A R C H I"
Cohesion: 0.10
Nodes (18): MiniGraphEdge, MiniGraphNode, route:/drift, AddEdges, BuildMiniGraph, ChangeClass, CompareAsync, DisplayDescription (+10 more)

### Community 81 - "Cytoscape Graph Library"
Cohesion: 0.17
Nodes (18): De(), Fi(), Gi(), go(), Hi(), ji(), Ki(), Li() (+10 more)

### Community 82 - "ELK Layout Engine"
Cohesion: 0.14
Nodes (20): Avc(), BWc(), bXc(), jWc(), Kwc(), Lwc(), MWc(), OWc() (+12 more)

### Community 83 - "Hosted Components"
Cohesion: 0.15
Nodes (7): DevOpsAgent.Web.Hosted, AlertEngineHostedService, AlertStatusState, OpenCount, HealthCheckHostedService, MetricPollerService, MetricRollupHostedService

### Community 84 - "Cytoscape Graph Library"
Cohesion: 0.26
Nodes (17): i(), n(), o(), od(), On(), a(), n(), pn() (+9 more)

### Community 85 - "Chat Components"
Cohesion: 0.12
Nodes (4): ChatService, ChatServiceTests, ProbeArguments, ToolDescriptor

### Community 86 - "Dev Ops Agent Tests Components"
Cohesion: 0.18
Nodes (17): coverlet.collector, Microsoft.EntityFrameworkCore.Sqlite, Microsoft.Extensions.AI.Abstractions, Microsoft.Extensions.AI.OpenAI, Microsoft.NET.Test.Sdk, SSH.NET, xunit, xunit.runner.visualstudio (+9 more)

### Community 87 - "Collector Run"
Cohesion: 0.12
Nodes (17): CollectorRun, CollectorName, CreatedAtUtc, Duration, Error, ExitCode, Id, RawOutput (+9 more)

### Community 88 - "Data Protection Secret Protector"
Cohesion: 0.14
Nodes (17): Ku(), Av(), dk(), Ir(), Jr(), Lx(), mq(), Mx() (+9 more)

### Community 89 - "ELK Layout Engine"
Cohesion: 0.13
Nodes (18): ase(), B_d(), Bwe(), exd(), h0d(), Ive(), j$d(), Ove() (+10 more)

### Community 90 - "Chat Components"
Cohesion: 0.16
Nodes (4): ChatStreamEvent, ChatContentRedactor, JsonToolProtocol, JsonToolProtocolTests

### Community 91 - "Dev Ops Agent Tests"
Cohesion: 0.21
Nodes (14): _arrayLikeToArray(), _arrayWithHoles(), _classCallCheck(), _createClass(), _defineProperties(), _defineProperty(), _iterableToArrayLimit(), Layout() (+6 more)

### Community 92 - "ELK Layout Engine"
Cohesion: 0.13
Nodes (17): Bqb(), Cqb(), dr(), eib(), en(), Fpb(), GPb(), knd() (+9 more)

### Community 93 - "ELK Layout Engine"
Cohesion: 0.13
Nodes (17): cAb(), dAb(), fEb(), FHb(), gAb(), Ghb(), hAb(), iAb() (+9 more)

### Community 94 - "Component Preview"
Cohesion: 0.12
Nodes (15): AlertRow, ProgressBar, ProposedActionCard, route:/dev/components, Badge, Card, CodeBlock, EmptyState (+7 more)

### Community 95 - "Monitoring Components"
Cohesion: 0.17
Nodes (5): DevOpsAgent.Infrastructure.Monitoring, RemoteContainerStatus, RemoteServiceStatus, RemoteServiceStatusParser, RemoteServiceStatusParserTests

### Community 96 - "Collectors Components"
Cohesion: 0.27
Nodes (6): EtcHostsEntry, HostAddress, HostDiscoveryResult, HostInterface, HostRoute, HostCollectorParser

### Community 97 - "Cytoscape ELK Layout"
Cohesion: 0.14
Nodes (14): _assertThisInitialized(), _callSuper(), _classCallCheck(), _createClass(), _defineProperties(), ELK(), ELKNode(), _getPrototypeOf() (+6 more)

### Community 98 - "Host Collector Parser"
Cohesion: 0.18
Nodes (16): $A(), Ehb(), Fed(), ged(), IA(), jA(), KA(), ked() (+8 more)

### Community 99 - "ELK Layout Engine"
Cohesion: 0.13
Nodes (16): c2d(), FBb(), Gc(), gIb(), _hc(), kib(), LBb(), le() (+8 more)

### Community 100 - "ELK Layout Engine"
Cohesion: 0.14
Nodes (16): CLb(), GLb(), gNb(), lmd(), lUd(), pwd(), Sy(), tmb() (+8 more)

### Community 101 - "Cytoscape Graph Library"
Cohesion: 0.19
Nodes (15): Be(), Ce(), e(), h(), Il(), t(), ms(), Oi() (+7 more)

### Community 102 - "ELK Layout Engine"
Cohesion: 0.14
Nodes (15): aA(), bA(), CA(), eA(), fA(), GA(), GFb(), hA() (+7 more)

### Community 103 - "ELK Layout Engine"
Cohesion: 0.17
Nodes (15): aKb(), bKb(), ekb(), Fmb(), gMb(), hLb(), Ikb(), jkb() (+7 more)

### Community 104 - "Ssh Components"
Cohesion: 0.14
Nodes (5): DevOpsAgent.Infrastructure.Ssh, SshConnectionOptions, ConnectionTimeoutSeconds, KeepAliveSeconds, KnownHostsFile

### Community 105 - "Docker Collector Parser"
Cohesion: 0.38
Nodes (4): DockerNetworkContainer, DockerNetworkInfo, DockerCollectorParser, JsonElement

### Community 106 - "Ssh Config Parser"
Cohesion: 0.23
Nodes (6): HostOptions, Values, SshConfigEntry, SshConfigParser, SshConfigParseResult, SshConfigParserTests

### Community 107 - "Chat Conversation"
Cohesion: 0.23
Nodes (8): applyStyles(), clearTrace(), highlightTrace(), init(), runLayout(), setData(), setTheme(), tokens()

### Community 108 - "Agent Chat"
Cohesion: 0.23
Nodes (13): _1b(), e9b(), _ec(), EJb(), F1b(), h1b(), Jac(), MJb() (+5 more)

### Community 109 - "ELK Layout Engine"
Cohesion: 0.17
Nodes (13): ape(), Bne(), bpe(), Cne(), jze(), mxe(), Mze(), nxe() (+5 more)

### Community 110 - "ELK Layout Engine"
Cohesion: 0.18
Nodes (13): K5c(), k6c(), k7c(), l6c(), M6c(), m7c(), n6c(), n7c() (+5 more)

### Community 111 - "Pages Alerts"
Cohesion: 0.18
Nodes (9): route:/alerts, ChangeStateAsync, LoadAsync, OnInitializedAsync, AlertDto, EmptyState, PageTitle, SetFilter (+1 more)

### Community 112 - "Docker Compose Configuration"
Cohesion: 0.25
Nodes (3): DevOpsAgent.Infrastructure.Security, DataProtectionSecretProtector, SecretProtectorTests

### Community 113 - "Servers Components"
Cohesion: 0.31
Nodes (11): B3b(), bvd(), cVd(), dvd(), fgb(), Fvd(), Svd(), Xnb() (+3 more)

### Community 114 - "Pages Home"
Cohesion: 0.20
Nodes (9): route:/, OpenServers, OpenSettings, Badge, Card, EmptyState, PageTitle, Segmented (+1 more)

### Community 115 - "Graph Trace Resolver"
Cohesion: 0.29
Nodes (3): GraphTracePath, GraphTraceResolver, GraphElementDto

### Community 116 - "Launch Settings"
Cohesion: 0.20
Nodes (9): ASPNETCORE_ENVIRONMENT, applicationUrl, commandName, dotnetRunMessages, environmentVariables, launchBrowser, profiles, http (+1 more)

### Community 117 - "Graph Node Kind"
Cohesion: 0.20
Nodes (10): Al(), Dl(), l(), _ac(), cbc(), fbc(), ibc(), oac() (+2 more)

### Community 118 - "ELK Layout Engine"
Cohesion: 0.20
Nodes (10): eQb(), FOb(), fQb(), hPb(), kPb(), sob(), tPb(), uOb() (+2 more)

### Community 119 - "Home Components"
Cohesion: 0.22
Nodes (6): Dispose, OnAfterRenderAsync, OnInitialized, OnLocationChanged, Icon, ToggleTheme

### Community 120 - "Graph Node Kind"
Cohesion: 0.25
Nodes (7): GraphNodeKind, Container, Domain, Host, Service, Subnet, Unknown

### Community 121 - "Cytoscape Graph Library"
Cohesion: 0.25
Nodes (8): au(), cu(), a(), iu(), lu(), ou(), su(), uu()

### Community 122 - "Cytoscape Graph Library"
Cohesion: 0.25
Nodes (6): d(), kc(), nf(), pl(), rf(), yc()

### Community 123 - "ELK Layout Engine"
Cohesion: 0.29
Nodes (8): Ake(), Kme(), Lle(), Nme(), ole(), Tke(), Uke(), zke()

### Community 124 - "ELK Layout Engine"
Cohesion: 0.29
Nodes (8): aqb(), bx(), Dy(), hj(), hx(), Ix(), Pm(), zm()

### Community 125 - "ELK Layout Engine"
Cohesion: 0.29
Nodes (8): Qz(), Rz(), Sz(), Tz(), Uz(), Wz(), $z(), Zz()

### Community 127 - "Top Bar"
Cohesion: 0.29
Nodes (7): DiscoveryProgressStage, CollectorCompleted, CollectorStarted, Completed, ServerCompleted, ServerStarted, Started

### Community 128 - "Discovery Snapshot"
Cohesion: 0.29
Nodes (6): SnapshotStatus, Cancelled, Completed, Failed, Partial, Running

### Community 129 - "App Setting"
Cohesion: 0.33
Nodes (4): DevOpsAgent.Domain.Settings, AppSetting, Key, Value

### Community 130 - "Graph Edge Kind"
Cohesion: 0.33
Nodes (5): GraphEdgeKind, Declared, Inferred, NetworkAttachment, Observed

### Community 131 - "Json Tool Protocol"
Cohesion: 0.33
Nodes (6): Rpc(), Tpc(), UHc(), Vpc(), wpc(), YHc()

### Community 132 - "Nginx Configuration"
Cohesion: 0.33
Nodes (6): nginx configuration dump fixture, nginx upstream app_backend, nginx upstream target /run/app/backend.sock, nginx upstream target 10.20.0.11:8080, nginx upstream postgres_cluster, nginx PostgreSQL targets 10.30.0.21:5432 and 10.30.0.22:5432

### Community 133 - "Fixtures Components"
Cohesion: 0.33
Nodes (6): Listening socket fixture, Socket fixture: sshd on 22, nginx on 80, PostgreSQL on 5432, systemd nginx details fixture, systemd nginx MainPID 220, systemd sshd details fixture, systemd sshd MainPID 1000

### Community 135 - "And Ssh Authorized"
Cohesion: 0.40
Nodes (5): Authorized SSH Targets and Pinned Host Keys, T1.2 SSH Pool and Runner, T2.7 Topology Page, T3.3 Evidence Panel and Trace, T6.5 Updates

### Community 136 - "Graph Edge Kind"
Cohesion: 0.40
Nodes (4): FocusOnNavigate, Found, Router, RouteView

### Community 137 - "Nginx Collector Parser"
Cohesion: 0.40
Nodes (5): TokenKind, CloseBrace, OpenBrace, Semicolon, Word

### Community 138 - "ELK Layout Engine"
Cohesion: 0.40
Nodes (5): CXc(), FXc(), gXc(), Iid(), lXc()

### Community 139 - "App Setting"
Cohesion: 0.40
Nodes (5): gob(), ipb(), jxb(), qpb(), Rob()

### Community 140 - "And T5.3 Alert"
Cohesion: 0.67
Nodes (4): T5.3 Alert Engine and Alerts Page, T6.3 Agent Chat with Tools, T6.4 SSH Attack Detection and Bans, T6.6 MVP Hardening Pass

### Community 141 - "Components App"
Cohesion: 0.50
Nodes (3): HeadOutlet, ImportMap, Routes

### Community 143 - "Persistence Options"
Cohesion: 0.50
Nodes (3): PersistenceOptions, ConnectionString, DataProtectionKeysDirectory

### Community 144 - "Js Theme"
Cohesion: 0.83
Nodes (3): apply(), current(), toggle()

### Community 146 - "Configured Telegram Ntfy"
Cohesion: 0.67
Nodes (3): Configured Telegram or ntfy Destination, Progress Handoff, T5.4 Notifications

### Community 155 - "Logout Components"
Cohesion: 0.67
Nodes (3): JetBrains Mono SIL Open Font License, Plus Jakarta Sans SIL Open Font License, Font Licenses Index

## Knowledge Gaps
- **533 isolated node(s):** `CreatedAtUtc`, `Id`, `Messages`, `Title`, `UpdatedAtUtc` (+528 more)
  These have ≤1 connection - possible missing edges or undocumented components. (Counts symbols only; 1151 node(s) total have ≤1 connection when file, concept and rationale nodes are included.)
- **60 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `Guid` connect `Unit Test1` to `App Db Context`, `Execution Policy`, `Llm Provider`, `Pages Components`, `Llm Components`, `Migrations Components`, `20261113191218 Initial Create Designer`, `Tool Registry`, `Servers Components`, `Discovery Components`, `Collector Run`, `Tool Registry`?**
  _High betweenness centrality (0.021) - this node is a cross-community bridge._
- **What connects `CreatedAtUtc`, `Id`, `Messages` to the rest of the system?**
  _533 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `ELK Layout Engine` be split into smaller, more focused modules?**
  _Cohesion score 0.005200508494163873 - nodes in this community are weakly interconnected._
- **Why does `xh()` connect `Cytoscape Graph Library` to `ELK Layout Engine`?**
  _High betweenness centrality (0.007) - this node is a cross-community bridge._
- **Should `App Db Context` be split into smaller, more focused modules?**
  _Cohesion score 0.033515881708652794 - nodes in this community are weakly interconnected._
- **Why does `ChatService` connect `Chat Components` to `Execution Policy`, `Persistence Components`, `Unit Test1`, `Web UI Components`, `Tool Registry`, `Chat Components`?**
  _High betweenness centrality (0.007) - this node is a cross-community bridge._
- **Should `ELK Layout Engine` be split into smaller, more focused modules?**
  _Cohesion score 0.030414746543778803 - nodes in this community are weakly interconnected._