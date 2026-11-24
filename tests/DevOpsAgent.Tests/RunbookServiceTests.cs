using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Agent;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevOpsAgent.Tests;

public sealed class RunbookServiceTests
{
    [Fact]
    public void BuiltInRunbooksIncludeAllFiveRequiredWorkflowsWithChecklistsAndPrompts()
    {
        var service = new RunbookService();
        var all = service.List();

        Assert.Equal(5, all.Count);
        Assert.Contains(all, r => r.Title.Contains("Nginx Reverse Proxy + TLS Setup", StringComparison.Ordinal));
        Assert.Contains(all, r => r.Title.Contains("New Server Security Hardening", StringComparison.Ordinal));
        Assert.Contains(all, r => r.Title.Contains("Docker Compose Service Setup", StringComparison.Ordinal));
        Assert.Contains(all, r => r.Title.Contains("Safe Rolling Patch", StringComparison.Ordinal));
        Assert.Contains(all, r => r.Title.Contains("Incident Triage", StringComparison.Ordinal));

        Assert.All(all, runbook =>
        {
            Assert.False(string.IsNullOrWhiteSpace(runbook.Id));
            Assert.False(string.IsNullOrWhiteSpace(runbook.AgentPrompt));
            Assert.True(runbook.Steps.Count >= 4);
            Assert.Equal(Enumerable.Range(1, runbook.Steps.Count), runbook.Steps.Select(s => s.Number));
        });
    }

    [Fact]
    public void FilterAndChatUrlEncodingWorkForOptionalServerTarget()
    {
        var service = new RunbookService();
        var matches = service.List("nginx");
        var nginx = Assert.Single(matches);
        Assert.Equal("nginx-reverse-proxy-tls", nginx.Id);

        var urlWithoutServer = RunbookService.BuildChatUrl(nginx);
        Assert.StartsWith("/agent-chat?prompt=", urlWithoutServer, StringComparison.Ordinal);

        var urlWithServer = RunbookService.BuildChatUrl(nginx, "srv-edge");
        Assert.StartsWith("/agent-chat?prompt=", urlWithServer, StringComparison.Ordinal);
        Assert.Contains("Target%20server%3A%20srv-edge", urlWithServer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListRunbooksAgentToolRunsReadOnlyWithoutServerOrApproval()
    {
        await using var db = await CreateDatabaseAsync();
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
            discovery, runner, runbooks: new RunbookService());

        var result = await dispatcher.DispatchAsync("list_runbooks",
            JsonSerializer.SerializeToElement(new { query = "hardening" }), null, CancellationToken.None);

        Assert.False(result.NeedsApproval);
        Assert.NotNull(result.ReadOnlyServerIds);
        Assert.Empty(result.ReadOnlyServerIds);
        Assert.Contains("New Server Security Hardening", result.Summary, StringComparison.Ordinal);
    }

    private static async Task<SqliteTestDatabase> CreateDatabaseAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var context = new AppDbContext(options);
        await context.Database.EnsureCreatedAsync();
        return new SqliteTestDatabase(connection, context);
    }

    private sealed class NoOpSshRunner : ISshRunner
    {
        public Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options, CancellationToken cancellationToken) =>
            Task.FromResult(new RemoteCommandResult(0, string.Empty, string.Empty, TimeSpan.Zero));
    }

    private sealed class SqliteTestDatabase(SqliteConnection connection, AppDbContext context) : IAsyncDisposable
    {
        public AppDbContext Context { get; } = context;
        public async ValueTask DisposeAsync() { await Context.DisposeAsync(); await connection.DisposeAsync(); }
    }
}
