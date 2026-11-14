using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Agent;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevOpsAgent.Tests;

public sealed class AgentToolDispatcherTests
{
    [Fact]
    public async Task ReadOnlyToolReportsAnUnreachableServerWithoutDroppingTheOthers()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await context.Database.EnsureCreatedAsync();
        var healthy = new Server("healthy", "healthy.example.internal", 22, "deploy", "key", "staging");
        var broken = new Server("broken", "broken.example.internal", 22, "deploy", "key", "staging");
        context.Servers.AddRange(healthy, broken);
        await context.SaveChangesAsync();
        var runner = new PartiallyFailingRunner(broken.Id);
        var dispatcher = CreateDispatcher(context, runner);
        var arguments = JsonDocument.Parse($"{{\"serverIds\":[\"{healthy.Id}\",\"{broken.Id}\"]}}").RootElement.Clone();

        var result = await dispatcher.DispatchAsync("docker_ps", arguments, null, CancellationToken.None);

        Assert.Contains("[healthy] exit=0", result.Summary, StringComparison.Ordinal);
        Assert.Contains("CONTAINER-OK", result.Summary, StringComparison.Ordinal);
        Assert.Contains("[broken] error:", result.Summary, StringComparison.Ordinal);
        Assert.Contains("connection refused", result.Summary, StringComparison.Ordinal);
    }

    private static AgentToolDispatcher CreateDispatcher(AppDbContext context, ISshRunner runner)
    {
        var registry = new ToolRegistry();
        var serverRepository = new ServerRepository(context);
        var actionRepository = new ActionRecordRepository(context);
        var executionModes = new ExecutionModeService(new AppSettingRepository(context), serverRepository, registry, NullLogger<ExecutionModeService>.Instance);
        var commandBuilder = new ToolCommandBuilder(registry);
        var approvals = new ApprovalService(registry, executionModes, serverRepository, actionRepository, TimeProvider.System);
        var execution = new ActionExecutionService(actionRepository, serverRepository, registry, executionModes, commandBuilder,
            runner, TimeProvider.System);
        var snapshots = new SnapshotRepository(context);
        var graphs = new GraphRepository(context);
        var discovery = new DiscoveryService(serverRepository, snapshots, graphs, new GraphBuilder(), runner, [], TimeProvider.System);
        return new AgentToolDispatcher(registry, approvals, execution, commandBuilder, serverRepository, graphs, snapshots,
            discovery, runner);
    }

    private sealed class PartiallyFailingRunner(Guid failingServerId) : ISshRunner
    {
        public Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options,
            CancellationToken cancellationToken) =>
            serverId == failingServerId
                ? throw new InvalidOperationException("connection refused")
                : Task.FromResult(new RemoteCommandResult(0, "CONTAINER-OK", string.Empty, TimeSpan.FromMilliseconds(1)));
    }
}
