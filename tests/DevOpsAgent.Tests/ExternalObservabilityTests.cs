using System.Net;
using System.Text;
using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Agent;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Application.Monitoring;
using DevOpsAgent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevOpsAgent.Tests;

public sealed class ExternalObservabilityTests
{
    [Fact]
    public async Task SaveSettingsEnforcesLocalityAndPreservesEncryptedTokens()
    {
        await using var db = await CreateDatabaseAsync();
        var service = CreateService(db.Context, new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));

        // Plain HTTP to public host is rejected (LlmEndpointLocality.AllowsCredentials).
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveSettingsAsync(
            new ExternalObservabilitySettingsInput(true, "http://prometheus.example.com:9090", "tok", false, "", ""),
            CancellationToken.None));

        // Embedded userinfo credentials in URL are rejected.
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveSettingsAsync(
            new ExternalObservabilitySettingsInput(true, "https://user:pass@prometheus.example.com", "", false, "", ""),
            CancellationToken.None));

        // Private HTTP and public HTTPS are accepted.
        await service.SaveSettingsAsync(
            new ExternalObservabilitySettingsInput(true, "http://10.0.0.5:9090/", "prom-secret", true, "https://logs.example.com/", "vlogs-secret"),
            CancellationToken.None);

        var loaded = await service.GetSettingsAsync(CancellationToken.None);
        Assert.True(loaded.PrometheusEnabled);
        Assert.Equal("http://10.0.0.5:9090", loaded.PrometheusBaseUrl);
        Assert.True(loaded.HasPrometheusToken);
        Assert.True(loaded.VictoriaLogsEnabled);
        Assert.Equal("https://logs.example.com", loaded.VictoriaLogsBaseUrl);
        Assert.True(loaded.HasVictoriaLogsToken);

        // Blank token preserves previously saved encrypted token.
        await service.SaveSettingsAsync(
            new ExternalObservabilitySettingsInput(true, "http://10.0.0.5:9090", "", true, "https://logs.example.com", ""),
            CancellationToken.None);
        var preserved = await service.GetSettingsAsync(CancellationToken.None);
        Assert.True(preserved.HasPrometheusToken);
        Assert.True(preserved.HasVictoriaLogsToken);
    }

    [Fact]
    public async Task QueriesPrometheusAndVictoriaLogsWithFakeHttpAndReadOnlyTool()
    {
        await using var db = await CreateDatabaseAsync();
        var promFixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "prometheus-query.json"));
        var vlogsFixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "victorialogs-query.ndjson"));

        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpHandler(request =>
        {
            requests.Add(request);
            if (request.RequestUri!.AbsolutePath == "/api/v1/query")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(promFixture, Encoding.UTF8, "application/json")
                };
            }

            if (request.RequestUri.AbsolutePath == "/select/logsql/query")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(vlogsFixture, Encoding.UTF8, "application/x-ndjson")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var obs = CreateService(db.Context, handler);
        await obs.SaveSettingsAsync(
            new ExternalObservabilitySettingsInput(true, "http://prometheus:9090", "prom-token", true, "http://victorialogs:9428", "vlogs-token"),
            CancellationToken.None);

        var promResult = await obs.QueryPrometheusAsync("up", CancellationToken.None);
        Assert.True(promResult.Succeeded);
        Assert.Contains("srv-edge:9100", promResult.Summary, StringComparison.Ordinal);
        Assert.Contains("srv-app:9100", promResult.Summary, StringComparison.Ordinal);
        Assert.Equal(HttpMethod.Get, requests[0].Method);
        Assert.Equal("Bearer", requests[0].Headers.Authorization?.Scheme);
        Assert.Equal("prom-token", requests[0].Headers.Authorization?.Parameter);

        var logsResult = await obs.QueryVictoriaLogsAsync("error", 25, CancellationToken.None);
        Assert.True(logsResult.Succeeded);
        Assert.Contains("upstream timed out", logsResult.Summary, StringComparison.Ordinal);
        Assert.Contains("database pool exhausted", logsResult.Summary, StringComparison.Ordinal);
        Assert.Equal(HttpMethod.Get, requests[1].Method);
        Assert.Contains("limit=25", requests[1].RequestUri!.Query, StringComparison.Ordinal);

        // Verify read-only agent tool dispatch without server or approval.
        var registry = new ToolRegistry();
        var settings = new AppSettingRepository(db.Context);
        var executionModes = new ExecutionModeService(settings, new ServerRepository(db.Context), NullLogger<ExecutionModeService>.Instance);
        var approvals = new ApprovalService(registry, executionModes, new ServerRepository(db.Context),
            new ActionRecordRepository(db.Context), TestBanGuard.Create(), TimeProvider.System);
        var commandBuilder = new ToolCommandBuilder(registry);
        var runner = new NoOpSshRunner();
        var execution = new ActionExecutionService(new ActionRecordRepository(db.Context), new ServerRepository(db.Context),
            registry, executionModes, commandBuilder, runner, TestBanGuard.Create(), TimeProvider.System);
        var discovery = new DiscoveryService(new ServerRepository(db.Context), new SnapshotRepository(db.Context),
            new GraphRepository(db.Context), new GraphBuilder(), runner, [], TimeProvider.System);
        var dispatcher = new AgentToolDispatcher(registry, approvals, execution, commandBuilder,
            new ServerRepository(db.Context), new GraphRepository(db.Context), new SnapshotRepository(db.Context),
            discovery, runner, observability: obs);

        var toolResult = await dispatcher.DispatchAsync("query_observability",
            JsonSerializer.SerializeToElement(new { source = "prometheus", query = "up" }), null, CancellationToken.None);

        Assert.False(toolResult.NeedsApproval);
        Assert.Contains("Prometheus vector", toolResult.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CorruptProtectedTokenReturnsErrorResultInsteadOfThrowing()
    {
        await using var db = await CreateDatabaseAsync();
        var obs = new ExternalObservabilityService(
            new AppSettingRepository(db.Context),
            new FailingUnprotectProtector(),
            new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))),
            NullLogger<ExternalObservabilityService>.Instance);

        await obs.SaveSettingsAsync(
            new ExternalObservabilitySettingsInput(true, "http://prometheus:9090", "tok", true, "http://victorialogs:9428", "tok2"),
            CancellationToken.None);

        var prom = await obs.QueryPrometheusAsync("up", CancellationToken.None);
        Assert.False(prom.Succeeded);
        Assert.NotNull(prom.Error);

        var vlogs = await obs.QueryVictoriaLogsAsync("error", 10, CancellationToken.None);
        Assert.False(vlogs.Succeeded);
        Assert.NotNull(vlogs.Error);
    }

    private static ExternalObservabilityService CreateService(AppDbContext context, HttpMessageHandler handler) =>
        new(new AppSettingRepository(context), new PlainProtector(), new HttpClient(handler), NullLogger<ExternalObservabilityService>.Instance);

    private static async Task<SqliteTestDatabase> CreateDatabaseAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var context = new AppDbContext(options);
        await context.Database.EnsureCreatedAsync();
        return new SqliteTestDatabase(connection, context);
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private sealed class NoOpSshRunner : ISshRunner
    {
        public Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options, CancellationToken cancellationToken) =>
            Task.FromResult(new RemoteCommandResult(0, string.Empty, string.Empty, TimeSpan.Zero));
    }

    private sealed class FailingUnprotectProtector : ISecretProtector
    {
        public string Protect(string plaintext) => "enc:" + plaintext;
        public string Unprotect(string ciphertext) => throw new System.Security.Cryptography.CryptographicException("Key ring rotated");
    }

    private sealed class PlainProtector : ISecretProtector
    {
        public string Protect(string plaintext) => "enc:" + plaintext;
        public string Unprotect(string ciphertext) => ciphertext.StartsWith("enc:", StringComparison.Ordinal) ? ciphertext[4..] : ciphertext;
    }

    private sealed class SqliteTestDatabase(SqliteConnection connection, AppDbContext context) : IAsyncDisposable
    {
        public AppDbContext Context { get; } = context;
        public async ValueTask DisposeAsync() { await Context.DisposeAsync(); await connection.DisposeAsync(); }
    }
}
