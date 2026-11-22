using System.Collections.Concurrent;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Domain.Discovery;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevOpsAgent.Tests;

public sealed class DiscoveryCoordinatorTests
{
    [Fact]
    public async Task ConcurrentFullRunsShareSingleInFlightExecutionAndProgress()
    {
        await using var harness = await CreateHarnessAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var collector = new GatedCollector(entered, release);
        harness.Collector = collector;

        using var coordinator = new DiscoveryCoordinator(harness, NullLogger<DiscoveryCoordinator>.Instance);
        var progressA = new ConcurrentQueue<DiscoveryProgress>();
        var progressB = new ConcurrentQueue<DiscoveryProgress>();

        var first = coordinator.RunAsync(new InlineProgress(progressA.Enqueue), CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(coordinator.IsRunning);

        var second = coordinator.RunAsync(new InlineProgress(progressB.Enqueue), CancellationToken.None);
        release.SetResult();

        var snapshots = await Task.WhenAll(first, second);

        Assert.Equal(1, harness.ScopeExecutions);
        Assert.Equal(1, collector.Invocations);
        Assert.Equal(snapshots[0].Id, snapshots[1].Id);
        Assert.Equal(SnapshotStatus.Completed, snapshots[0].Status);
        Assert.False(coordinator.IsRunning);
        Assert.NotEmpty(progressA);
        Assert.NotEmpty(progressB);
    }

    [Fact]
    public async Task ClosingCallerMidRunLeavesHostOwnedDiscoveryCompleted()
    {
        await using var harness = await CreateHarnessAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Collector = new GatedCollector(entered, release);

        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = new DiscoveryCoordinator(harness, NullLogger<DiscoveryCoordinator>.Instance);
        coordinator.StateChanged += () =>
        {
            if (!coordinator.IsRunning && entered.Task.IsCompleted)
            {
                finished.TrySetResult();
            }
        };

        using var callerCts = new CancellationTokenSource();
        var callerTask = coordinator.RunAsync(null, callerCts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Simulate closing the browser tab: caller cancels while the host-owned scope keeps running.
        callerCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => callerTask);
        Assert.True(coordinator.IsRunning);

        release.SetResult();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(coordinator.IsRunning);

        await using var verifyContext = harness.CreateDbContext();
        var saved = await verifyContext.Snapshots.SingleAsync();
        Assert.Equal(SnapshotStatus.Completed, saved.Status);
        Assert.Equal(1, saved.SucceededServerCount);
    }

    [Fact]
    public async Task SingleServerMergeSerializesBehindActiveFullRun()
    {
        await using var harness = await CreateHarnessAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var collector = new GatedCollector(entered, release);
        harness.Collector = collector;

        using var coordinator = new DiscoveryCoordinator(harness, NullLogger<DiscoveryCoordinator>.Instance);
        var fullRun = coordinator.RunAsync(null, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var mergeRun = coordinator.RunServerAndMergeAsync(harness.ServerId, null, CancellationToken.None);
        await Task.Delay(50);
        Assert.Equal(1, harness.ScopeExecutions);
        Assert.False(mergeRun.IsCompleted);

        release.SetResult();
        var fullSnapshot = await fullRun;
        var mergeSnapshot = await mergeRun;

        Assert.Equal(2, harness.ScopeExecutions);
        Assert.Equal(1, harness.MaxConcurrentScopes);
        Assert.NotEqual(fullSnapshot.Id, mergeSnapshot.Id);
        Assert.Equal(SnapshotStatus.Completed, mergeSnapshot.Status);
    }

    [Fact]
    public async Task HostShutdownCancelsInFlightDiscoveryPromptly()
    {
        await using var harness = await CreateHarnessAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var neverRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Collector = new GatedCollector(entered, neverRelease);

        using var coordinator = new DiscoveryCoordinator(harness, NullLogger<DiscoveryCoordinator>.Instance);
        var run = coordinator.RunAsync(null, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        harness.HostShutdown.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(coordinator.IsRunning);

        await using var verifyContext = harness.CreateDbContext();
        var saved = await verifyContext.Snapshots.SingleAsync();
        Assert.Equal(SnapshotStatus.Cancelled, saved.Status);
    }

    private static async Task<ScopeHarness> CreateHarnessAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var setup = new AppDbContext(options);
        await setup.Database.EnsureCreatedAsync();
        var server = new Server("edge", "10.88.10.10", 22, "deploy", "id_ed25519", "staging");
        setup.Servers.Add(server);
        await setup.SaveChangesAsync();
        return new ScopeHarness(connection, options, server.Id);
    }

    private sealed class ScopeHarness(SqliteConnection connection, DbContextOptions<AppDbContext> options, Guid serverId)
        : IDiscoveryScopeRunner, IAsyncDisposable
    {
        private int _concurrent;
        private int _maxConcurrent;
        private int _executions;

        public Guid ServerId { get; } = serverId;
        public CancellationTokenSource HostShutdown { get; } = new();
        public ICollector Collector { get; set; } = null!;
        public int ScopeExecutions => Volatile.Read(ref _executions);
        public int MaxConcurrentScopes => Volatile.Read(ref _maxConcurrent);

        public AppDbContext CreateDbContext() => new(options);

        public async Task<Snapshot> ExecuteAsync(
            Func<DiscoveryService, IProgress<DiscoveryProgress>, CancellationToken, Task<Snapshot>> work,
            IProgress<DiscoveryProgress> progress)
        {
            var active = Interlocked.Increment(ref _concurrent);
            Interlocked.Increment(ref _executions);
            while (true)
            {
                var observed = Volatile.Read(ref _maxConcurrent);
                if (active <= observed || Interlocked.CompareExchange(ref _maxConcurrent, active, observed) == observed)
                {
                    break;
                }
            }

            try
            {
                await using var db = CreateDbContext();
                var discovery = new DiscoveryService(
                    new ServerRepository(db),
                    new SnapshotRepository(db),
                    new GraphRepository(db),
                    new GraphBuilder(),
                    new StubSshRunner(),
                    [Collector],
                    TimeProvider.System);
                return await work(discovery, progress, HostShutdown.Token);
            }
            finally
            {
                Interlocked.Decrement(ref _concurrent);
            }
        }

        public async ValueTask DisposeAsync()
        {
            HostShutdown.Dispose();
            await connection.DisposeAsync();
        }
    }

    private sealed class GatedCollector(TaskCompletionSource entered, TaskCompletionSource release) : ICollector
    {
        private int _invocations;
        public int Invocations => Volatile.Read(ref _invocations);
        public string Name => "host";

        public Task<bool> IsApplicableAsync(ServerContext context, CancellationToken cancellationToken) => Task.FromResult(true);

        public async Task<CollectorOutput> CollectAsync(ServerContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _invocations);
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return new CollectorOutput("ok", string.Empty, 0, TimeSpan.FromMilliseconds(5));
        }

        public CollectorResult Parse(CollectorOutput output) =>
            new(System.Text.Json.JsonSerializer.SerializeToElement(new HostDiscoveryResult("edge", [], [], [])));
    }

    private sealed class StubSshRunner : ISshRunner
    {
        public Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options, CancellationToken cancellationToken) =>
            Task.FromResult(new RemoteCommandResult(0, string.Empty, string.Empty, TimeSpan.Zero));
    }

    private sealed class InlineProgress(Action<DiscoveryProgress> callback) : IProgress<DiscoveryProgress>
    {
        public void Report(DiscoveryProgress value) => callback(value);
    }
}
