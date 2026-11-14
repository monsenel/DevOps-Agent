using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Monitoring;
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
