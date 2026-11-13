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
        var service = new ServerService(repository, new TestSecretProtector(), new TestKeyStore("id_ed25519"));
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
    public async Task JumpServerMustExistAndCannotBeRemovedWhileReferenced()
    {
        await using var database = await CreateDatabaseAsync();
        var service = new ServerService(new ServerRepository(database.Context), new TestSecretProtector(), new TestKeyStore("id_ed25519"));
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
        var service = new ServerService(new ServerRepository(database.Context), new TestSecretProtector(), new TestKeyStore("id_ed25519"));
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
        var service = new ServerService(new ServerRepository(database.Context), new TestSecretProtector(), new TestKeyStore("id_ed25519"));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateAsync(Input("missing", null) with { KeyFile = "missing-key" }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateAsync(Input("path", null) with { KeyFile = "../id_ed25519" }, CancellationToken.None));
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
