using System.Text;
using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Agent;
using DevOpsAgent.Application.Chat;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Application.Search;
using DevOpsAgent.Domain.Agent;
using DevOpsAgent.Domain.Chat;
using DevOpsAgent.Domain.Discovery;
using DevOpsAgent.Domain.Graph;
using DevOpsAgent.Domain.Llm;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace DevOpsAgent.Tests;

/// <summary>T7.R30: redaction gaps, image redaction guard, attachment GIF & replay budget, impact accuracy, global search hardening.</summary>
public sealed class T7R30HardeningTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
    private static readonly byte[] ValidGif89a = [0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x3B];
    private static readonly byte[] ValidGif87a = [0x47, 0x49, 0x46, 0x38, 0x37, 0x61, 0x02, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x3B];

    [Theory]
    [InlineData("dbpass=super-secret-pw", "super-secret-pw")]
    [InlineData("mysqlpass=my-db-secret", "my-db-secret")]
    [InlineData("AWS key AKIAIOSFODNN7EXAMPLE leaked in log", "AKIAIOSFODNN7EXAMPLE")]
    [InlineData("GitHub token ghp_aBcDeFgHiJkLmNoPqRsTuVwXyZ1234 in output", "ghp_aBcDeFgHiJkLmNoPqRsTuVwXyZ1234")]
    [InlineData("Slack xoxb-1234567890-abcdef123456 posted", "xoxb-1234567890-abcdef123456")]
    [InlineData("jwt eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.signatureToken12345 here", "signatureToken12345")]
    [InlineData("proxy sent Basic dXNlcjpwYXNz to upstream", "dXNlcjpwYXNz")]
    [InlineData("header Basic dXNlcjpwYXNzd29yZA== logged", "dXNlcjpwYXNzd29yZA==")]
    [InlineData("mysql --password hunter2secret -u root", "hunter2secret")]
    [InlineData("cli --password \"space secret value\" --host db1", "space secret value")]
    [InlineData("curl --api-key sk_live_abcdef123456 https://api.example", "sk_live_abcdef123456")]
    public void Redactor_CoversAllT7R30SecretPatterns(string input, string secret)
    {
        var redacted = ChatContentRedactor.Redact(input);

        Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redactor_MasksTruncatedPemWithoutEndLine_AndPreservesTrailingNonKeyLines()
    {
        var input = "-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjEAAAAABG5vbmU=\nMIIEpAIBAAKCAQEAtokenBody\nservice nginx running";

        var redacted = ChatContentRedactor.Redact(input);

        Assert.DoesNotContain("b3BlbnNzaC1rZXktdjE", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("MIIEpAIBAAKCAQEAtokenBody", redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED PRIVATE KEY]", redacted, StringComparison.Ordinal);
        Assert.Contains("service nginx running", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Redactor_MasksSecretsLongerThan4096CharactersCompletely()
    {
        var tail = "TRAILING_SECRET_MARKER_987654321";
        var longSecret = new string('x', 5000) + tail;

        var quoted = ChatContentRedactor.Redact($"password=\"{longSecret}\"");
        var bare = ChatContentRedactor.Redact($"DB_PASSWORD={longSecret}");
        var bearer = ChatContentRedactor.Redact($"Bearer {longSecret}");
        var longPem = ChatContentRedactor.Redact($"-----BEGIN PRIVATE KEY-----\n{new string('A', 20_000)}{tail}\n-----END PRIVATE KEY-----");

        Assert.DoesNotContain(tail, quoted, StringComparison.Ordinal);
        Assert.DoesNotContain(tail, bare, StringComparison.Ordinal);
        Assert.DoesNotContain(tail, bearer, StringComparison.Ordinal);
        Assert.DoesNotContain(tail, longPem, StringComparison.Ordinal);
    }

    [Fact]
    public void Redactor_MasksMultiWordUnquotedPasswordWithoutBreakingAdjacentAssignments()
    {
        var multiWord = ChatContentRedactor.Redact("DB_PASSWORD=alpha beta gamma");
        Assert.Equal("DB_PASSWORD=[REDACTED]", multiWord);
        Assert.DoesNotContain("beta", multiWord, StringComparison.Ordinal);
        Assert.DoesNotContain("gamma", multiWord, StringComparison.Ordinal);

        var adjacent = ChatContentRedactor.Redact("DB_PASSWORD=s3cr3t host=db1 port=5432");
        Assert.Equal("DB_PASSWORD=[REDACTED] host=db1 port=5432", adjacent);
    }

    [Fact]
    public void Redactor_DoesNotFalsePositiveOnBasicProseOrCompassBypass()
    {
        const string prose = "Basic monitoring is enabled; compass=north bypass=true";
        Assert.Equal(prose, ChatContentRedactor.Redact(prose));
    }

    [Fact]
    public void Attachments_DistinguishesRealGif87a89aFromTextStartingWithGif8()
    {
        var textStartingWithGif8 = new ChatAttachmentInput("notes.txt", "text/plain", Encoding.UTF8.GetBytes("GIF8 is an old image format prefix."));
        var textStartingWithGif89a = new ChatAttachmentInput("spec.txt", "text/plain", Encoding.UTF8.GetBytes("GIF89a header notes without binary trailer"));

        var checkedText = ChatAttachmentPolicy.Check([textStartingWithGif8, textStartingWithGif89a], imagesAllowed: false);
        Assert.All(checkedText, item => Assert.Equal(ChatAttachmentKind.Text, item.Kind));
        Assert.False(ChatAttachmentPolicy.IsImage(textStartingWithGif8.Data));

        var checkedGifs = ChatAttachmentPolicy.Check([
            new ChatAttachmentInput("anim.gif", "image/gif", ValidGif89a),
            new ChatAttachmentInput("old.gif", "image/gif", ValidGif87a)
        ], imagesAllowed: true);
        Assert.All(checkedGifs, item =>
        {
            Assert.Equal(ChatAttachmentKind.Image, item.Kind);
            Assert.Equal("image/gif", item.ContentType);
        });
    }

    [Fact]
    public async Task ChatService_RefusesImageOnNonLocalRedactingProvider_UntilExplicitlyConfirmed()
    {
        await using var cloud = await ChatServiceHarness.CreateAsync(
            baseUrl: "https://openrouter.ai/api/v1",
            capabilities: new FixedCapabilities(false, true),
            redactSecrets: true);
        var conversationId = await cloud.Service.CreateAsync(CancellationToken.None);

        Assert.True(await cloud.Service.RequiresImageRedactionConfirmationAsync(CancellationToken.None));

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            cloud.SendAsync(conversationId, "inspect screenshot", [new("screen.png", "image/png", Png)], allowUnredactedImages: false));
        Assert.Contains("Allow unredacted image upload", error.Message, StringComparison.Ordinal);
        Assert.Empty(await cloud.Context.ChatMessages.AsNoTracking().ToListAsync());

        // Explicit per-message confirmation allows the image upload.
        await cloud.SendAsync(conversationId, "inspect screenshot", [new("screen.png", "image/png", Png)], allowUnredactedImages: true);
        Assert.Single(cloud.Requests[0].SelectMany(message => message.Contents).OfType<DataContent>());

        // A follow-up turn without re-ticking the confirmation must not replay the earlier unredacted image to the cloud.
        await cloud.SendAsync(conversationId, "follow up question", allowUnredactedImages: false);
        Assert.Empty(cloud.Requests[1].SelectMany(message => message.Contents).OfType<DataContent>());
        Assert.Contains("secret redaction is enabled", string.Join("\n", cloud.Requests[1].Select(m => m.Text)), StringComparison.Ordinal);

        // Local providers do not require the redaction confirmation checkbox.
        await using var local = await ChatServiceHarness.CreateAsync(
            baseUrl: "http://127.0.0.1:11434/v1",
            type: ProviderType.Ollama,
            capabilities: new FixedCapabilities(false, true),
            redactSecrets: true);
        Assert.False(await local.Service.RequiresImageRedactionConfirmationAsync(CancellationToken.None));
        var localConv = await local.Service.CreateAsync(CancellationToken.None);
        await local.SendAsync(localConv, "inspect locally", [new("screen.png", "image/png", Png)], allowUnredactedImages: false);
        Assert.Single(local.Requests[0].SelectMany(message => message.Contents).OfType<DataContent>());
    }

    [Fact]
    public async Task ChatService_CapsReplayedAttachmentBudgetAcrossTurns()
    {
        await using var chat = await ChatServiceHarness.CreateAsync();
        var conversationId = await chat.Service.CreateAsync(CancellationToken.None);

        // Each attachment is ~1.8 MB; 3 turns = 5.4 MB > MaxReplayedBytes (4 MB).
        var payload1 = "TURN1_MARKER_" + new string('a', 1_800_000);
        var payload2 = "TURN2_MARKER_" + new string('b', 1_800_000);
        var payload3 = "TURN3_MARKER_" + new string('c', 1_800_000);

        await chat.SendAsync(conversationId, "turn 1", [new("first.log", "text/plain", Encoding.UTF8.GetBytes(payload1))]);
        await chat.SendAsync(conversationId, "turn 2", [new("second.log", "text/plain", Encoding.UTF8.GetBytes(payload2))]);
        await chat.SendAsync(conversationId, "turn 3", [new("third.log", "text/plain", Encoding.UTF8.GetBytes(payload3))]);

        var turn3Text = string.Join("\n", chat.Requests[2].Select(message => message.Text));
        Assert.Contains("TURN3_MARKER_", turn3Text, StringComparison.Ordinal);
        Assert.Contains("TURN2_MARKER_", turn3Text, StringComparison.Ordinal);
        Assert.DoesNotContain("TURN1_MARKER_", turn3Text, StringComparison.Ordinal);
        Assert.Contains("first.log", turn3Text, StringComparison.Ordinal);
        Assert.Contains("omitted from replay", turn3Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImpactService_CoversStaleDependentsDomains_InventoryIp_PackageRules_AndHistoricalDisclaimer()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await context.Database.EnsureCreatedAsync();

        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));
        var edge = new Server("srv-edge", "10.88.10.10", 22, "dva", "key", "staging");
        var app = new Server("srv-app", "10.88.20.20", 22, "dva", "key", "staging");
        app.Update("srv-app", "10.88.20.20", 22, "dva", "key", edge.Id, "staging", "[]", null);
        context.Servers.AddRange(edge, app);
        var snapshot = new Snapshot([edge.Id, app.Id], clock.GetUtcNow().AddMinutes(-5));
        snapshot.Complete(clock.GetUtcNow().AddMinutes(-4), 2, 0);
        context.Snapshots.Add(snapshot);
        await context.SaveChangesAsync();

        // Mark edge host and its server block stale to verify dependent and domain staleness warnings.
        var edgeHost = new GraphNode(snapshot.Id, $"host:{edge.Id:N}", GraphNodeKind.Host, "srv-edge", null, edge.Id,
            JsonSerializer.Serialize(new { Addresses = new[] { "10.88.10.10", "172.18.0.10" }, Stale = true, LastSeenUtc = "2026-11-19T10:00:00Z" }));
        var appHost = new GraphNode(snapshot.Id, $"host:{app.Id:N}", GraphNodeKind.Host, "srv-app", null, app.Id,
            JsonSerializer.Serialize(new { Addresses = new[] { "10.88.20.20" } }));
        var edgeNginx = new GraphNode(snapshot.Id, $"service:{edge.Id:N}:process:nginx", GraphNodeKind.Service, "nginx", edgeHost.Id, edge.Id,
            JsonSerializer.Serialize(new { Stale = true, LastSeenUtc = "2026-11-19T10:00:00Z" }));
        var apiBlock = new GraphNode(snapshot.Id, $"service:{edge.Id:N}:nginx-server:/etc/nginx/conf.d/api.conf:1", GraphNodeKind.Service, "server block · api.example.test", edgeNginx.Id, edge.Id,
            JsonSerializer.Serialize(new { FilePath = "/etc/nginx/conf.d/api.conf", Stale = true, LastSeenUtc = "2026-11-19T10:00:00Z" }));
        var apiDomain = new GraphNode(snapshot.Id, "domain:api.example.test", GraphNodeKind.Domain, "api.example.test", null, edge.Id, "{}");
        var apiContainer = new GraphNode(snapshot.Id, $"container:{app.Id:N}:api", GraphNodeKind.Container, "api", appHost.Id, app.Id, "{}");
        // Compose service inside container (must NOT be treated as a host service for apt updates)
        var composePg = new GraphNode(snapshot.Id, $"service:{app.Id:N}:compose:stack/postgres", GraphNodeKind.Service, "postgres", apiContainer.Id, app.Id, "{}");
        // Host services for package matching rules
        var sshUnit = new GraphNode(snapshot.Id, $"service:{app.Id:N}:systemd:ssh.service", GraphNodeKind.Service, "ssh", appHost.Id, app.Id, "{}");
        var nodeProcess = new GraphNode(snapshot.Id, $"service:{app.Id:N}:process:node", GraphNodeKind.Service, "node", appHost.Id, app.Id, "{}");
        var hostPg = new GraphNode(snapshot.Id, $"service:{app.Id:N}:systemd:postgresql.service", GraphNodeKind.Service, "postgresql", appHost.Id, app.Id, "{}");
        var hostMysql = new GraphNode(snapshot.Id, $"service:{app.Id:N}:systemd:mysqld.service", GraphNodeKind.Service, "mysqld", appHost.Id, app.Id, "{}");

        var graphResult = new GraphBuildResult(
            [edgeHost, appHost, edgeNginx, apiBlock, apiDomain, apiContainer, composePg, sshUnit, nodeProcess, hostPg, hostMysql],
            [
                new GraphEdge(snapshot.Id, apiDomain.Id, apiBlock.Id, GraphEdgeKind.Declared),
                new GraphEdge(snapshot.Id, apiBlock.Id, apiContainer.Id, GraphEdgeKind.Declared)
            ],
            [], []);
        await new GraphRepository(context).SaveAsync(snapshot.Id, graphResult, CancellationToken.None);

        var service = new ImpactService(new ServerRepository(context), new SnapshotRepository(context), new GraphRepository(context), clock);

        // 1. Stale dependent + stale domain routing warnings
        var restartImpact = await service.ForAsync("restart_container", """{"container":"api"}""", app.Id, CancellationToken.None);
        var dep = Assert.Single(restartImpact.Dependents);
        Assert.True(dep.IsStale);
        Assert.Contains(restartImpact.Warnings, w => w.Contains("Dependent data", StringComparison.Ordinal) && w.Contains("stale", StringComparison.Ordinal));
        Assert.Contains(restartImpact.Warnings, w => w.Contains("Domain routing for api.example.test", StringComparison.Ordinal) && w.Contains("stale", StringComparison.Ordinal));

        // 2. block_ip / unblock_ip inventory IP warning (including jump host & discovered IP)
        var blockJump = await service.ForAsync("block_ip", """{"address":"10.88.10.10"}""", app.Id, CancellationToken.None);
        Assert.Contains(blockJump.Warnings, w => w.Contains("srv-edge", StringComparison.Ordinal) && w.Contains("jump host", StringComparison.Ordinal));

        var blockDiscovered = await service.ForAsync("block_ip", """{"address":"172.18.0.10"}""", app.Id, CancellationToken.None);
        Assert.Contains(blockDiscovered.Warnings, w => w.Contains("srv-edge", StringComparison.Ordinal));

        var unblockSelf = await service.ForAsync("unblock_ip", """{"address":"10.88.20.20"}""", app.Id, CancellationToken.None);
        Assert.Contains(unblockSelf.Warnings, w => w.Contains("srv-app", StringComparison.Ordinal) && w.Contains("target server itself", StringComparison.Ordinal));

        // 3. Package rules: openssh-server->ssh, nodejs!->node, postgresql-client-*!->postgresql, moby-containerd/moby-runc, mariadb-server->mysqld
        var updateImpact = await service.ForAsync("apply_updates",
            """{"packages":["openssh-server","nodejs","postgresql-client-15","moby-containerd","moby-runc","mariadb-server"]}""",
            app.Id, CancellationToken.None);
        var pkgMap = updateImpact.Packages!.ToDictionary(p => p.Package);
        Assert.Equal("restarts ssh", pkgMap["openssh-server"].Effect);
        Assert.Equal("no service in the graph", pkgMap["nodejs"].Effect);
        Assert.Equal("no service in the graph", pkgMap["postgresql-client-15"].Effect);
        Assert.True(pkgMap["moby-containerd"].RestartsDockerOrNginx);
        Assert.Equal("Docker", pkgMap["moby-containerd"].Effect);
        Assert.True(pkgMap["moby-runc"].RestartsDockerOrNginx);
        Assert.Equal("Docker", pkgMap["moby-runc"].Effect);
        Assert.Equal("restarts mysqld", pkgMap["mariadb-server"].Effect);

        // Compose service alone (without a host postgresql unit) must NOT be matched by host postgresql-15 package
        var composeOnlyNodes = new[] { appHost, apiContainer, composePg };
        var composeOnlyImpact = ImpactService.PackageImpacts(["postgresql-15"], composeOnlyNodes, graphResult, "srv-app").Single();
        Assert.Equal("no service in the graph", composeOnlyImpact.Effect);

        // 4. Historical graph disclaimer when action predates the current snapshot
        var oldAction = new ActionRecord("restart_container", "staging",
            JsonSerializer.Serialize(new[] { app.Id }), """{"container":"api"}""",
            null, clock.GetUtcNow().AddHours(-2));
        var historicalPreview = Assert.Single(await service.ForActionAsync(oldAction, CancellationToken.None));
        Assert.Contains(historicalPreview.Warnings, w => w.Contains("current discovery graph, not the graph from when the action was proposed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GlobalSearch_MarksStaleResults_CapsQueryLength_AndAvoidsLoadingEdgesAndEvidence()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await context.Database.EnsureCreatedAsync();

        var srv = new Server("srv-stale", "10.88.99.9", 22, "dva", "key", "prod");
        var live = new Server("srv-live", "10.88.99.10", 22, "dva", "key", "prod");
        context.Servers.AddRange(srv, live);
        var snapshot = new Snapshot([srv.Id, live.Id], DateTimeOffset.UtcNow.AddMinutes(-5));
        snapshot.Complete(DateTimeOffset.UtcNow, 1, 1);
        context.Snapshots.Add(snapshot);
        await context.SaveChangesAsync();

        var host = new GraphNode(snapshot.Id, $"host:{srv.Id:N}", GraphNodeKind.Host, "srv-stale", null, srv.Id,
            JsonSerializer.Serialize(new { Addresses = new[] { "10.88.99.9" }, Stale = true, LastSeenUtc = "2026-11-18T08:00:00Z" }));
        var container = new GraphNode(snapshot.Id, $"container:{srv.Id:N}:cache", GraphNodeKind.Container, "redis-cache", host.Id, srv.Id,
            JsonSerializer.Serialize(new { Stale = true, LastSeenUtc = "2026-11-18T08:00:00Z" }));
        var listener = new GraphListener(snapshot.Id, container.Id, "0.0.0.0", 6379, "tcp", "redis-server", 10);
        await new GraphRepository(context).SaveAsync(snapshot.Id, new GraphBuildResult([host, container], [], [listener], []), CancellationToken.None);

        var spyGraphRepo = new SpyGraphSearchRepository(new GraphRepository(context));
        var search = new GlobalSearchService(new ServerRepository(context), new SnapshotRepository(context), spyGraphRepo, new AlertRepository(context));

        var nodeResults = await search.SearchAsync("redis-cache", CancellationToken.None);
        var nodeHit = Assert.Single(Assert.Single(nodeResults, g => g.Kind == SearchResultKind.Node).Items);
        Assert.True(nodeHit.IsStale);
        Assert.Contains("stale", nodeHit.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2026-11-18T08:00:00Z", nodeHit.Detail, StringComparison.Ordinal);

        var listenerResults = await search.SearchAsync("6379", CancellationToken.None);
        var listenerHit = Assert.Single(Assert.Single(listenerResults, g => g.Kind == SearchResultKind.Listener).Items);
        Assert.True(listenerHit.IsStale);
        Assert.Contains("stale", listenerHit.Detail, StringComparison.OrdinalIgnoreCase);

        var serverResults = await search.SearchAsync("srv-stale", CancellationToken.None);
        var serverHit = Assert.Single(Assert.Single(serverResults, g => g.Kind == SearchResultKind.Server).Items);
        Assert.True(serverHit.IsStale);
        Assert.Contains("stale", serverHit.Detail, StringComparison.OrdinalIgnoreCase);

        // Query length cap: huge query does not throw and returns empty.
        var huge = await search.SearchAsync(new string('z', GlobalSearchService.MaxQueryLength * 10), CancellationToken.None);
        Assert.Empty(huge);

        // Verify lightweight search path was used (edges & evidence never loaded).
        Assert.True(spyGraphRepo.SearchDataCalls >= 4);
        Assert.Equal(0, spyGraphRepo.FullEntityCalls);
    }

    private sealed class SpyGraphSearchRepository(GraphRepository inner) : IGraphRepository, IGraphSearchRepository
    {
        public int FullEntityCalls { get; private set; }
        public int SearchDataCalls { get; private set; }

        public Task SaveAsync(Guid snapshotId, GraphBuildResult graph, CancellationToken cancellationToken) =>
            inner.SaveAsync(snapshotId, graph, cancellationToken);

        public Task MergeAsync(Guid snapshotId, Guid previousSnapshotId, GraphBuildResult graph, CancellationToken cancellationToken) =>
            inner.MergeAsync(snapshotId, previousSnapshotId, graph, cancellationToken);

        public Task<GraphSnapshotDto?> GetAsync(Guid snapshotId, GraphView view, CancellationToken cancellationToken) =>
            inner.GetAsync(snapshotId, view, cancellationToken);

        public Task<GraphBuildResult?> GetEntitiesAsync(Guid snapshotId, CancellationToken cancellationToken)
        {
            FullEntityCalls++;
            return inner.GetEntitiesAsync(snapshotId, cancellationToken);
        }

        public Task<GraphSearchSnapshot?> GetSearchDataAsync(Guid snapshotId, CancellationToken cancellationToken)
        {
            SearchDataCalls++;
            return inner.GetSearchDataAsync(snapshotId, cancellationToken);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
