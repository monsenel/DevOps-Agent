using DevOpsAgent.Domain.Discovery;
using DevOpsAgent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DevOpsAgent.Tests;

public sealed class DiscoveryRecoveryTests
{
    [Fact]
    public async Task RunsLeftRunningAtStartupAreMarkedFailedAndOthersAreUntouched()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        await context.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.UtcNow;
        var interrupted = new Snapshot([Guid.NewGuid()], now.AddMinutes(-10));
        var completed = new Snapshot([Guid.NewGuid()], now.AddMinutes(-20));
        completed.Complete(now.AddMinutes(-19), 1, 0);
        context.Snapshots.AddRange(interrupted, completed);
        await context.SaveChangesAsync();

        var swept = await PersistenceServiceCollectionExtensions.RecoverInterruptedDiscoveryAsync(context, now, CancellationToken.None);

        Assert.Equal(1, swept);
        context.ChangeTracker.Clear();
        Assert.Equal(SnapshotStatus.Failed, (await context.Snapshots.SingleAsync(item => item.Id == interrupted.Id)).Status);
        Assert.NotNull((await context.Snapshots.SingleAsync(item => item.Id == interrupted.Id)).FinishedAtUtc);
        Assert.Equal(SnapshotStatus.Completed, (await context.Snapshots.SingleAsync(item => item.Id == completed.Id)).Status);
        Assert.Equal(0, await PersistenceServiceCollectionExtensions.RecoverInterruptedDiscoveryAsync(context, now, CancellationToken.None));
    }
}
