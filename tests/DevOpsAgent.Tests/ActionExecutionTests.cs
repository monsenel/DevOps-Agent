using System.Collections.Concurrent;
using System.Text.Json;
using DevOpsAgent.Application.Agent;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Domain.Agent;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevOpsAgent.Tests;

public sealed class ActionExecutionTests
{
    [Fact]
    public async Task ApprovedActionRunsAcrossServersInParallelAndStoresFullResults()
    {
        await using var database = await CreateDatabaseAsync();
        var servers = await AddServersAsync(database.Context, 6, "staging");
        var action = AddProposedAction(database.Context, "restart_service", servers, "{\"unit\":\"nginx.service\"}");
        var runner = new TestSshRunner();
        var execution = CreateService(database.Context, runner);

        var completed = await execution.ApproveAndRunAsync(action.Id, "admin", CancellationToken.None);

        Assert.Equal(ActionRecordStatus.Succeeded, completed.Status);
        Assert.Equal("admin", completed.ApprovedBy);
        Assert.NotNull(completed.ApprovedAtUtc);
        Assert.NotNull(completed.CompletedAtUtc);
        Assert.Equal(0, completed.ExitCode);
        Assert.Equal(6, runner.Commands.Count);
        Assert.InRange(runner.MaximumConcurrent, 2, 4);
        Assert.All(runner.Commands, command =>
        {
            Assert.Equal("systemctl restart -- 'nginx.service'", command.Command);
            Assert.True(command.Options.UseSudo);
        });

        var logEntry = Assert.Single(await execution.ListAsync(CancellationToken.None));
        Assert.Equal(6, logEntry.Results.Count);
        Assert.Contains(logEntry.Results, result => result.StandardOutput == "updated" && result.ExitCode == 0);
        Assert.All(logEntry.Results, result =>
        {
            Assert.True(result.UseSudo);
            Assert.StartsWith("sudo systemctl restart", result.DisplayCommand, StringComparison.Ordinal);
            Assert.EndsWith(":22", result.Endpoint, StringComparison.Ordinal);
        });
        Assert.Contains("nginx.service", completed.CommandsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedRemoteResultIsRecordedWithoutLosingOtherServerOutput()
    {
        await using var database = await CreateDatabaseAsync();
        var servers = await AddServersAsync(database.Context, 2, "prod");
        var action = AddProposedAction(database.Context, "run_shell", servers, "{\"command\":\"printf approved\"}");
        var runner = new TestSshRunner(servers[1].Id);
        var execution = CreateService(database.Context, runner);

        var completed = await execution.ApproveAndRunAsync(action.Id, "owner", CancellationToken.None);

        Assert.Equal(ActionRecordStatus.Failed, completed.Status);
        Assert.Null(completed.ExitCode);
        var results = (await execution.ListAsync(CancellationToken.None)).Single().Results;
        Assert.Equal(2, results.Count);
        Assert.Contains(results, result => result.ServerId == servers[0].Id && result.StandardOutput == "updated");
        Assert.Contains(results, result => result.ServerId == servers[1].Id && result.ExitCode == 2
            && result.StandardError == "command failed");
        Assert.All(runner.Commands, command =>
        {
            Assert.Equal("printf approved", command.Command);
            Assert.False(command.Options.UseSudo);
        });
    }

    [Fact]
    public async Task ProposedActionHasCommandPreviewAndCanBeRejectedOnce()
    {
        await using var database = await CreateDatabaseAsync();
        var servers = await AddServersAsync(database.Context, 1, "prod");
        var action = AddProposedAction(database.Context, "restart_service", servers, "{\"unit\":\"nginx\"}");
        var execution = CreateService(database.Context, new TestSshRunner());

        var pending = Assert.Single(await execution.ListAsync(CancellationToken.None));
        var preview = Assert.Single(pending.PendingCommandPreviews);
        Assert.Equal("systemctl restart -- 'nginx'", preview.Command);
        // Approvers must see the sudo wrapper and the real endpoint, not only the display name.
        Assert.Equal("sudo systemctl restart -- 'nginx'", preview.DisplayCommand);
        Assert.Equal("192.0.2.10:22", preview.Endpoint);
        var rejected = await execution.RejectAsync(action.Id, "admin", CancellationToken.None);
        Assert.Equal(ActionRecordStatus.Rejected, rejected.Status);
        Assert.Equal("admin", rejected.ApprovedBy);
        Assert.NotNull(rejected.CompletedAtUtc);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            execution.RejectAsync(action.Id, "admin", CancellationToken.None));
    }

    [Fact]
    public async Task ApprovalCannotRunActionAfterGroupChangesToReadOnly()
    {
        await using var database = await CreateDatabaseAsync();
        var servers = await AddServersAsync(database.Context, 1, "prod");
        var action = AddProposedAction(database.Context, "restart_service", servers, "{\"unit\":\"nginx\"}");
        var runner = new TestSshRunner();
        var executionModes = new ExecutionModeService(new AppSettingRepository(database.Context),
            new ServerRepository(database.Context), new ToolRegistry(), NullLogger<ExecutionModeService>.Instance);
        await executionModes.SaveAsync(new ExecutionModePolicy("prod", ExecutionMode.ReadOnly, []), CancellationToken.None);
        var execution = CreateService(database.Context, runner, executionModes);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            execution.ApproveAndRunAsync(action.Id, "admin", CancellationToken.None));
        Assert.Empty(runner.Commands);
        Assert.Equal(ActionRecordStatus.Proposed, (await database.Context.ActionRecords.SingleAsync()).Status);
    }

    [Fact]
    public void ResultsStoredBeforeEndpointAndSudoWereRecordedStillDeserialize()
    {
        const string legacy = "[{\"serverId\":\"c4d7d14f-897d-4b31-8d33-455cc6e926d6\",\"serverName\":\"old\",\"command\":\"id\","
            + "\"exitCode\":0,\"standardOutput\":\"ok\",\"standardError\":\"\",\"durationMilliseconds\":5,\"error\":null}]";

        var result = Assert.Single(JsonSerializer.Deserialize<ActionServerResult[]>(legacy, new JsonSerializerOptions(JsonSerializerDefaults.Web))!);

        Assert.Equal("old", result.ServerName);
        Assert.Null(result.Endpoint);
        Assert.False(result.UseSudo);
        Assert.Equal("id", result.DisplayCommand);
    }

    [Fact]
    public async Task SecondApproveAfterCompletionFailsWithoutRunningAgain()
    {
        await using var database = await CreateDatabaseAsync();
        var servers = await AddServersAsync(database.Context, 1, "staging");
        var action = AddProposedAction(database.Context, "restart_service", servers, "{\"unit\":\"nginx\"}");
        var runner = new TestSshRunner();
        var execution = CreateService(database.Context, runner);

        await execution.ApproveAndRunAsync(action.Id, "admin", CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            execution.ApproveAndRunAsync(action.Id, "other", CancellationToken.None));
        Assert.Single(runner.Commands);
    }

    [Fact]
    public async Task ApproveAfterAnotherCircuitClaimedTheActionDoesNotRun()
    {
        await using var database = await CreateDatabaseAsync();
        var servers = await AddServersAsync(database.Context, 1, "staging");
        var action = AddProposedAction(database.Context, "restart_service", servers, "{\"unit\":\"nginx\"}");
        var runner = new TestSshRunner();
        var execution = CreateService(database.Context, runner);
        // This circuit has the action tracked as Proposed...
        Assert.Equal(ActionRecordStatus.Proposed, (await new ActionRecordRepository(database.Context).GetAsync(action.Id, CancellationToken.None))!.Status);

        // ...while another circuit (separate DbContext) claims it first.
        await using var otherContext = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(database.Connection).Options);
        Assert.True(await new ActionRecordRepository(otherContext).TryTransitionFromProposedAsync(
            action.Id, ActionRecordStatus.Running, "other", DateTimeOffset.UtcNow, CancellationToken.None));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            execution.ApproveAndRunAsync(action.Id, "admin", CancellationToken.None));
        Assert.Empty(runner.Commands);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            execution.RejectAsync(action.Id, "admin", CancellationToken.None));
        var stored = await otherContext.ActionRecords.AsNoTracking().SingleAsync();
        Assert.Equal(ActionRecordStatus.Running, stored.Status);
        Assert.Equal("other", stored.ApprovedBy);
    }

    [Fact]
    public async Task RejectAfterApproveFailsAndClaimIsExclusive()
    {
        await using var database = await CreateDatabaseAsync();
        var servers = await AddServersAsync(database.Context, 1, "staging");
        var action = AddProposedAction(database.Context, "restart_service", servers, "{\"unit\":\"nginx\"}");
        var repository = new ActionRecordRepository(database.Context);

        Assert.True(await repository.TryTransitionFromProposedAsync(action.Id, ActionRecordStatus.Running, "a", DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.False(await repository.TryTransitionFromProposedAsync(action.Id, ActionRecordStatus.Running, "b", DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.False(await repository.TryTransitionFromProposedAsync(action.Id, ActionRecordStatus.Rejected, "b", DateTimeOffset.UtcNow, CancellationToken.None));

        var execution = CreateService(database.Context, new TestSshRunner());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            execution.RejectAsync(action.Id, "b", CancellationToken.None));
        var stored = await database.Context.ActionRecords.AsNoTracking().SingleAsync();
        Assert.Equal(ActionRecordStatus.Running, stored.Status);
        Assert.Equal("a", stored.ApprovedBy);
    }

    private static ActionExecutionService CreateService(
        AppDbContext context,
        ISshRunner runner,
        ExecutionModeService? executionModes = null)
    {
        var serverRepository = new ServerRepository(context);
        var registry = new ToolRegistry();
        executionModes ??= new ExecutionModeService(new AppSettingRepository(context), serverRepository, registry, NullLogger<ExecutionModeService>.Instance);
        return new ActionExecutionService(new ActionRecordRepository(context), serverRepository, registry, executionModes,
            new ToolCommandBuilder(registry), runner, TimeProvider.System);
    }

    private static ActionRecord AddProposedAction(AppDbContext context, string toolName, IReadOnlyList<Server> servers, string args)
    {
        var action = new ActionRecord(toolName, servers[0].Group, JsonSerializer.Serialize(servers.Select(server => server.Id)),
            args, null, DateTimeOffset.UtcNow);
        context.ActionRecords.Add(action);
        context.SaveChanges();
        return action;
    }

    private static async Task<List<Server>> AddServersAsync(AppDbContext context, int count, string group)
    {
        var servers = Enumerable.Range(0, count)
            .Select(index => new Server($"server-{group}-{index}", $"192.0.2.{index + 10}", 22, "deploy", "test-key", group))
            .ToList();
        context.Servers.AddRange(servers);
        await context.SaveChangesAsync();
        return servers;
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

    private sealed class SqliteTestDatabase(SqliteConnection connection, AppDbContext context) : IAsyncDisposable
    {
        public AppDbContext Context { get; } = context;
        public SqliteConnection Connection { get; } = connection;

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }

    private sealed class TestSshRunner(Guid? failureServer = null) : ISshRunner
    {
        private int _active;
        private int _maximumConcurrent;

        public ConcurrentQueue<(Guid ServerId, string Command, SshRunOptions Options)> Commands { get; } = new();
        public int MaximumConcurrent => _maximumConcurrent;

        public async Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options, CancellationToken cancellationToken)
        {
            Commands.Enqueue((serverId, command, options));
            var active = Interlocked.Increment(ref _active);
            UpdateMaximum(active);
            try
            {
                await Task.Delay(40, cancellationToken);
                return serverId == failureServer
                    ? new(2, string.Empty, "command failed", TimeSpan.FromMilliseconds(25))
                    : new(0, "updated", string.Empty, TimeSpan.FromMilliseconds(25));
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        private void UpdateMaximum(int current)
        {
            while (true)
            {
                var observed = _maximumConcurrent;
                if (current <= observed || Interlocked.CompareExchange(ref _maximumConcurrent, current, observed) == observed)
                    return;
            }
        }
    }
}
