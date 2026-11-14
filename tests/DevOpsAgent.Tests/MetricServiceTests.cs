using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Monitoring;
using DevOpsAgent.Domain.Monitoring;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DevOpsAgent.Tests;

public sealed class MetricServiceTests
{
    [Fact]
    public async Task PollReadsOneCombinedCommandAndPersistsSamplesIncludingCpuAfterBaseline()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var server = new Server("metrics", "metrics.internal", 22, "devops", "id_ed25519", "prod");
        database.Context.Servers.Add(server);
        await database.Context.SaveChangesAsync();

        var start = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
        var time = new MutableTimeProvider(start);
        var runner = new FixtureSshRunner(server.Id);
        var repository = new MetricRepository(database.Context);
        var service = new MetricService(new ServerRepository(database.Context), repository, runner, time);

        await service.PollAsync(CancellationToken.None);
        time.Set(start.AddSeconds(60));
        await service.PollAsync(CancellationToken.None);

        Assert.Equal(2, runner.Commands.Count);
        Assert.All(runner.Commands, command => Assert.Equal(MetricsParser.CollectionCommand, command));
        var detail = await service.GetServerMetricsAsync(server.Id, CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Equal(70, Assert.Single(detail.Latest, item => item.Name == "cpu_percent").Value);
        Assert.Contains(detail.Latest, item => item.Name == "memory_percent");
        Assert.Contains(detail.Latest, item => item.Name == "disk_percent");
        Assert.Contains(detail.Latest, item => item.Name == "load_1m");
        Assert.DoesNotContain(detail.Latest, item => item.Name.StartsWith('_'));
    }

    [Fact]
    public async Task PollPersistsEveryReachableServerEvenWhenAnotherFailsAndStillSurfacesTheFailure()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var inventory = Enumerable.Range(0, 12)
            .Select(index => new Server($"metrics-{index}", $"metrics-{index}.internal", 22, "devops", "id_ed25519", "staging")).ToArray();
        database.Context.Servers.AddRange(inventory);
        await database.Context.SaveChangesAsync();
        var runner = new DelayedSshRunner(inventory[3].Id);
        var service = new MetricService(new ServerRepository(database.Context), new MetricRepository(database.Context), runner,
            new MutableTimeProvider(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero)));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PollAsync(CancellationToken.None));

        var stored = await database.Context.MetricSamples.Select(sample => sample.ServerId).Distinct().ToListAsync();
        Assert.Equal(11, stored.Count);
        Assert.DoesNotContain(inventory[3].Id, stored);
        Assert.True(runner.MaximumConcurrent > 1);
    }

    [Fact]
    public async Task PollIsolatesDatabaseFailuresPerServer()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var inventory = Enumerable.Range(0, 4)
            .Select(index => new Server($"db-{index}", $"db-{index}.internal", 22, "devops", "id_ed25519", "staging")).ToArray();
        database.Context.Servers.AddRange(inventory);
        await database.Context.SaveChangesAsync();
        var metrics = new FlakyMetricRepository(new MetricRepository(database.Context),
            readFailure: inventory[0].Id, writeFailure: inventory[2].Id);
        var service = new MetricService(new ServerRepository(database.Context), metrics, new DelayedSshRunner(Guid.Empty),
            new MutableTimeProvider(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero)));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.PollAsync(CancellationToken.None));

        Assert.Contains("simulated", error.Message, StringComparison.Ordinal);
        var stored = await database.Context.MetricSamples.Select(sample => sample.ServerId).Distinct().ToListAsync();
        Assert.Equal(2, stored.Count);
        Assert.Contains(inventory[1].Id, stored);
        Assert.Contains(inventory[3].Id, stored);
    }

    private sealed class FlakyMetricRepository(IMetricRepository inner, Guid readFailure, Guid writeFailure) : IMetricRepository
    {
        public Task<IReadOnlyList<MetricSample>> GetLatestAsync(Guid serverId, CancellationToken cancellationToken) =>
            serverId == readFailure ? throw new InvalidOperationException("simulated read failure") : inner.GetLatestAsync(serverId, cancellationToken);

        public Task AddSamplesAsync(IReadOnlyCollection<MetricSample> samples, CancellationToken cancellationToken) =>
            samples.Any(sample => sample.ServerId == writeFailure)
                ? throw new InvalidOperationException("simulated write failure")
                : inner.AddSamplesAsync(samples, cancellationToken);

        public Task<IReadOnlyList<MetricSample>> GetRecentAsync(Guid serverId, DateTimeOffset sinceUtc, CancellationToken cancellationToken) =>
            inner.GetRecentAsync(serverId, sinceUtc, cancellationToken);

        public Task<IReadOnlyList<MetricSample>> GetUnrolledAsync(DateTimeOffset beforeUtc, CancellationToken cancellationToken) =>
            inner.GetUnrolledAsync(beforeUtc, cancellationToken);

        public Task SaveRollupsAsync(IReadOnlyCollection<MetricRollup> rollups, DateTimeOffset rawPruneBeforeUtc,
            DateTimeOffset rollupPruneBeforeUtc, CancellationToken cancellationToken) =>
            inner.SaveRollupsAsync(rollups, rawPruneBeforeUtc, rollupPruneBeforeUtc, cancellationToken);
    }

    private sealed class DelayedSshRunner(Guid failingServerId) : ISshRunner
    {
        private int _active;
        private int _maximumConcurrent;
        public int MaximumConcurrent => _maximumConcurrent;

        public async Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options, CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _active);
            int observed;
            while (active > (observed = _maximumConcurrent) && Interlocked.CompareExchange(ref _maximumConcurrent, active, observed) != observed) { }
            try
            {
                await Task.Delay(20, cancellationToken);
                if (serverId == failingServerId) throw new InvalidOperationException("unreachable");
                var output = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "metrics-sample-1.txt"), cancellationToken);
                return new RemoteCommandResult(0, output, string.Empty, TimeSpan.FromMilliseconds(10));
            }
            finally { Interlocked.Decrement(ref _active); }
        }
    }

    private sealed class FixtureSshRunner(Guid expectedServerId) : ISshRunner
    {
        public List<string> Commands { get; } = [];
        private int _callCount;

        public Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options, CancellationToken cancellationToken)
        {
            Assert.Equal(expectedServerId, serverId);
            Assert.False(options.UseSudo);
            Commands.Add(command);
            var fixture = _callCount++ == 0 ? "metrics-sample-1.txt" : "metrics-sample-2.txt";
            var output = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture));
            return Task.FromResult(new RemoteCommandResult(0, output, string.Empty, TimeSpan.FromMilliseconds(10)));
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Set(DateTimeOffset value) => _now = value;
    }

    private sealed class SqliteTestDatabase(SqliteConnection connection, AppDbContext context) : IAsyncDisposable
    {
        public AppDbContext Context { get; } = context;

        public static async Task<SqliteTestDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
            var context = new AppDbContext(options);
            await context.Database.EnsureCreatedAsync();
            return new SqliteTestDatabase(connection, context);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
