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
    public void ParseCgroupOutput_MapsV1V2TemplatedUnitsAndIgnoresContainers()
    {
        var mapping = SystemdCollectorParser.ParseCgroupOutput(Fixture("systemd-proc-cgroup.txt"));

        Assert.Equal("nginx.service", mapping.PidToUnit[220]);
        Assert.Equal("nginx.service", mapping.PidToUnit[221]);
        Assert.Equal("sshd.service", mapping.PidToUnit[1000]);
        Assert.Equal("postgresql@16-main.service", mapping.PidToUnit[5500]);
        Assert.DoesNotContain(4500, mapping.PidToUnit.Keys);
        Assert.Empty(mapping.RestrictedPids);
    }

    [Fact]
    public void Parse_WorkerPidMappedViaCgroupIncludesWorkerInListenerPids()
    {
        var showOutputs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["nginx.service"] = Fixture("systemd-show-nginx.txt")
        };

        var result = SystemdCollectorParser.Parse(
            Fixture("systemd-list-units.txt"),
            showOutputs,
            new HashSet<int> { 221 },
            Fixture("systemd-proc-cgroup.txt"));

        var nginx = Assert.Single(result.Units);
        Assert.Equal("nginx.service", nginx.Name);
        Assert.Equal(220, nginx.MainPid);
        Assert.Equal(new[] { 221 }, nginx.ListenerPids);
    }

    [Fact]
    public async Task DiscoveryPassesListenerResultsToSystemdAndRunsSystemctlShowOnlyForOwningUnits()
    {
        await using var database = await CreateDatabaseAsync();
        var server = new Server("edge", "edge.example.internal", 22, "deploy", "id_ed25519", "prod");
        database.Context.Servers.Add(server);
        await database.Context.SaveChangesAsync();
        var runner = new FixtureSshRunner(cgroupOutput: Fixture("systemd-proc-cgroup.txt"));
        var listenerCollector = new ListenerResultCollector();
        var service = new DiscoveryService(
            new ServerRepository(database.Context),
            new SnapshotRepository(database.Context),
            new GraphRepository(database.Context),
            new GraphBuilder(),
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

        // T7.R9: systemctl show MUST run only for the listener-owning units, never for postgresql.service or dbus.service.
        var showCommands = runner.Commands.Where(command => command.StartsWith("systemctl show ", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, showCommands.Length);
        Assert.Contains(showCommands, command => command.Contains("nginx.service", StringComparison.Ordinal));
        Assert.Contains(showCommands, command => command.Contains("sshd.service", StringComparison.Ordinal));
        Assert.DoesNotContain(showCommands, command => command.Contains("postgresql.service", StringComparison.Ordinal));
        Assert.DoesNotContain(showCommands, command => command.Contains("dbus.service", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SystemdCollectorFallsBackToRunningUnitsWhenProcCgroupIsRestricted()
    {
        await using var database = await CreateDatabaseAsync();
        var server = new Server("edge", "edge.example.internal", 22, "deploy", "id_ed25519", "prod");
        database.Context.Servers.Add(server);
        await database.Context.SaveChangesAsync();
        var restrictedCgroup = "__PID__:220\n__ERR__\n__PID__:1000\n__ERR__\n";
        var runner = new FixtureSshRunner(cgroupOutput: restrictedCgroup);
        var listenerCollector = new ListenerResultCollector();
        var service = new DiscoveryService(
            new ServerRepository(database.Context),
            new SnapshotRepository(database.Context),
            new GraphRepository(database.Context),
            new GraphBuilder(),
            runner,
            [listenerCollector, new SystemdCollector()],
            TimeProvider.System);

        var snapshot = await service.RunAsync(progress: null, CancellationToken.None);
        var systemdRun = await database.Context.CollectorRuns.SingleAsync(run => run.CollectorName == "systemd");
        var data = JsonDocument.Parse(systemdRun.ResultJson!).RootElement;

        Assert.Equal(SnapshotStatus.Completed, snapshot.Status);
        var unitNames = data.GetProperty("Units").EnumerateArray()
            .Select(unit => unit.GetProperty("Name").GetString()).ToArray();
        Assert.Equal(new[] { "nginx.service", "sshd.service" }, unitNames);
        // Fallback stopped early once both restricted listener PIDs were found, without querying unrelated trailing units.
        Assert.Contains(runner.Commands, command => command.Contains("/proc/$p/cgroup", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SystemdCollectorMapsWorkerListenerPidWithoutFallingBack()
    {
        await using var database = await CreateDatabaseAsync();
        var server = new Server("edge", "edge.example.internal", 22, "deploy", "id_ed25519", "prod");
        database.Context.Servers.Add(server);
        await database.Context.SaveChangesAsync();
        var runner = new FixtureSshRunner(cgroupOutput: "__PID__:221\n0::/system.slice/nginx.service\n");
        var listenerCollector = new ListenerResultCollector(
            new ListenerInfo("tcp", "LISTEN", "0.0.0.0", 80, "0.0.0.0", "nginx", 221));
        var service = new DiscoveryService(
            new ServerRepository(database.Context),
            new SnapshotRepository(database.Context),
            new GraphRepository(database.Context),
            new GraphBuilder(),
            runner,
            [listenerCollector, new SystemdCollector()],
            TimeProvider.System);

        var snapshot = await service.RunAsync(progress: null, CancellationToken.None);
        var graph = (await new GraphRepository(database.Context).GetAsync(snapshot.Id, GraphView.All, CancellationToken.None))!;

        var nginxNode = Assert.Single(graph.Nodes, node => node.Data.GetProperty("label").GetString() == "nginx.service");
        var nginxNodeId = Guid.ParseExact(nginxNode.Data.GetProperty("id").GetString()!, "N");
        Assert.Contains(graph.Listeners, listener => listener.Port == 80 && listener.NodeId == nginxNodeId);
        var showCommands = runner.Commands.Where(command => command.StartsWith("systemctl show ", StringComparison.Ordinal)).ToArray();
        Assert.Single(showCommands);
        Assert.Contains("nginx.service", showCommands[0], StringComparison.Ordinal);
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

    private sealed class ListenerResultCollector(params ListenerInfo[] listeners) : ICollector
    {
        private readonly ListenerInfo[] _listeners = listeners.Length > 0
            ? listeners
            : [
                new ListenerInfo("tcp", "LISTEN", "0.0.0.0", 80, "0.0.0.0", "nginx", 220),
                new ListenerInfo("tcp", "LISTEN", "0.0.0.0", 22, "0.0.0.0", "sshd", 1000)
            ];

        public string Name => "listeners";

        public Task<bool> IsApplicableAsync(ServerContext context, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<CollectorOutput> CollectAsync(ServerContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new CollectorOutput("listeners", string.Empty, 0, TimeSpan.Zero));

        public CollectorResult Parse(CollectorOutput output) => new(JsonSerializer.SerializeToElement(
            new ListenerDiscoveryResult(_listeners)));
    }

    private sealed class FixtureSshRunner(string? cgroupOutput = null) : ISshRunner
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
                var value when value.Contains("/proc/$p/cgroup", StringComparison.Ordinal) => cgroupOutput ?? string.Empty,
                var value when value.Contains("nginx.service", StringComparison.Ordinal) => Fixture("systemd-show-nginx.txt"),
                var value when value.Contains("sshd.service", StringComparison.Ordinal) => Fixture("systemd-show-sshd.txt"),
                var value when value.Contains("postgresql.service", StringComparison.Ordinal) => Fixture("systemd-show-postgresql.txt"),
                _ => string.Empty
            };
            return Task.FromResult(new RemoteCommandResult(0, output, string.Empty, TimeSpan.FromMilliseconds(1)));
        }
    }
}
