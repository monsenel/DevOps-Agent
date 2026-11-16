using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Servers;
using DevOpsAgent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace DevOpsAgent.Tests;

public sealed class ServerInventoryTests
{
    [Fact]
    public async Task CreateAndUpdate_ProtectPassphraseAndValidateInventoryFields()
    {
        await using var database = await CreateDatabaseAsync();
        var repository = new ServerRepository(database.Context);
        var service = new ServerService(repository, new TestSecretProtector(), new TestKeyStore("id_ed25519"), new RecordingSshConnectionPool());
        var first = await service.CreateAsync(Input("edge", "secret phrase"), CancellationToken.None);

        Assert.True(first.HasKeyPassphrase);
        Assert.Equal("prod", first.Group);
        Assert.Equal(new[] { "web", "public" }, first.Tags);
        Assert.DoesNotContain("secret phrase", JsonSerializer.Serialize(first));
        Assert.Equal("protected:secret phrase", (await repository.GetAsync(first.Id, CancellationToken.None))!.ProtectedKeyPassphrase);

        var duplicate = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateAsync(Input("EDGE", null), CancellationToken.None));
        Assert.Contains("already exists", duplicate.Message, StringComparison.OrdinalIgnoreCase);

        var invalidPort = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.CreateAsync(Input("bad-port", null) with { Port = 65536 }, CancellationToken.None));
        Assert.Contains("between 1 and 65535", invalidPort.Message);

        var updated = await service.UpdateAsync(first.Id,
            Input("edge", null) with { Group = "staging", Tags = ["private"] }, CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal("staging", updated!.Group);
        Assert.True(updated.HasKeyPassphrase);
        Assert.Equal("protected:secret phrase", (await repository.GetAsync(first.Id, CancellationToken.None))!.ProtectedKeyPassphrase);
    }

    [Fact]
    public async Task OnlyConnectionRelevantEditsEvictThePooledSshConnection()
    {
        await using var database = await CreateDatabaseAsync();
        var pool = new RecordingSshConnectionPool();
        var service = new ServerService(new ServerRepository(database.Context), new TestSecretProtector(),
            new TestKeyStore("id_ed25519", "id_other"), pool);
        var gateway = await service.CreateAsync(Input("gateway", null), CancellationToken.None);
        var server = await service.CreateAsync(Input("edge", null), CancellationToken.None);

        // Cosmetic edits (name, group, tags, blank passphrase field) keep live sessions alive.
        await service.UpdateAsync(server.Id, Input("edge-renamed", null) with { Group = "staging", Tags = ["x"] }, CancellationToken.None);
        Assert.Empty(pool.Evicted);

        var current = Input("edge-renamed", null) with { Group = "staging", Tags = ["x"] };
        foreach (var changed in new[]
                 {
                     current with { Port = 2222 },
                     current with { User = "other" },
                     current with { KeyFile = "id_other" },
                     current with { KeyPassphrase = "new phrase" },
                     current with { JumpServerId = gateway.Id },
                     current with { Host = "moved.example.internal" }
                 })
        {
            pool.Evicted.Clear();
            await service.UpdateAsync(server.Id, changed, CancellationToken.None);
            Assert.Equal([server.Id], pool.Evicted);
            await service.UpdateAsync(server.Id, current with { KeyPassphrase = null }, CancellationToken.None);
        }
    }

    [Fact]
    public async Task EditingAJumpServerAlsoEvictsEveryServerBehindIt()
    {
        await using var database = await CreateDatabaseAsync();
        var pool = new RecordingSshConnectionPool();
        var service = new ServerService(new ServerRepository(database.Context), new TestSecretProtector(),
            new TestKeyStore("id_ed25519"), pool);
        var gateway = await service.CreateAsync(Input("gateway", null), CancellationToken.None);
        var middle = await service.CreateAsync(Input("middle", null) with { JumpServerId = gateway.Id }, CancellationToken.None);
        var leaf = await service.CreateAsync(Input("leaf", null) with { JumpServerId = middle.Id }, CancellationToken.None);
        var unrelated = await service.CreateAsync(Input("unrelated", null), CancellationToken.None);

        await service.UpdateAsync(gateway.Id, Input("gateway", null) with { Host = "moved.example.internal" }, CancellationToken.None);

        Assert.Equal(gateway.Id, pool.Evicted[0]);
        Assert.Equal(new[] { gateway.Id, middle.Id, leaf.Id }.Order(), pool.Evicted.Order());
        Assert.DoesNotContain(unrelated.Id, pool.Evicted);

        // A cosmetic edit of the jump server evicts nobody.
        pool.Evicted.Clear();
        await service.UpdateAsync(gateway.Id, Input("gateway-renamed", null) with { Host = "moved.example.internal" }, CancellationToken.None);
        Assert.Empty(pool.Evicted);
    }

    [Fact]
    public async Task UpdateAndDeleteEvictThePooledSshConnection()
    {
        await using var database = await CreateDatabaseAsync();
        var pool = new RecordingSshConnectionPool();
        var service = new ServerService(new ServerRepository(database.Context), new TestSecretProtector(),
            new TestKeyStore("id_ed25519"), pool);
        var server = await service.CreateAsync(Input("edge", null), CancellationToken.None);
        Assert.Empty(pool.Evicted);

        await service.UpdateAsync(server.Id, Input("edge", null) with { Host = "other.example.internal" }, CancellationToken.None);
        Assert.Equal([server.Id], pool.Evicted);

        Assert.False(await service.DeleteAsync(Guid.NewGuid(), CancellationToken.None));
        Assert.Single(pool.Evicted);
        Assert.True(await service.DeleteAsync(server.Id, CancellationToken.None));
        Assert.Equal([server.Id, server.Id], pool.Evicted);
    }

    [Fact]
    public async Task JumpServerMustExistAndCannotBeRemovedWhileReferenced()
    {
        await using var database = await CreateDatabaseAsync();
        var service = new ServerService(new ServerRepository(database.Context), new TestSecretProtector(), new TestKeyStore("id_ed25519"), new RecordingSshConnectionPool());
        var jump = await service.CreateAsync(Input("gateway", null), CancellationToken.None);
        var target = await service.CreateAsync(Input("private-app", null) with { JumpServerId = jump.Id }, CancellationToken.None);

        Assert.Equal("gateway", target.JumpServerName);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteAsync(jump.Id, CancellationToken.None));
        Assert.False(await service.DeleteAsync(Guid.NewGuid(), CancellationToken.None));
        Assert.True(await service.DeleteAsync(target.Id, CancellationToken.None));
        Assert.True(await service.DeleteAsync(jump.Id, CancellationToken.None));
    }

    [Fact]
    public async Task JumpServerRouteCannotContainACycle()
    {
        await using var database = await CreateDatabaseAsync();
        var service = new ServerService(new ServerRepository(database.Context), new TestSecretProtector(), new TestKeyStore("id_ed25519"), new RecordingSshConnectionPool());
        var gateway = await service.CreateAsync(Input("gateway", null), CancellationToken.None);
        var target = await service.CreateAsync(Input("private-app", null) with { JumpServerId = gateway.Id }, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateAsync(
            gateway.Id, Input("gateway", null) with { JumpServerId = target.Id }, CancellationToken.None));

        Assert.Contains("cycle", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task KeyFileMustBeAnAvailableNameWithoutPathTraversal()
    {
        await using var database = await CreateDatabaseAsync();
        var service = new ServerService(new ServerRepository(database.Context), new TestSecretProtector(), new TestKeyStore("id_ed25519"), new RecordingSshConnectionPool());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateAsync(Input("missing", null) with { KeyFile = "missing-key" }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateAsync(Input("path", null) with { KeyFile = "../id_ed25519" }, CancellationToken.None));
    }

    [Fact]
    public async Task SetAllowedSshSources_SavesTheNormalisedListAndChangesNothingElse()
    {
        await using var database = await CreateDatabaseAsync();
        var pool = new RecordingSshConnectionPool();
        var repository = new ServerRepository(database.Context);
        var service = new ServerService(repository, new TestSecretProtector(), new TestKeyStore("id_ed25519"), pool);
        var created = await service.CreateAsync(Input("edge", "secret phrase"), CancellationToken.None);
        Assert.Empty(created.AllowedSshSources);
        var before = await repository.GetAsync(created.Id, CancellationToken.None);
        var (name, host, port, tags, passphrase) = (before!.Name, before.Host, before.Port, before.TagsJson, before.ProtectedKeyPassphrase);

        var result = await service.SetAllowedSshSourcesAsync(
            created.Id, "# office\n203.0.113.7\n192.168.20.5/24, 2001:DB8::/32\n203.0.113.7", CancellationToken.None);

        Assert.True(result.Saved);
        Assert.Empty(result.Errors);
        Assert.Equal(["203.0.113.7", "192.168.20.0/24", "2001:db8::/32"], result.Sources);
        var listed = Assert.Single(await service.ListAsync(CancellationToken.None));
        Assert.Equal(result.Sources, listed.AllowedSshSources);
        var after = await repository.GetAsync(created.Id, CancellationToken.None);
        Assert.Equal((name, host, port, tags, passphrase), (after!.Name, after.Host, after.Port, after.TagsJson, after.ProtectedKeyPassphrase));
        Assert.Empty(pool.Evicted);

        // Editing the server afterwards keeps the list; clearing it empties it.
        var updated = await service.UpdateAsync(created.Id, Input("edge", null) with { Group = "staging" }, CancellationToken.None);
        Assert.Equal(result.Sources, updated!.AllowedSshSources);
        var cleared = await service.SetAllowedSshSourcesAsync(created.Id, "  \n# nothing\n", CancellationToken.None);
        Assert.True(cleared.Saved);
        Assert.Empty(cleared.Sources);
        Assert.Empty(Assert.Single(await service.ListAsync(CancellationToken.None)).AllowedSshSources);
    }

    [Fact]
    public async Task SetAllowedSshSources_InvalidTextReturnsLineErrorsAndSavesNothing()
    {
        await using var database = await CreateDatabaseAsync();
        var service = new ServerService(new ServerRepository(database.Context), new TestSecretProtector(),
            new TestKeyStore("id_ed25519"), new RecordingSshConnectionPool());
        var created = await service.CreateAsync(Input("edge", null), CancellationToken.None);
        _ = await service.SetAllowedSshSourcesAsync(created.Id, "203.0.113.7", CancellationToken.None);

        var result = await service.SetAllowedSshSourcesAsync(created.Id, "10.0.0.1\n10.0.0.256\nbogus", CancellationToken.None);

        Assert.False(result.Saved);
        Assert.False(result.ServerNotFound);
        Assert.Empty(result.Sources);
        Assert.Equal(
        [
            "line 2: \"10.0.0.256\" is not an IP address or CIDR range",
            "line 3: \"bogus\" is not an IP address or CIDR range"
        ], result.Errors);
        Assert.Equal(["203.0.113.7"], Assert.Single(await service.ListAsync(CancellationToken.None)).AllowedSshSources);
    }

    [Fact]
    public async Task SetAllowedSshSources_UnknownServerIsReportedWithoutThrowing()
    {
        await using var database = await CreateDatabaseAsync();
        var service = new ServerService(new ServerRepository(database.Context), new TestSecretProtector(),
            new TestKeyStore("id_ed25519"), new RecordingSshConnectionPool());

        var result = await service.SetAllowedSshSourcesAsync(Guid.NewGuid(), "203.0.113.7", CancellationToken.None);

        Assert.False(result.Saved);
        Assert.True(result.ServerNotFound);
        Assert.Empty(result.Sources);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task AllowedSshSourcesColumnRoundTripsThroughTheDatabase()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        Guid id;
        await using (var writer = new AppDbContext(options))
        {
            await writer.Database.EnsureCreatedAsync();
            var service = new ServerService(new ServerRepository(writer), new TestSecretProtector(), new TestKeyStore("id_ed25519"),
                new RecordingSshConnectionPool());
            id = (await service.CreateAsync(Input("edge", null), CancellationToken.None)).Id;
            _ = await service.SetAllowedSshSourcesAsync(id, "203.0.113.7\n2001:db8::/32", CancellationToken.None);
        }

        // A fresh context reads the stored JSON, not a tracked entity.
        await using var reader = new AppDbContext(options);
        var stored = await reader.Servers.AsNoTracking().SingleAsync(server => server.Id == id);
        Assert.Equal("""["203.0.113.7","2001:db8::/32"]""", stored.AllowedSshSourcesJson);
        var summary = Assert.Single(await new ServerService(new ServerRepository(reader), new TestSecretProtector(),
            new TestKeyStore("id_ed25519"), new RecordingSshConnectionPool()).ListAsync(CancellationToken.None));
        Assert.Equal(["203.0.113.7", "2001:db8::/32"], summary.AllowedSshSources);
    }

    [Fact]
    public void NewServerStartsWithAnEmptyAllowedSshSourcesJsonArray()
    {
        var server = new DevOpsAgent.Domain.Servers.Server("edge", "edge.example.internal", 22, "deploy", "id_ed25519", "prod");

        Assert.Equal("[]", server.AllowedSshSourcesJson);
        Assert.Throws<ArgumentException>(() => server.SetAllowedSshSources(["10.0.0.1", " "]));
        Assert.Equal("[]", server.AllowedSshSourcesJson);
        server.SetAllowedSshSources(["10.0.0.1"]);
        Assert.Equal("""["10.0.0.1"]""", server.AllowedSshSourcesJson);
    }

    private static ServerInput Input(string name, string? passphrase) => new(
        name, "edge.example.internal", 22, "deploy", "id_ed25519", passphrase, null, "prod", ["web", "public"]);

    private static async Task<SqliteTestDatabase> CreateDatabaseAsync()
    {
        var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var context = new AppDbContext(options);
        await context.Database.EnsureCreatedAsync();
        return new SqliteTestDatabase(connection, context);
    }

    private sealed class SqliteTestDatabase(Microsoft.Data.Sqlite.SqliteConnection connection, AppDbContext context) : IAsyncDisposable
    {
        public AppDbContext Context { get; } = context;

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class TestSecretProtector : ISecretProtector
    {
        public string Protect(string plaintext) => $"protected:{plaintext}";
        public string Unprotect(string protectedValue) => protectedValue["protected:".Length..];
    }

    private sealed class TestKeyStore(params string[] files) : IServerKeyStore
    {
        public IReadOnlyList<string> ListFileNames() => files;
        public bool Contains(string fileName) => files.Contains(fileName, StringComparer.Ordinal);
        public string GetFullPath(string fileName) => Contains(fileName)
            ? Path.Combine(Path.GetTempPath(), fileName)
            : throw new FileNotFoundException("Test key file not found.", fileName);
    }
}
