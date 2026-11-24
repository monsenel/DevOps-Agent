using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Domain.Discovery;
using DevOpsAgent.Domain.Graph;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Infrastructure.Collectors;
using DevOpsAgent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DevOpsAgent.Tests;

public sealed class V2CollectorsTests
{
    private static readonly string[] SecretCanaries =
    [
        "CANARY_PG_SECRET_991",
        "CANARY_REDIS_SECRET_882",
        "CANARY_NEXUS_PASS_773",
        "CANARY_API_KEY_664",
        "CANARY_NPGSQL_PWD_555",
        "CANARY_SE_REDIS_446",
        "CANARY_REDIS_CONF_337",
        "CANARY_MASTER_AUTH_228",
        "CANARY_REPL_PASS_119",
        "CANARY_MAVEN_PASS_101",
        "CANARY_NPM_TOKEN_202",
        "CANARY_PIP_PASS_303"
    ];

    [Fact]
    public void FirewallParser_ParsesUfwFixture()
    {
        var result = FirewallCollectorParser.Parse(Fixture("firewall-ufw.txt"));

        Assert.Equal("ufw", result.Backend);
        Assert.True(result.Enabled);
        Assert.Equal("deny", result.DefaultInbound);
        Assert.Equal("allow", result.DefaultOutbound);
        Assert.Contains(result.Rules, rule => rule.Port == "22" && rule.Action == "limit" && rule.Source == "192.0.2.0/24" && rule.Comment == "admin ssh");
        Assert.Contains(result.Rules, rule => rule.Port == "5432" && rule.Action == "allow" && rule.Source == "10.88.20.0/24");
        Assert.Contains(result.Rules, rule => rule.Port == "3306" && rule.Action == "deny");
    }

    [Fact]
    public void FirewallParser_ParsesNftablesFixture()
    {
        var result = FirewallCollectorParser.Parse(Fixture("firewall-nft.txt"));

        Assert.Equal("nftables", result.Backend);
        Assert.True(result.Enabled);
        Assert.Equal("drop", result.DefaultInbound);
        Assert.Equal("accept", result.DefaultOutbound);
        Assert.Contains(result.Rules, rule => rule.Protocol == "tcp" && rule.Port == "22" && rule.Action == "accept");
        Assert.Contains(result.Rules, rule => rule.Protocol == "tcp" && rule.Port == "443" && rule.Action == "accept");
        Assert.Contains(result.Rules, rule => rule.Protocol == "tcp" && rule.Port == "5432" && rule.Source == "10.88.20.0/24");
        Assert.Contains(result.Rules, rule => rule.Protocol == "udp" && rule.Port == "51820");
    }

    [Fact]
    public void FirewallParser_ParsesIptablesFixture()
    {
        var result = FirewallCollectorParser.Parse(Fixture("firewall-iptables.txt"));

        Assert.Equal("iptables", result.Backend);
        Assert.True(result.Enabled);
        Assert.Equal("drop", result.DefaultInbound);
        Assert.Equal("accept", result.DefaultOutbound);
        Assert.Contains(result.Rules, rule => rule.Protocol == "tcp" && rule.Port == "22" && rule.Action == "accept");
        Assert.Contains(result.Rules, rule => rule.Protocol == "tcp" && rule.Port == "6379" && rule.Source == "10.88.20.0/24");
        Assert.Contains(result.Rules, rule => rule.Protocol == "tcp" && rule.Port == "80,443" && rule.Action == "accept");
    }

    [Fact]
    public void FirewallParser_ParsesFirewalldFixture()
    {
        var result = FirewallCollectorParser.Parse(Fixture("firewall-firewalld.txt"));

        Assert.Equal("firewalld", result.Backend);
        Assert.True(result.Enabled);
        Assert.Contains(result.Rules, rule => rule.Port == "22" && rule.Protocol == "tcp" && rule.Comment == "ssh");
        Assert.Contains(result.Rules, rule => rule.Port == "443" && rule.Protocol == "tcp" && rule.Comment == "https");
        Assert.Contains(result.Rules, rule => rule.Port == "8080" && rule.Protocol == "tcp");
        Assert.Contains(result.Rules, rule => rule.Port == "51820" && rule.Protocol == "udp");
    }

    [Fact]
    public void TlsParser_ParsesOpenSslMetadataAndDetectsSelfSigned()
    {
        var result = TlsCollectorParser.Parse(Fixture("tls-openssl.txt"));

        Assert.Equal(2, result.Certificates.Count);
        var letsEncrypt = result.Certificates.Single(cert => cert.Path.Contains("api.example.test", StringComparison.Ordinal));
        Assert.False(letsEncrypt.IsSelfSigned);
        Assert.Equal("03A1B2C3D4E5F6070809", letsEncrypt.SerialNumber);
        Assert.Contains("api.example.test", letsEncrypt.SubjectAlternativeNames);
        Assert.Contains("www.example.test", letsEncrypt.SubjectAlternativeNames);
        Assert.Contains("192.0.2.10", letsEncrypt.SubjectAlternativeNames);
        Assert.Equal(new DateTimeOffset(2026, 12, 30, 23, 59, 59, TimeSpan.Zero), letsEncrypt.NotAfterUtc);

        var selfSigned = result.Certificates.Single(cert => cert.Path.Contains("internal-selfsigned.crt", StringComparison.Ordinal));
        Assert.True(selfSigned.IsSelfSigned);
        Assert.Contains("internal.local", selfSigned.SubjectAlternativeNames);

        // Collector command must never inspect private keys.
        Assert.Contains("-noout", TlsCollector.Command, StringComparison.Ordinal);
        Assert.Contains("*key*|*priv*", TlsCollector.Command, StringComparison.Ordinal);
    }

    [Fact]
    public void ReverseProxyParser_ParsesHaProxyCaddyAndTraefikFixtures()
    {
        var combined = string.Join('\n',
            Fixture("reverse-proxy-haproxy.cfg"),
            Fixture("reverse-proxy-caddy.txt"),
            Fixture("reverse-proxy-traefik.yml"));

        var result = ReverseProxyCollectorParser.Parse(combined);

        // HAProxy: api_backend (api.example.test), web_backend (app.example.test), pg_tcp (listen)
        var haApi = result.Routes.Single(r => r.ProxyType == "haproxy" && r.BackendName == "api_backend");
        Assert.Contains("api.example.test", haApi.Domains);
        Assert.Equal(443, haApi.ListenPort);
        Assert.Equal("https", haApi.ListenProtocol);
        Assert.Equal(2, haApi.Targets.Count);
        Assert.Contains(haApi.Targets, t => t.Address == "10.88.20.20:5000");

        var haPg = result.Routes.Single(r => r.ProxyType == "haproxy" && r.FrontendName == "pg_tcp");
        Assert.Equal("tcp", haPg.ListenProtocol);
        Assert.Equal(5433, haPg.ListenPort);
        Assert.Contains(haPg.Targets, t => t.Address == "10.88.20.30:5432");

        // Caddy: multi-domain + internal:8080
        var caddyApi = result.Routes.Single(r => r.ProxyType == "caddy" && r.Domains.Contains("api.example.test"));
        Assert.Contains("v2.example.test", caddyApi.Domains);
        Assert.Equal(443, caddyApi.ListenPort);
        Assert.Contains(caddyApi.Targets, t => t.Address == "10.88.20.20:5000");
        Assert.Contains(caddyApi.Targets, t => t.Address == "http://10.88.20.21:5000");

        var caddyInternal = result.Routes.Single(r => r.ProxyType == "caddy" && r.Domains.Contains("internal.example.test"));
        Assert.Equal(8080, caddyInternal.ListenPort);
        Assert.Equal("http", caddyInternal.ListenProtocol);
        Assert.Contains(caddyInternal.Targets, t => t.Address == "10.88.20.20:3000");

        // Traefik: HTTP router + TCP router
        var traefikApi = result.Routes.Single(r => r.ProxyType == "traefik" && r.FrontendName == "api-router");
        Assert.Contains("api.example.test", traefikApi.Domains);
        Assert.Contains("alt.example.test", traefikApi.Domains);
        Assert.Equal("https", traefikApi.ListenProtocol);
        Assert.Equal(2, traefikApi.Targets.Count);

        var traefikPg = result.Routes.Single(r => r.ProxyType == "traefik" && r.FrontendName == "pg-router");
        Assert.Equal("tcp", traefikPg.ListenProtocol);
        Assert.Contains("db.example.test", traefikPg.Domains);
        Assert.Contains(traefikPg.Targets, t => t.Address == "10.88.20.30:5432");
    }

    [Fact]
    public async Task AppConfigCollector_RedactsEveryCredentialBeforeStorageAndParsesPostgresRedisNexus()
    {
        var server = new Server("srv-app", "10.88.20.20", 22, "dva", "key", "staging");
        server.RecordCapabilities(true, "Debian 12", DateTimeOffset.UtcNow);
        var bundle = Fixture("app-config-bundle.txt");
        var runner = new StubSshRunner(command => command == AppConfigCollector.Command ? bundle : string.Empty);
        var collector = new AppConfigCollector();
        var context = new ServerContext(server, runner);

        var output = await collector.CollectAsync(context, CancellationToken.None);
        var parsedResult = collector.Parse(output);

        Assert.True(parsedResult.IsSuccess);
        var parsedJson = JsonSerializer.Serialize(parsedResult.Data);
        var parsed = parsedResult.Data!.Value.Deserialize<AppConfigDiscoveryResult>()!;

        // ZERO plaintext secret canaries may appear anywhere in raw stored output or parsed result!
        foreach (var canary in SecretCanaries)
        {
            Assert.DoesNotContain(canary, output.StandardOutput, StringComparison.Ordinal);
            Assert.DoesNotContain(canary, parsedJson, StringComparison.Ordinal);
        }

        // Verify PostgreSQL, Redis, and Nexus references were all deterministically extracted.
        Assert.Contains(parsed.References, r => r.DependencyType == "postgresql" && r.Host == "10.88.20.30" && r.Port == 5432 && r.DatabaseName == "billing_db");
        Assert.Contains(parsed.References, r => r.DependencyType == "postgresql" && r.Host == "10.88.20.30" && r.Port == 5432 && r.DatabaseName == "orders");
        Assert.Contains(parsed.References, r => r.DependencyType == "postgresql" && r.Host == "10.88.20.31" && r.Port == 5432 && r.SourceKind == "postgres-conf");
        Assert.Contains(parsed.References, r => r.DependencyType == "redis" && r.Host == "10.88.20.30" && r.Port == 6379 && r.SourceKind == "env");
        Assert.Contains(parsed.References, r => r.DependencyType == "redis" && r.Host == "10.88.20.30" && r.Port == 6379 && r.SourceKind == "appsettings");
        Assert.Contains(parsed.References, r => r.DependencyType == "redis" && r.Host == "10.88.20.31" && r.Port == 6379 && r.SourceKind == "redis-conf");
        Assert.Contains(parsed.References, r => r.DependencyType == "nexus" && r.Host == "10.88.20.40" && r.Port == 8081 && r.DatabaseName == "maven-releases");
        Assert.Contains(parsed.References, r => r.DependencyType == "nexus" && r.Host == "10.88.20.40" && r.Port == 8081 && r.SourceKind == "maven" && r.DatabaseName == "maven-public");
        Assert.Contains(parsed.References, r => r.DependencyType == "nexus" && r.Host == "10.88.20.40" && r.Port == 8081 && r.SourceKind == "npm" && r.DatabaseName == "npm-group");
        Assert.Contains(parsed.References, r => r.DependencyType == "nexus" && r.Host == "10.88.20.40" && r.Port == 8081 && r.SourceKind == "pip" && r.DatabaseName == "pypi-all");
    }

    [Fact]
    public void AppConfigParser_ResolvesSplitEnvironmentVariables()
    {
        var input = """
            __DVA_CFG__:env:/opt/worker/.env
            PGHOST=10.88.20.30
            PGPORT=5433
            PGDATABASE=analytics
            PGPASSWORD=CANARY_SPLIT_PASS
            REDIS_HOST=10.88.20.31
            REDIS_PORT=6380
            """;

        var redacted = AppConfigRedactor.Redact(input);
        Assert.DoesNotContain("CANARY_SPLIT_PASS", redacted, StringComparison.Ordinal);

        var parsed = AppConfigCollectorParser.Parse(redacted);
        Assert.Contains(parsed.References, r => r.DependencyType == "postgresql" && r.Host == "10.88.20.30" && r.Port == 5433 && r.DatabaseName == "analytics");
        Assert.Contains(parsed.References, r => r.DependencyType == "redis" && r.Host == "10.88.20.31" && r.Port == 6380);
    }

    [Fact]
    public async Task DiscoveryAndGraphBuilder_IntegrateAllFourV2CollectorsWithoutLeakingSecrets()
    {
        await using var database = await CreateDatabaseAsync();
        var edge = new Server("srv-edge", "10.88.20.10", 22, "dva", "key", "prod");
        var app = new Server("srv-app", "10.88.20.20", 22, "dva", "key", "prod");
        var db = new Server("srv-db", "10.88.20.30", 22, "dva", "key", "prod");
        database.Context.Servers.AddRange(edge, app, db);
        await database.Context.SaveChangesAsync();

        var firewall = FirewallCollectorParser.Parse(Fixture("firewall-ufw.txt"));
        var tls = TlsCollectorParser.Parse(Fixture("tls-openssl.txt"));
        var haproxy = ReverseProxyCollectorParser.Parse(Fixture("reverse-proxy-haproxy.cfg"));
        var appConfig = AppConfigCollectorParser.Parse(AppConfigRedactor.Redact(Fixture("app-config-bundle.txt")));

        var graph = new GraphBuilder().Build(Guid.NewGuid(),
        [
            new ServerDiscoveryData(
                edge,
                new HostDiscoveryResult("srv-edge", [new HostInterface(2, "eth0", null, "UP", 1500, [new HostAddress("inet", "10.88.20.10", 24, "10.88.20.0/24", "global", null)])], [], []),
                new ListenerDiscoveryResult([new ListenerInfo("tcp", "LISTEN", "0.0.0.0", 443, null, "haproxy", 101)]),
                null, null, null, null,
                Firewall: firewall,
                Tls: tls,
                ReverseProxy: haproxy),
            new ServerDiscoveryData(
                app,
                new HostDiscoveryResult("srv-app", [new HostInterface(2, "eth0", null, "UP", 1500, [new HostAddress("inet", "10.88.20.20", 24, "10.88.20.0/24", "global", null)])], [], []),
                new ListenerDiscoveryResult([new ListenerInfo("tcp", "LISTEN", "0.0.0.0", 5000, null, "api", 202), new ListenerInfo("tcp", "LISTEN", "0.0.0.0", 3000, null, "web", 203)]),
                null, null, null, null,
                AppConfig: appConfig),
            new ServerDiscoveryData(
                db,
                new HostDiscoveryResult("srv-db", [new HostInterface(2, "eth0", null, "UP", 1500, [new HostAddress("inet", "10.88.20.30", 24, "10.88.20.0/24", "global", null)])], [], []),
                new ListenerDiscoveryResult([new ListenerInfo("tcp", "LISTEN", "0.0.0.0", 5432, null, "postgres", 301), new ListenerInfo("tcp", "LISTEN", "0.0.0.0", 6379, null, "redis-server", 302)]),
                null, null)
        ]);

        // 1. Firewall & TLS stored on edge host attributes + evidence.
        var edgeHost = graph.Nodes.Single(n => n.Key == $"host:{edge.Id:N}");
        using var edgeAttrs = JsonDocument.Parse(edgeHost.AttributesJson);
        Assert.Equal("ufw", edgeAttrs.RootElement.GetProperty("Firewall").GetProperty("Backend").GetString());
        Assert.True(edgeAttrs.RootElement.GetProperty("Firewall").GetProperty("Enabled").GetBoolean());
        Assert.Equal(2, edgeAttrs.RootElement.GetProperty("TlsCertificates").GetArrayLength());
        Assert.Contains(graph.Evidence, e => e.NodeId == edgeHost.Id && e.Collector == "firewall");
        Assert.Contains(graph.Evidence, e => e.NodeId == edgeHost.Id && e.Collector == "tls");

        // 2. HAProxy created domain:api.example.test, matched TLS SAN evidence to it, and wired declared edge -> srv-app:5000.
        var apiDomain = graph.Nodes.Single(n => n.Key == "domain:api.example.test");
        Assert.Contains(graph.Evidence, e => e.NodeId == apiDomain.Id && e.Collector == "tls");
        var appApiNode = graph.Nodes.Single(n => n.Key == $"service:{app.Id:N}:process:api");
        Assert.Contains(graph.Edges, e => e.TargetNodeId == appApiNode.Id && e.Port == 5000 && e.Kind == GraphEdgeKind.Declared);

        // 3. AppConfig wired srv-app -> srv-db postgres (:5432) and redis (:6379), plus external Nexus Unknown (:8081).
        var appHost = graph.Nodes.Single(n => n.Key == $"host:{app.Id:N}");
        var pgNode = graph.Nodes.Single(n => n.Key == $"service:{db.Id:N}:process:postgres");
        var redisNode = graph.Nodes.Single(n => n.Key == $"service:{db.Id:N}:process:redis-server");
        Assert.Contains(graph.Edges, e => e.SourceNodeId == appHost.Id && e.TargetNodeId == pgNode.Id && e.Port == 5432 && e.Kind == GraphEdgeKind.Declared);
        Assert.Contains(graph.Edges, e => e.SourceNodeId == appHost.Id && e.TargetNodeId == redisNode.Id && e.Port == 6379 && e.Kind == GraphEdgeKind.Declared);
        var nexusUnknown = graph.Nodes.Single(n => n.Kind == GraphNodeKind.Unknown && n.Key.Contains("10.88.20.40:8081", StringComparison.Ordinal));
        Assert.Contains(graph.Edges, e => e.SourceNodeId == appHost.Id && e.TargetNodeId == nexusUnknown.Id && e.Port == 8081);

        // 4. Zero canary secrets anywhere in graph nodes or evidence!
        foreach (var canary in SecretCanaries)
        {
            Assert.DoesNotContain(graph.Nodes, n => n.AttributesJson.Contains(canary, StringComparison.Ordinal));
            Assert.DoesNotContain(graph.Evidence, e => e.Description.Contains(canary, StringComparison.Ordinal));
        }
    }

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private sealed class StubSshRunner(Func<string, string> responder) : ISshRunner
    {
        public Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options, CancellationToken cancellationToken) =>
            Task.FromResult(new RemoteCommandResult(0, responder(command), string.Empty, TimeSpan.FromMilliseconds(1)));
    }

    private static async Task<SqliteTestDatabase> CreateDatabaseAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await context.Database.EnsureCreatedAsync();
        return new SqliteTestDatabase(connection, context);
    }

    private sealed class SqliteTestDatabase(SqliteConnection connection, AppDbContext context) : IAsyncDisposable
    {
        public AppDbContext Context { get; } = context;

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
