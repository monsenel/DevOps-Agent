# Graph Report - DevOps-Agent  (2026-11-14)

## Corpus Check
- 30 files · ~32,653 words
- Verdict: corpus is large enough that graph structure adds value.

## Summary
- 869 nodes · 1687 edges · 81 communities (34 shown, 47 thin omitted)
- Extraction: 88% EXTRACTED · 12% INFERRED · 0% AMBIGUOUS · INFERRED: 198 edges (avg confidence: 0.83)
- Token cost: 2,438 input · 1,527 output

## Community Hubs (Navigation)
- Application Architecture
- Validation and Test Types
- Web Pages and Services
- Chat Persistence and Migrations
- Authentication and Authorization
- Core Product Architecture
- SSH Connections and Errors
- Shared Framework Types
- EF Core Data Model
- Blazor Forms and Validation
- Infrastructure Services
- Docker Compose Configuration
- Server Inventory Contracts
- LLM Provider Domain
- SSH Connection Pool
- LLM Provider Repository
- UI Components and Routes
- SSH Config Parsing
- Solution Dependencies
- LLM Infrastructure Contracts
- OpenAI Compatible Chat
- LLM Provider Management
- Agent Chat Flow
- Data Protection Secrets
- Secret Protection Tests
- SSH Runner and Chat DTOs
- HTTP Model Listing
- Pinned SSH Host Keys
- Dashboard Navigation
- Development Launch Settings
- HTTP Provider Tests
- Provider Types and Endpoints
- App Settings Persistence
- Blazor Routing
- Application Composition
- Action Log Placeholder
- Alerts Placeholder
- Topology Placeholder
- Updates Placeholder
- LLM Test Results
- Error Page
- Theme Management
- Application Shell
- Collector Parser Plan
- Login Flow
- Logout Flow
- Sidebar Navigation
- Placeholder UI Components
- Proposed Action UI
- Stat Tile UI
- Docker Entrypoint
- Form Field Component
- Select Component
- Switch Component
- Implementation Roadmap
- Version Two Backlog
- Collector Framework
- Favicon Asset

## God Nodes (most connected - your core abstractions)
1. `LlmProvider` - 46 edges
2. `Server` - 41 edges
3. `AppDbContext` - 27 edges
4. `DevOpsAgent.Application.Abstractions` - 24 edges
5. `ServerService` - 23 edges
6. `SshConnectionPool` - 21 edges
7. `ChatConversation` - 20 edges
8. `ProviderService` - 19 edges
9. `DevOpsAgent.Infrastructure.Persistence` - 18 edges
10. `ServerRepository` - 16 edges

## Surprising Connections (you probably didn't know these)
- `Collectors and Deterministic Parsing` --semantically_similar_to--> `Deterministic Collectors and Parsers`  [INFERRED] [semantically similar]
  docs/ARCHITECTURE.md → AGENTS.md
- `JetBrains Mono SIL Open Font License` --semantically_similar_to--> `Self-Hosted UI Fonts`  [INFERRED] [semantically similar]
  src/DevOpsAgent.Web/wwwroot/fonts/LICENSE-JetBrainsMono.txt → docs/DESIGN.md
- `Plus Jakarta Sans SIL Open Font License` --semantically_similar_to--> `Self-Hosted UI Fonts`  [INFERRED] [semantically similar]
  src/DevOpsAgent.Web/wwwroot/fonts/LICENSE-PlusJakartaSans.txt → docs/DESIGN.md
- `Font Licenses Index` --semantically_similar_to--> `Self-Hosted UI Fonts`  [INFERRED] [semantically similar]
  src/DevOpsAgent.Web/wwwroot/fonts/LICENSES.md → docs/DESIGN.md
- `UnusedChatClientFactory` --implements--> `IChatClientFactory`  [EXTRACTED]
  tests/DevOpsAgent.Tests/LlmProviderTests.cs → src/DevOpsAgent.Application/Abstractions/IChatClientFactory.cs

## Import Cycles
- None detected.

## Hyperedges (group relationships)
- **Components of the DevOps Agent Solution** — docs_architecture_clean_architecture_dependency_rule, docs_architecture_ssh_connection_pool_and_jump_host, docs_architecture_collectors_and_deterministic_parsing, docs_architecture_graph_building_and_edge_resolution, docs_architecture_llm_tool_calling_and_approval, docs_architecture_sqlite_data_model, docs_architecture_background_services [EXTRACTED 1.00]

## Communities (81 total, 47 thin omitted)

### Community 0 - "Application Architecture"
Cohesion: 0.06
Nodes (14): DevOpsAgent.Domain.Chat, DevOpsAgent.Application.Servers, DevOpsAgent.Infrastructure.Persistence, DevOpsAgent.Domain.Servers, DevOpsAgent.Infrastructure.Migrations, DevOpsAgent.Infrastructure.Llm, DevOpsAgent.Tests, DevOpsAgent.Infrastructure.Ssh (+6 more)

### Community 1 - "Validation and Test Types"
Cohesion: 0.08
Nodes (16): IServerKeyStore, ISshRunner, ServerCapabilityService, ServerInput, ServerSummary, ServerService, ServerRepository, ServerInventoryTests (+8 more)

### Community 2 - "Web Pages and Services"
Cohesion: 0.05
Nodes (28): DevOpsAgent.Web.Pages, DevOpsAgent.Web.Authentication, DevOpsAgent.Application.Chat, DevOpsAgent.Application.Llm, DevOpsAgent.Web, DevOpsAgent.Web.Components, DevOpsAgent.Web.Components.Layout, DevOpsAgent.Web.Components.UI (+20 more)

### Community 3 - "Chat Persistence and Migrations"
Cohesion: 0.07
Nodes (15): DevOpsAgent.Application.Chat, ChatConversationSummary, ChatMessageRecord, Content, Conversation, ConversationId, CreatedAtUtc, Id (+7 more)

### Community 4 - "Authentication and Authorization"
Cohesion: 0.05
Nodes (33): AntiforgeryToken, Authorized, AuthorizeView, NavLink, route:/settings, route:/settings/llm, Dispose, ModelStatus (+25 more)

### Community 5 - "Core Product Architecture"
Cohesion: 0.07
Nodes (32): DevOps Agent, Background Services, Clean Architecture Dependency Rule, Collectors and Deterministic Parsing, DevOps Agent Architecture, Graph Building and Edge Resolution, LLM Tool Calling and Approval, SQLite Data Model (+24 more)

### Community 6 - "SSH Connections and Errors"
Cohesion: 0.08
Nodes (17): IDisposable, ISshConnectionPool, RemoteCommandResult, SshHostKeyMismatchException, Fingerprint, Host, Port, SshRunOptions (+9 more)

### Community 7 - "Shared Framework Types"
Cohesion: 0.12
Nodes (9): IChatConversationRepository, ChatService, ChatConversation, CreatedAtUtc, Id, Messages, Title, UpdatedAtUtc (+1 more)

### Community 8 - "EF Core Data Model"
Cohesion: 0.10
Nodes (12): AppDbContext, AppSettings, ChatConversations, ChatMessages, LlmProviders, Servers, SqliteTestDatabase, Context (+4 more)

### Community 9 - "Blazor Forms and Validation"
Cohesion: 0.07
Nodes (26): DataAnnotationsValidator, EditForm, InputNumber, InputSelect, InputText, PageTitle, route:/servers, AskDelete (+18 more)

### Community 10 - "Infrastructure Services"
Cohesion: 0.08
Nodes (8): PersistenceServiceCollectionExtensions, ServerInventoryOptions, KeyDirectory, ServerKeyStore, SshConnectionOptions, ConnectionTimeoutSeconds, KeepAliveSeconds, KnownHostsFile

### Community 11 - "Docker Compose Configuration"
Cohesion: 0.08
Nodes (25): build, env_file, environment, ports, restart, user, volumes, context (+17 more)

### Community 12 - "Server Inventory Contracts"
Cohesion: 0.10
Nodes (16): IServerRepository, Server, CreatedAtUtc, Group, Host, Id, JumpServerId, KeyFile (+8 more)

### Community 13 - "LLM Provider Domain"
Cohesion: 0.09
Nodes (21): LlmProvider, BaseUrl, ContextWindow, CreatedAtUtc, Id, IsDefault, LastTestedAtUtc, LastTestError (+13 more)

### Community 14 - "SSH Connection Pool"
Cohesion: 0.16
Nodes (5): ISshCommandSession, IsConnected, SshCommandSession, IsConnected, SshConnectionPool

### Community 16 - "UI Components and Routes"
Cohesion: 0.12
Nodes (15): AlertRow, ProgressBar, ProposedActionCard, route:/dev/components, Badge, Card, CodeBlock, EmptyState (+7 more)

### Community 17 - "SSH Config Parsing"
Cohesion: 0.19
Nodes (6): HostOptions, Values, SshConfigEntry, SshConfigParser, SshConfigParseResult, SshConfigParserTests

### Community 18 - "Solution Dependencies"
Cohesion: 0.19
Nodes (9): coverlet.collector, Microsoft.EntityFrameworkCore.Design, Microsoft.EntityFrameworkCore.Sqlite, Microsoft.Extensions.AI.OpenAI, Microsoft.NET.Test.Sdk, SSH.NET, xunit, xunit.runner.visualstudio (+1 more)

### Community 19 - "LLM Infrastructure Contracts"
Cohesion: 0.18
Nodes (6): DevOpsAgent.Web, DevOpsAgent.Application.Llm, DevOpsAgent.Domain.Llm, LlmProviderSummary, ProviderStatusState, DefaultProvider

### Community 22 - "Agent Chat Flow"
Cohesion: 0.18
Nodes (9): route:/agent-chat, CreateConversationAsync, LoadConversationsAsync, OnInitializedAsync, ChatConversationSummary, ChatMessageView, PageTitle, SelectConversationAsync (+1 more)

### Community 23 - "Data Protection Secrets"
Cohesion: 0.25
Nodes (3): DevOpsAgent.Infrastructure.Security, DataProtectionSecretProtector, SecretProtectorTests

### Community 24 - "Secret Protection Tests"
Cohesion: 0.29
Nodes (5): ISecretProtector, LlmProviderTests, TestSecretProtector, UnusedChatClientFactory, UnusedModelLister

### Community 28 - "Dashboard Navigation"
Cohesion: 0.20
Nodes (9): route:/, OpenServers, OpenSettings, Badge, Card, EmptyState, PageTitle, Segmented (+1 more)

### Community 29 - "Development Launch Settings"
Cohesion: 0.20
Nodes (9): ASPNETCORE_ENVIRONMENT, applicationUrl, commandName, dotnetRunMessages, environmentVariables, launchBrowser, profiles, http (+1 more)

### Community 32 - "Provider Types and Endpoints"
Cohesion: 0.33
Nodes (5): ProviderType, LmStudio, Ollama, OpenAiCompatible, OpenRouter

### Community 33 - "App Settings Persistence"
Cohesion: 0.40
Nodes (4): DevOpsAgent.Domain.Settings, AppSetting, Key, Value

### Community 34 - "Blazor Routing"
Cohesion: 0.40
Nodes (4): FocusOnNavigate, Found, Router, RouteView

### Community 35 - "Application Composition"
Cohesion: 0.50
Nodes (3): HeadOutlet, ImportMap, Routes

### Community 36 - "Action Log Placeholder"
Cohesion: 0.50
Nodes (3): route:/action-log, PagePlaceholder, PageTitle

### Community 37 - "Alerts Placeholder"
Cohesion: 0.50
Nodes (3): route:/alerts, PagePlaceholder, PageTitle

### Community 38 - "Topology Placeholder"
Cohesion: 0.50
Nodes (3): route:/topology, PagePlaceholder, PageTitle

### Community 39 - "Updates Placeholder"
Cohesion: 0.50
Nodes (3): route:/updates, PagePlaceholder, PageTitle

### Community 41 - "Error Page"
Cohesion: 0.50
Nodes (3): OnInitialized, PageTitle, System.Diagnostics

### Community 42 - "Theme Management"
Cohesion: 0.83
Nodes (3): apply(), current(), toggle()

## Knowledge Gaps
- **197 isolated node(s):** `CreatedAtUtc`, `Id`, `Messages`, `Title`, `UpdatedAtUtc` (+192 more)
  These have ≤1 connection - possible missing edges or undocumented components. (Counts symbols only; 386 node(s) total have ≤1 connection when file, concept and rationale nodes are included.)
- **47 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `IDisposable` connect `SSH Connections and Errors` to `Authentication and Authorization`?**
  _High betweenness centrality (0.061) - this node is a cross-community bridge._
- **Are the 2 inferred relationships involving `LlmProvider` (e.g. with `ProviderService.CreateAsync()` and `LlmProviderTests.TestAsync_ListsModelsStreamsAndDetectsToolCallingUsingHttpHandler()`) actually correct?**
  _`LlmProvider` has 2 INFERRED edges - model-reasoned connections that need verification._
- **What connects `CreatedAtUtc`, `Id`, `Messages` to the rest of the system?**
  _197 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `Application Architecture` be split into smaller, more focused modules?**
  _Cohesion score 0.05669710806697108 - nodes in this community are weakly interconnected._
- **Why does `SshConnectionPool` connect `SSH Connection Pool` to `Application Architecture`, `Validation and Test Types`, `SSH Connections and Errors`, `Infrastructure Services`, `Server Inventory Contracts`, `SSH Runner and Chat DTOs`?**
  _High betweenness centrality (0.052) - this node is a cross-community bridge._
- **Should `Validation and Test Types` be split into smaller, more focused modules?**
  _Cohesion score 0.07660455486542443 - nodes in this community are weakly interconnected._
- **Why does `LlmProvider` connect `LLM Provider Domain` to `Provider Types and Endpoints`, `Chat Persistence and Migrations`, `Shared Framework Types`, `LLM Test Results`, `LLM Provider Repository`, `LLM Infrastructure Contracts`, `OpenAI Compatible Chat`, `LLM Provider Management`, `SSH Runner and Chat DTOs`, `HTTP Model Listing`?**
  _High betweenness centrality (0.050) - this node is a cross-community bridge._

Graphify graph, report, and incremental cache are tracked in the repository. Local runtime path metadata is ignored.
