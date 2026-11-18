using DevOpsAgent.Domain.Monitoring;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DevOpsAgent.Tests;

public sealed class MetricTimeConversionTests
{
    [Fact]
    public void ConverterStoresUtcTicksTruncatedToMilliseconds()
    {
        var local = new DateTimeOffset(2026, 11, 18, 13, 30, 0, 123, TimeSpan.FromHours(3.5)).AddTicks(7);

        var stored = UtcMillisecondsConverter.ToStored(local);

        Assert.Equal(new DateTimeOffset(2026, 11, 18, 10, 0, 0, 123, TimeSpan.Zero).UtcTicks, stored);
        var back = new UtcMillisecondsConverter().ConvertFromProvider(stored);
        Assert.Equal(TimeSpan.Zero, Assert.IsType<DateTimeOffset>(back).Offset);
    }

    [Theory]
    [InlineData("2026-11-18 10:00:00.1234567+00:00", "2026-11-18T10:00:00.123")]
    [InlineData("2026-11-18 10:00:00+00:00", "2026-11-18T10:00:00.000")]
    [InlineData("2026-11-18 13:30:00.5+03:30", "2026-11-18T10:00:00.500")]
    public async Task UpMigrationConvertsEveryTextFormatToTheSameInstant(string text, string expectedUtc)
    {
        await using var connection = await OpenAsync();
        await Exec(connection, "CREATE TABLE MetricSamples (Id INTEGER PRIMARY KEY, AtUtc)");
        await Exec(connection, $"INSERT INTO MetricSamples (Id, AtUtc) VALUES (1, '{text}')");

        await Exec(connection, MetricTimeConversionSql.Up("MetricSamples", "AtUtc"));

        var stored = Convert.ToInt64(await Scalar(connection, "SELECT AtUtc FROM MetricSamples WHERE Id = 1"));
        var expected = DateTimeOffset.Parse(expectedUtc + "+00:00", System.Globalization.CultureInfo.InvariantCulture).UtcTicks;
        Assert.Equal(expected, stored);
    }

    [Fact]
    public async Task UpMigrationLeavesAlreadyConvertedRowsAlone()
    {
        await using var connection = await OpenAsync();
        await Exec(connection, "CREATE TABLE MetricSamples (Id INTEGER PRIMARY KEY, AtUtc)");
        var ticks = new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero).UtcTicks;
        await Exec(connection, $"INSERT INTO MetricSamples (Id, AtUtc) VALUES (1, {ticks})");

        await Exec(connection, MetricTimeConversionSql.Up("MetricSamples", "AtUtc"));

        Assert.Equal(ticks, Convert.ToInt64(await Scalar(connection, "SELECT AtUtc FROM MetricSamples WHERE Id = 1")));
    }

    [Fact]
    public async Task DownMigrationWritesUtcTextThatReadsBackToTheSameInstant()
    {
        await using var connection = await OpenAsync();
        await Exec(connection, "CREATE TABLE MetricSamples (Id INTEGER PRIMARY KEY, AtUtc)");
        var instant = new DateTimeOffset(2026, 11, 18, 10, 0, 0, 456, TimeSpan.Zero);
        await Exec(connection, $"INSERT INTO MetricSamples (Id, AtUtc) VALUES (1, {instant.UtcTicks})");

        await Exec(connection, MetricTimeConversionSql.Down("MetricSamples", "AtUtc"));

        var text = Convert.ToString(await Scalar(connection, "SELECT AtUtc FROM MetricSamples WHERE Id = 1"))!;
        Assert.Equal(instant, DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task RecentSamplesAreFilteredBySinceAndReturnedInOrder()
    {
        await using var connection = await OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var context = new AppDbContext(options);
        await context.Database.EnsureCreatedAsync();
        var server = new Server("metrics", "metrics.internal", 22, "devops", "id_ed25519", "prod");
        context.Servers.Add(server);
        var baseTime = new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero);
        // Inserted out of order, one of them written with a +03:30 offset (same instant semantics).
        context.MetricSamples.Add(new MetricSample(server.Id, "cpu", 3, baseTime.AddMinutes(20)));
        context.MetricSamples.Add(new MetricSample(server.Id, "cpu", 1, baseTime.AddMinutes(5)));
        context.MetricSamples.Add(new MetricSample(server.Id, "cpu", 2, baseTime.AddMinutes(10).ToOffset(TimeSpan.FromHours(3.5))));
        context.MetricSamples.Add(new MetricSample(server.Id, "cpu", 0, baseTime.AddMinutes(-30)));
        await context.SaveChangesAsync();
        var repository = new MetricRepository(context);

        var recent = await repository.GetRecentAsync(server.Id, baseTime, CancellationToken.None);

        // The -30 minute sample is before "since" and is left out; the rest come back in time order.
        Assert.Equal(new[] { 1.0, 2.0, 3.0 }, recent.Select(item => item.Value).ToArray());
        Assert.Equal(baseTime.AddMinutes(5), recent[0].AtUtc);
        var latest = await repository.GetLatestAsync(server.Id, CancellationToken.None);
        Assert.Equal(3, Assert.Single(latest).Value);
    }

    private static async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        return connection;
    }

    private static async Task Exec(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> Scalar(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }
}
