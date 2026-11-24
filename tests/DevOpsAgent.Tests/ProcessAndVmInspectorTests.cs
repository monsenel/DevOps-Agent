using System.Text;
using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Application.Monitoring;
using DevOpsAgent.Domain.Discovery;
using DevOpsAgent.Domain.Monitoring;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Infrastructure.Collectors;
using DevOpsAgent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DevOpsAgent.Tests;

public sealed class ProcessAndVmInspectorTests
{
    [Fact]
    public void DockerCollectorParser_ExtractsStartedAtFinishedAtAndHealthStatus()
    {
        const string containersJson = """
        [
          {
            "Id": "1111111111111111111111111111111111111111111111111111111111111111",
            "Name": "/nexus-ci",
            "State": {
              "Status": "running",
              "Pid": 412,
              "StartedAt": "2026-11-20T10:15:30Z",
              "FinishedAt": "0001-01-01T00:00:00Z",
              "Health": { "Status": "healthy" }
            },
            "Config": { "Image": "sonatype/nexus3:3.70.0", "Labels": {}, "Env": [] },
            "NetworkSettings": { "Networks": {}, "Ports": {} }
          }
        ]
        """;

        var result = DockerCollectorParser.Parse(containersJson, "[]");
        var nexus = Assert.Single(result.Containers);

        Assert.Equal("healthy", nexus.HealthStatus);
        Assert.Equal(DateTimeOffset.Parse("2026-11-20T10:15:30Z"), nexus.StartedAtUtc);
        Assert.Null(nexus.FinishedAtUtc);
    }

    [Fact]
    public void SystemdCollectorParser_ParsesActiveEnterTimestampAndRunningServices()
    {
        const string runningUnits = "nginx.service loaded active running A high performance web server\n"
            + "docker.service loaded active running Docker Application Container Engine\n";
        var showOutputs = new Dictionary<string, string>
        {
            ["nginx.service"] = "MainPID=220\nExecStart=/usr/sbin/nginx\nFragmentPath=/lib/systemd/system/nginx.service\nActiveEnterTimestamp=Tue 2026-11-24 10:15:32 UTC\n"
        };

        var result = SystemdCollectorParser.Parse(runningUnits, showOutputs, new HashSet<int> { 220 });

        var nginx = Assert.Single(result.Units);
        Assert.Equal(DateTimeOffset.Parse("2026-11-24T10:15:32Z"), nginx.ActiveSinceUtc);
        Assert.NotNull(result.RunningServices);
        Assert.Contains("docker.service", result.RunningServices);
        Assert.Contains("nginx.service", result.RunningServices);
    }

    [Fact]
    public void ProcessCollectorParser_ClassifiesRedactsAndCapsAtSixty()
    {
        var sb = new StringBuilder();
        sb.AppendLine("220 1 www-data Ss 1.5 0.8 86400 nginx nginx: worker process");
        sb.AppendLine("412 1 root Ssl 12.4 24.1 172800 java java --password=super-secret-pass -jar nexus.jar");
        sb.AppendLine("101 1 root Ss 9.5 0.2 900000 systemd-journald /lib/systemd/systemd-journald");
        for (var i = 1; i <= 80; i++)
        {
            sb.AppendLine($"{2000 + i} 1 root S {i * 0.1:0.0} 0.1 500 worker-{i} /usr/bin/custom-job-{i}");
        }

        const string cgroups =
            "__PID__:220\n0::/system.slice/nginx.service\n" +
            "__PID__:412\n0::/system.slice/docker-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.scope\n" +
            "__PID__:101\n0::/init.scope\n";

        const string dockerAll =
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa|nexus|sonatype/nexus3|running|Up 2 days (healthy)|0.0.0.0:8081->8081/tcp\n" +
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb|old-worker|busybox|exited|Exited (0) 3 hours ago|\n";

        var result = ProcessCollectorParser.Parse("172800.45 340000.12", sb.ToString(), cgroups, dockerAll, true, true);

        Assert.Equal(172800.45, result.SystemUptimeSeconds);
        Assert.Equal(83, result.TotalProcessCount);
        Assert.Equal(ProcessCollectorParser.MaxStoredProcesses, result.Processes.Count);

        var nginx = Assert.Single(result.Processes, p => p.Pid == 220);
        Assert.Equal(ProcessCategory.Service, nginx.Category);
        Assert.Equal("nginx.service", nginx.UnitName);

        var nexus = Assert.Single(result.Processes, p => p.Pid == 412);
        Assert.Equal(ProcessCategory.Docker, nexus.Category);
        Assert.Equal("nexus", nexus.ContainerName);
        Assert.DoesNotContain("super-secret-pass", nexus.CommandLine);
        Assert.Contains("[REDACTED]", nexus.CommandLine);

        var journald = Assert.Single(result.Processes, p => p.Pid == 101);
        Assert.Equal(ProcessCategory.Os, journald.Category);

        Assert.Equal(2, result.DockerContainers.Count);
        Assert.Contains(result.DockerContainers, c => c.Name == "nexus" && c.State == "running");
        Assert.Contains(result.DockerContainers, c => c.Name == "old-worker" && c.State == "exited");
    }

    [Fact]
    public async Task ProcessCollector_CollectAsyncRedactsSecretsBeforeStorage()
    {
        var runner = new StubRunner(command => command switch
        {
            ProcessCollector.UptimeCommand => "4200.5 8400.0\n",
            ProcessCollector.PsCommand => "500 1 root S 5.0 2.0 300 dotnet dotnet app.dll DB_PASSWORD=top-secret-canary\n",
            ProcessCollector.DockerCheckCommand => string.Empty,
            DockerCollector.InfoCommand => string.Empty,
            ProcessCollector.DockerAllContainersCommand => "cccccccccccc|api|api:1|running|Up 5 minutes|0.0.0.0:5000->5000/tcp\n",
            _ when command.Contains("/proc/$p/cgroup", StringComparison.Ordinal) => "__PID__:500\n0::/system.slice/app.service\n",
            _ => string.Empty
        });
        var server = new Server("app-1", "10.0.0.10", 22, "deploy", "id_ed25519", "staging");
        var collector = new ProcessCollector();
        var output = await collector.CollectAsync(new ServerContext(server, runner), CancellationToken.None);
        var parsed = collector.Parse(output);

        Assert.Equal(0, output.ExitCode);
        Assert.DoesNotContain("top-secret-canary", output.StandardOutput);
        Assert.True(parsed.IsSuccess, parsed.Error);
    }

    [Fact]
    public async Task VmInspectorService_LoadsTelemetryServicesPortsProcessesAndSupportsLiveRefresh()
    {
        await using var db = await CreateDatabaseAsync();
        var server = new Server("edge-1", "10.10.0.5", 22, "deploy", "id_ed25519", "staging");
        server.RecordCapabilities(true, "Debian GNU/Linux 12", DateTimeOffset.UtcNow);
        db.Context.Servers.Add(server);

        var snapshot = new Snapshot([server.Id], DateTimeOffset.UtcNow.AddMinutes(-10));
        var now = DateTimeOffset.UtcNow;
        var runs = new[]
        {
            Run(snapshot.Id, server.Id, "docker", new DockerDiscoveryResult(
                [new DockerContainerInfo("aaaaaaaaaaaa", "api", "my/api:1.2", "running", "web", "api", 201, [],
                    [new DockerPublishedPort(8080, "tcp", "127.0.0.1", 8080)], [], now.AddHours(-6), null, "healthy")],
                []), now),
            Run(snapshot.Id, server.Id, "systemd", new SystemdDiscoveryResult(
                [new SystemdUnitInfo("nginx.service", 101, "/usr/sbin/nginx", "/lib/systemd/system/nginx.service", [101], now.AddDays(-3))],
                ["nginx.service", "docker.service", "sshd.service"]), now),
            Run(snapshot.Id, server.Id, "listeners", new ListenerDiscoveryResult(
                [
                    new ListenerInfo("tcp", "LISTEN", "0.0.0.0", 443, "0.0.0.0", "nginx", 101),
                    new ListenerInfo("tcp", "LISTEN", "10.10.0.5", 9100, "0.0.0.0", "node_exporter", 301),
                    new ListenerInfo("tcp", "LISTEN", "127.0.0.1", 8080, "0.0.0.0", "docker-proxy", 401)
                ]), now),
            Run(snapshot.Id, server.Id, "firewall", new FirewallDiscoveryResult(
                "ufw", true, "deny", "allow",
                [new FirewallRuleInfo("in", "ALLOW", "tcp", "443", "Anywhere")]), now),
            Run(snapshot.Id, server.Id, "processes", new ProcessDiscoveryResult(
                259200,
                [
                    new ProcessInfo(101, 1, "root", "Ss", 0.4, 0.5, 259200, "nginx", "nginx: master", ProcessCategory.Service, "nginx.service"),
                    new ProcessInfo(201, 1, "1000", "Ssl", 4.2, 6.1, 21600, "dotnet", "dotnet Api.dll", ProcessCategory.Docker, null, "api"),
                    new ProcessInfo(1, 0, "root", "Ss", 0.0, 0.1, 259200, "systemd", "/sbin/init", ProcessCategory.Os)
                ],
                [
                    new DockerInventoryContainerInfo("aaaaaaaaaaaa", "api", "my/api:1.2", "running", "Up 6 hours (healthy)", "127.0.0.1:8080->8080/tcp"),
                    new DockerInventoryContainerInfo("dddddddddddd", "migrator", "my/api:1.2", "exited", "Exited (0) 2 hours ago")
                ],
                true, true, 95), now)
        };
        snapshot.Complete(now, 1, 0);
        db.Context.Snapshots.Add(snapshot);
        db.Context.CollectorRuns.AddRange(runs);
        db.Context.MetricSamples.AddRange(
            new MetricSample(server.Id, "cpu_percent", 22.5, now),
            new MetricSample(server.Id, "cpu_cores", 4, now),
            new MetricSample(server.Id, "memory_percent", 50.0, now),
            new MetricSample(server.Id, "memory_used_bytes", 4d * 1024 * 1024 * 1024, now),
            new MetricSample(server.Id, "memory_total_bytes", 8d * 1024 * 1024 * 1024, now));
        await db.Context.SaveChangesAsync();

        var liveProbe = new StubLiveVmProbe(new LiveVmProbeResult(
            new ParsedMetrics(new Dictionary<string, double> { ["cpu_percent"] = 33.3, ["cpu_cores"] = 4 },
                new Dictionary<string, long>(), new Dictionary<string, long>()),
            new ProcessDiscoveryResult(
                259300,
                [new ProcessInfo(999, 1, "root", "R", 33.0, 2.0, 12, "nexus", "java -jar nexus.jar", ProcessCategory.Service)],
                [new DockerInventoryContainerInfo("aaaaaaaaaaaa", "api", "my/api:1.2", "running", "Up 6 hours (healthy)")],
                true, true, 96),
            [new ListenerInfo("tcp", "LISTEN", "0.0.0.0", 443, "0.0.0.0", "nginx", 101)],
            now.AddSeconds(30)));

        var inspector = new VmInspectorService(
            new ServerRepository(db.Context),
            new SnapshotRepository(db.Context),
            new MetricRepository(db.Context),
            new AlertRepository(db.Context),
            liveProbe,
            TimeProvider.System);

        var view = await inspector.LoadAsync(server.Id, CancellationToken.None);
        Assert.NotNull(view);
        Assert.Equal(22.5, view.Resources.CpuPercent);
        Assert.Equal(50.0, view.Resources.MemoryPercent);
        Assert.Equal(259200, view.Resources.SystemUptimeSeconds);
        Assert.Equal(2, view.Containers.Count);
        Assert.Contains(view.Containers, c => c.Name == "api" && c.State == "running" && c.HealthStatus == "healthy");
        Assert.Contains(view.Containers, c => c.Name == "migrator" && c.State == "exited");
        Assert.Contains(view.SystemdServices, s => s.Name == "nginx.service" && !s.IsOsService);
        Assert.Contains(view.SystemdServices, s => s.Name == "sshd.service" && s.IsOsService);

        Assert.Contains(view.Ports, p => p.Port == 443 && p.Exposure == PortExposure.External && p.FirewallNote == "ufw: ALLOW");
        Assert.Contains(view.Ports, p => p.Port == 9100 && p.Exposure == PortExposure.Private);
        Assert.Contains(view.Ports, p => p.Port == 8080 && p.Exposure == PortExposure.Internal);
        Assert.Equal(3, view.Processes.Count);

        var refreshed = await inspector.RefreshLiveAsync(server.Id, CancellationToken.None);
        Assert.NotNull(refreshed);
        Assert.True(refreshed.IsLiveRefresh);
        Assert.Equal(33.3, refreshed.Resources.CpuPercent);
        Assert.Contains(refreshed.Processes, p => p.Name == "nexus" && p.Category == ProcessCategory.Service);
    }

    private static CollectorRun Run<T>(Guid snapshotId, Guid serverId, string name, T data, DateTimeOffset atUtc) =>
        new(snapshotId, serverId, name, CollectorRunStatus.Succeeded, string.Empty, string.Empty, 0, null,
            JsonSerializer.Serialize(data, new JsonSerializerOptions(JsonSerializerDefaults.Web)), TimeSpan.FromMilliseconds(10), atUtc);

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
        public async ValueTask DisposeAsync() { await Context.DisposeAsync(); await connection.DisposeAsync(); }
    }

    private sealed class StubRunner(Func<string, string> handler) : ISshRunner
    {
        public Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options, CancellationToken cancellationToken) =>
            Task.FromResult(new RemoteCommandResult(0, handler(command), string.Empty, TimeSpan.FromMilliseconds(1)));
    }

    private sealed class StubLiveVmProbe(LiveVmProbeResult result) : ILiveVmProbe
    {
        public Task<LiveVmProbeResult> ProbeAsync(Server server, CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }
}
