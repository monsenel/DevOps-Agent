using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Agent;
using DevOpsAgent.Application.Updates;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevOpsAgent.Tests;

public sealed class UpdatesServiceTests
{
    [Fact]
    public async Task ScanPersistsPackageManagerPackagesAndEmptyResults()
    {
        await using var database = await CreateDatabaseAsync();
        var server = new Server("staging", "staging.example.internal", 22, "deploy", "key", "staging");
        database.Context.Servers.Add(server);
        await database.Context.SaveChangesAsync();
        var dto = new UpdatePackageDto("openssl", "3.0.1", "3.0.2", "jammy-security", true);
        var collector = new FakeUpdatesCollector(JsonSerializer.Serialize(new[] { dto }));
        var serverRepo = new ServerRepository(database.Context);
        var packageRepo = new UpdatePackageRepository(database.Context);
        var scanRepo = new UpdateScanRepository(database.Context);
        var executionModes = new ExecutionModeService(new AppSettingRepository(database.Context), serverRepo,
            NullLogger<ExecutionModeService>.Instance);
        var service = new UpdatesService(serverRepo, packageRepo, scanRepo, collector, executionModes, TimeProvider.System);

        var scanned = await service.ScanAsync(CancellationToken.None);
        var loaded = await service.ListAsync(CancellationToken.None);

        var serverResult = Assert.Single(scanned);
        Assert.Equal(PackageManagerKind.Apt, serverResult.PackageManager);
        Assert.True(Assert.Single(serverResult.Packages).IsSecurity);
        var loadedServer = Assert.Single(loaded);
        Assert.Equal(PackageManagerKind.Apt, loadedServer.PackageManager);
        Assert.Equal("openssl", Assert.Single(loadedServer.Packages).Name);
        Assert.Equal("Apt", Assert.Single(await scanRepo.ListAsync(CancellationToken.None)).PackageManager);
        // The group has no stored mode, so it is read-only (D-029) and the scan must be cache-only (D-027).
        Assert.True(Assert.Single(collector.CacheOnlyRequests));
    }

    [Fact]
    public async Task ReadOnlyGroups_AreMarked_SoTheirUpdatesCannotBeSelectedOrApplied()
    {
        await using var database = await CreateDatabaseAsync();
        var readOnly = new Server("ro", "ro.example.internal", 22, "deploy", "key", "prod");
        var confirm = new Server("cf", "cf.example.internal", 22, "deploy", "key", "staging");
        database.Context.Servers.AddRange(readOnly, confirm);
        await database.Context.SaveChangesAsync();
        var serverRepo = new ServerRepository(database.Context);
        var executionModes = new ExecutionModeService(new AppSettingRepository(database.Context), serverRepo,
            NullLogger<ExecutionModeService>.Instance);
        await executionModes.SaveAsync(new ExecutionModePolicy("staging", DevOpsAgent.Domain.Agent.ExecutionMode.Confirm), CancellationToken.None);
        var service = new UpdatesService(serverRepo, new UpdatePackageRepository(database.Context), new UpdateScanRepository(database.Context),
            new FakeUpdatesCollector("[]"), executionModes, TimeProvider.System);

        var listed = await service.ListAsync(CancellationToken.None);
        var scanned = await service.ScanAsync(CancellationToken.None);

        Assert.True(listed.Single(item => item.ServerName == "ro").ReadOnly);
        Assert.False(listed.Single(item => item.ServerName == "cf").ReadOnly);
        Assert.True(scanned.Single(item => item.ServerName == "ro").ReadOnly);
        Assert.False(scanned.Single(item => item.ServerName == "cf").ReadOnly);
        Assert.Contains("Read-only group", ServerUpdateStatus.ReadOnlyReason, StringComparison.Ordinal);
    }

    [Fact]
    public void HeaderLine_SeparatesScanTimeAndListRefresh()
    {
        var now = new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
        var scanned = new ServerUpdateStatus(Guid.NewGuid(), "db", "staging", PackageManagerKind.Apt, now.AddMinutes(-5), [], null,
            now.AddHours(-2), CacheOnly: true);
        var never = scanned with { PackageManager = null, CheckedAtUtc = null };

        Assert.Equal("apt · 11:55 · Package lists last refreshed 2 h ago · cached lists only (read-only group)",
            scanned.MetaLine(now, at => at.ToString("HH:mm")));
        Assert.Equal("Not scanned · No scan yet", never.MetaLine(now, at => at.ToString("HH:mm")));
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

    private sealed class FakeUpdatesCollector(string json) : IUpdatesCollector
    {
        public System.Collections.Concurrent.ConcurrentBag<bool> CacheOnlyRequests { get; } = [];

        public Task<CollectedUpdateResult> CollectAsync(Server server, bool cacheOnly, CancellationToken cancellationToken)
        {
            CacheOnlyRequests.Add(cacheOnly);
            return Task.FromResult(new CollectedUpdateResult(PackageManagerKind.Apt, json));
        }
    }

    private sealed class SqliteTestDatabase(SqliteConnection connection, AppDbContext context) : IAsyncDisposable
    {
        public AppDbContext Context { get; } = context;
        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
