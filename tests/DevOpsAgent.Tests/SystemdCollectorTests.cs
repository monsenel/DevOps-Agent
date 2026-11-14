using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Domain.Discovery;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Infrastructure.Collectors;
using DevOpsAgent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DevOpsAgent.Tests;

public sealed class SystemdCollectorTests
{
    [Fact]
    public void Parse_FixturesKeepsOnlyUnitsWhoseMainPidOwnsAListener()
    {
        var showOutputs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["nginx.service"] = Fixture("systemd-show-nginx.txt"),
            ["sshd.service"] = Fixture("systemd-show-sshd.txt"),
            ["postgresql.service"] = Fixture("systemd-show-postgresql.txt")
        };

        var result = SystemdCollectorParser.Parse(Fixture("systemd-list-units.txt"), showOutputs, new HashSet<int> { 220, 1000 });

        Assert.Equal(new[] { "nginx.service", "sshd.service" }, result.Units.Select(unit => unit.Name));
        var nginx = result.Units.Single(unit => unit.Name == "nginx.service");
        Assert.Equal(220, nginx.MainPid);
        Assert.Contains("/usr/sbin/nginx", nginx.ExecStart);
        Assert.Equal("/lib/systemd/system/nginx.service", nginx.FragmentPath);
        Assert.Equal(new[] { 220 }, nginx.ListenerPids);
    }

    [Fact]
    public async Task DiscoveryPassesListenerResultsToSystemdAndCollectsOnlyOwningUnits()
    {
        await using var database = await CreateDatabaseAsync();
        var server = new Server("edge", "edge.example.internal", 22, "deploy", "id_ed25519", "prod");
        database.Context.Servers.Add(server);
        await database.Context.SaveChangesAsync();
        var runner = new FixtureSshRunner();
        var listenerCollector = new ListenerResultCollector();
        var service = new DiscoveryService(
            new ServerRepository(database.Context),
            new SnapshotRepository(database.Context),
            runner,
            [listenerCollector, new SystemdCollector()],
            TimeProvider.System);

        var snapshot = await service.RunAsync(progress: null, CancellationToken.None);
        var systemdRun = await database.Context.CollectorRuns.SingleAsync(run => run.CollectorName == "systemd");
        var data = JsonDocument.Parse(systemdRun.ResultJson!).RootElement;

        Assert.Equal(SnapshotStatus.Completed, snapshot.Status);
        Assert.Equal(2, data.GetProperty("Units").GetArrayLength());
        var unitNames = data.GetProperty("Units").EnumerateArray()
            .Select(unit => unit.GetProperty("Name").GetString()).ToArray();
        Assert.Equal(new[] { "nginx.service", "sshd.service" }, unitNames);
    }

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static async Task<SqliteTestDatabase> CreateDatabaseAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        var context = new AppDbContext(options);
        await context.Database.EnsureCreatedAsync();
        return new SqliteTestDatabase(connection, context);
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

    private sealed class ListenerResultCollector : ICollector
    {
        public string Name => "listeners";

        public Task<bool> IsApplicableAsync(ServerContext context, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<CollectorOutput> CollectAsync(ServerContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new CollectorOutput("listeners", string.Empty, 0, TimeSpan.Zero));

        public CollectorResult Parse(CollectorOutput output) => new(JsonSerializer.SerializeToElement(
            new ListenerDiscoveryResult([
                new ListenerInfo("tcp", "LISTEN", "0.0.0.0", 80, "0.0.0.0", "nginx", 220),
                new ListenerInfo("tcp", "LISTEN", "0.0.0.0", 22, "0.0.0.0", "sshd", 1000)
            ])));
    }

    private sealed class FixtureSshRunner : ISshRunner
    {
        public List<string> Commands { get; } = [];

        public Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options,
            CancellationToken cancellationToken)
        {
            Commands.Add(command);
            var output = command switch
            {
                "command -v systemctl >/dev/null 2>&1" => string.Empty,
                "systemctl list-units --type=service --state=running --no-legend --plain" => Fixture("systemd-list-units.txt"),
                var value when value.Contains("nginx.service", StringComparison.Ordinal) => Fixture("systemd-show-nginx.txt"),
                var value when value.Contains("sshd.service", StringComparison.Ordinal) => Fixture("systemd-show-sshd.txt"),
                var value when value.Contains("postgresql.service", StringComparison.Ordinal) => Fixture("systemd-show-postgresql.txt"),
                _ => string.Empty
            };
            return Task.FromResult(new RemoteCommandResult(0, output, string.Empty, TimeSpan.FromMilliseconds(1)));
        }
    }
}
