using DevOpsAgent.Domain.Monitoring;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DevOpsAgent.Tests;

public sealed class MetricRepositoryTests
{
    [Fact]
    public async Task SamplesAreQueryableAndFiveMinuteRollupsCanReplaceBucketsAndPruneRawData()
    {
        await using var database = await SqliteTestDatabase.CreateAsync();
        var repository = new MetricRepository(database.Context);
        var server = new Server("metrics", "metrics.internal", 22, "devops", "id_ed25519", "prod");
        database.Context.Servers.Add(server);
        await database.Context.SaveChangesAsync();

        var now = new DateTimeOffset(2026, 11, 14, 12, 10, 0, TimeSpan.Zero);
        var oldSample = new MetricSample(server.Id, "load_1m", 0.1, now.AddDays(-2));
        var samples = new[]
        {
            new MetricSample(server.Id, "load_1m", 1, now.AddMinutes(-4)),
            new MetricSample(server.Id, "load_1m", 3, now.AddMinutes(-2)),
            oldSample
        };
        await repository.AddSamplesAsync(samples, CancellationToken.None);

        var latest = await repository.GetLatestAsync(server.Id, CancellationToken.None);
        Assert.Single(latest);
        var recent = await repository.GetRecentAsync(server.Id, now.AddHours(-1), CancellationToken.None);
        Assert.Equal(2, recent.Count);
        var rollup = new MetricRollup(server.Id, "load_1m", new DateTimeOffset(2026, 11, 14, 12, 5, 0, TimeSpan.Zero), 1, 2, 3, 2);
        var expiredRollup = new MetricRollup(server.Id, "load_1m", now.AddDays(-31), 0, 0, 0, 1);
        await repository.SaveRollupsAsync([expiredRollup, rollup], now.AddDays(-1), now.AddDays(-30), CancellationToken.None);
        var refreshedRollup = new MetricRollup(server.Id, "load_1m", new DateTimeOffset(2026, 11, 14, 12, 5, 0, TimeSpan.Zero), 1, 2.5, 4, 3);
        await repository.SaveRollupsAsync([refreshedRollup], now.AddDays(-1), now.AddDays(-30), CancellationToken.None);

        Assert.Equal(1, await database.Context.MetricRollups.CountAsync());
        var savedRollup = await database.Context.MetricRollups.SingleAsync();
        Assert.Equal(3, savedRollup.SampleCount);
        Assert.Equal(2.5, savedRollup.Average);
        Assert.Equal(2, await database.Context.MetricSamples.CountAsync());
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
