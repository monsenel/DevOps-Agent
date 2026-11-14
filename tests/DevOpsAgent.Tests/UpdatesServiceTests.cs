using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Updates;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

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
        var service = new UpdatesService(serverRepo, packageRepo, scanRepo, collector, TimeProvider.System);

        var scanned = await service.ScanAsync(CancellationToken.None);
        var loaded = await service.ListAsync(CancellationToken.None);

        var serverResult = Assert.Single(scanned);
        Assert.Equal(PackageManagerKind.Apt, serverResult.PackageManager);
        Assert.True(Assert.Single(serverResult.Packages).IsSecurity);
        var loadedServer = Assert.Single(loaded);
        Assert.Equal(PackageManagerKind.Apt, loadedServer.PackageManager);
        Assert.Equal("openssl", Assert.Single(loadedServer.Packages).Name);
        Assert.Equal("Apt", Assert.Single(await scanRepo.ListAsync(CancellationToken.None)).PackageManager);
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
        public Task<CollectedUpdateResult> CollectAsync(Guid serverId, CancellationToken cancellationToken) =>
            Task.FromResult(new CollectedUpdateResult(PackageManagerKind.Apt, json));
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
