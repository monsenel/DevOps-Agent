using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Monitoring;
using DevOpsAgent.Domain.Monitoring;

namespace DevOpsAgent.Tests;

public sealed class ContainerRestartMonitorTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid Server = Guid.NewGuid();

    [Fact]
    public void ThreeRestartsWithinTenMinutesOpenTheAlert()
    {
        var history = Cycles(("web", "id1", "running", new[] { 0, 1, 2, 3 }));

        var loop = Assert.Single(ContainerRestartMonitor.Evaluate(history, Start.AddMinutes(3)));

        Assert.Equal("web", loop.Name);
        Assert.Equal(3, loop.RestartsInWindow);
    }

    [Fact]
    public void TwoRestartsDoNotAlertButARestartingStateInTwoCyclesDoes()
    {
        Assert.Empty(ContainerRestartMonitor.Evaluate(Cycles(("web", "id1", "running", new[] { 0, 1, 2 })), Start.AddMinutes(2)));

        // One sample caught in the short pause after a single crash is not a loop; the same state a minute later is.
        var once = Cycles(("web", "id1", "running", new[] { 1 }));
        once.Add(Cycle(Start.AddMinutes(1), ("web", "id1", "restarting", 2)));
        Assert.Empty(ContainerRestartMonitor.Evaluate(once, Start.AddMinutes(1)));
        once.Add(Cycle(Start.AddMinutes(2), ("web", "id1", "running", 2)));
        Assert.Empty(ContainerRestartMonitor.Evaluate(once, Start.AddMinutes(2)));

        var restarting = Cycles(("crashloop", "id1", "restarting", new[] { 70, 70 }));
        Assert.Equal("crashloop", Assert.Single(ContainerRestartMonitor.Evaluate(restarting, Start.AddMinutes(1))).Name);
    }

    [Fact]
    public void RestartsSpreadOverMoreThanTenMinutesDoNotAlert()
    {
        // One restart every 6 minutes: never three within a 10-minute window.
        var history = Enumerable.Range(0, 5).Select(index => Cycle(Start.AddMinutes(index * 6), ("worker", "id1", "running", index))).ToList();

        Assert.Empty(ContainerRestartMonitor.Evaluate(history, Start.AddMinutes(24)));
    }

    [Fact]
    public void ARecreatedContainerResetsItsCounterWithoutCountingAsRestarts()
    {
        // Count climbs to 2, the container is recreated (new ID, counter back to 0) and climbs to 2 again:
        // never three restarts of one container, and the reset is not a negative or extra restart.
        var history = new List<ContainerCycle>
        {
            Cycle(Start, ("web", "old", "running", 0)),
            Cycle(Start.AddMinutes(1), ("web", "old", "running", 2)),
            Cycle(Start.AddMinutes(2), ("web", "new", "running", 0)),
            Cycle(Start.AddMinutes(3), ("web", "new", "running", 1)),
            Cycle(Start.AddMinutes(4), ("web", "new", "running", 2)),
        };

        var loops = ContainerRestartMonitor.Evaluate(history, Start.AddMinutes(4));

        // 2 (old) + 2 (new) = 4 restarts in the window, so it does alert; the recreate itself added nothing.
        Assert.Equal(4, Assert.Single(loops).RestartsInWindow);
        Assert.Empty(ContainerRestartMonitor.Evaluate(history.Take(3).ToList(), Start.AddMinutes(2)));
    }

    [Fact]
    public void AlertResolvesTenMinutesAfterTheLastRestartOrWhenTheContainerIsRemoved()
    {
        var history = Cycles(("web", "id1", "running", new[] { 0, 1, 2, 3 }));
        for (var minute = 4; minute <= 13; minute++) history.Add(Cycle(Start.AddMinutes(minute), ("web", "id1", "running", 3)));

        Assert.Single(ContainerRestartMonitor.Evaluate(history, Start.AddMinutes(12)));
        Assert.Empty(ContainerRestartMonitor.Evaluate(history, Start.AddMinutes(13)));

        var removed = Cycles(("crashloop", "id1", "restarting", new[] { 0, 1, 2, 3 }));
        removed.Add(Cycle(Start.AddMinutes(4)));
        Assert.Empty(ContainerRestartMonitor.Evaluate(removed, Start.AddMinutes(4)));
    }

    [Fact]
    public void OneRestartAfterAResolvedLoopDoesNotReopenTheAlert()
    {
        // Restarts in minutes 1-3 open the alert, which resolves at minute 13; one more restart at minute 20 is a single
        // restart, however recent the earlier loop still is in the stored history.
        var history = Cycles(("web", "id1", "running", new[] { 0, 1, 2, 3 }));
        for (var minute = 4; minute <= 19; minute++) history.Add(Cycle(Start.AddMinutes(minute), ("web", "id1", "running", 3)));
        history.Add(Cycle(Start.AddMinutes(20), ("web", "id1", "running", 4)));

        IReadOnlyList<RestartLoop> At(int minute) =>
            ContainerRestartMonitor.Evaluate(history.Where(cycle => cycle.AtUtc <= Start.AddMinutes(minute)).ToList(), Start.AddMinutes(minute));

        Assert.Single(At(3));
        Assert.Empty(At(13));
        Assert.Empty(At(20));
    }

    [Fact]
    public void HealthyLongRunningContainersWithOldRestartCountsDoNotAlert()
    {
        // A container that restarted many times long ago keeps a high but steady counter.
        var history = Cycles(("worker-pg", "id1", "running", new[] { 517, 517, 517, 517 }));

        Assert.Empty(ContainerRestartMonitor.Evaluate(history, Start.AddMinutes(3)));
    }

    [Fact]
    public async Task MonitorPersistsCyclesAndRaisesAMediumAlertPerContainer()
    {
        var time = new ManualTime(Start);
        var monitor = new ContainerRestartMonitor(new MemorySettings(), time);
        for (var minute = 0; minute <= 3; minute++)
        {
            await monitor.RecordAsync(Server, [new ContainerStatus("id1", "crashloop", "running", minute), new ContainerStatus("id2", "api", "running", 0)],
                CancellationToken.None);
            time.Advance(TimeSpan.FromMinutes(1));
        }

        var condition = Assert.Single(await monitor.GetConditionsAsync(Server, "srv-app", CancellationToken.None));

        Assert.Equal(ContainerRestartMonitor.Rule, condition.Rule);
        Assert.Equal(AlertSeverity.Medium, condition.Severity);
        Assert.Equal($"{ContainerRestartMonitor.Rule}:{Server:N}:crashloop", condition.Key);
        Assert.Contains("3 restart(s) in the last 10 minutes", condition.Details, StringComparison.Ordinal);

        // Docker removed from the server: history is cleared and nothing alerts.
        await monitor.RecordAsync(Server, null, CancellationToken.None);
        Assert.Empty(await monitor.GetConditionsAsync(Server, "srv-app", CancellationToken.None));
    }

    [Fact]
    public void QueryParsesInspectOutputAndReportsMissingDocker()
    {
        var containers = ContainerStatusQuery.Parse("abc123|/crashloop|restarting|7\ndef456|/api|running|0\n");

        Assert.Equal(new[] { new ContainerStatus("abc123", "crashloop", "restarting", 7), new ContainerStatus("def456", "api", "running", 0) }, containers);
        Assert.Null(ContainerStatusQuery.Parse(ContainerStatusQuery.NoDockerMarker + "\n"));
        Assert.Empty(ContainerStatusQuery.Parse(string.Empty)!);
        Assert.DoesNotContain("sudo", ContainerStatusQuery.Command, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MetricCycleRetriesTheQueryWithSudoOnlyWhenDockerIsRefusedAndSudoIsAllowed()
    {
        var server = new DevOpsAgent.Domain.Servers.Server("srv-app", "srv-app", 22, "dva", "key", "staging");
        server.RecordCapabilities(true, "Debian GNU/Linux 12", Start);
        var runner = new DockerNeedsSudoRunner();
        var time = new ManualTime(Start);
        var monitor = new ContainerRestartMonitor(new MemorySettings(), time);
        var service = new MetricService(new OneServer(server), new NoMetrics(), runner, monitor, time);

        await service.PollAsync(CancellationToken.None);

        var sudo = Assert.Single(runner.Calls, call => call.Options.UseSudo);
        Assert.Equal(ContainerStatusQuery.Command, sudo.Command);
        // The sudo command is one of the app's own fixed command lines, so it never raises the sudo alert.
        Assert.True(DevOpsAgent.Infrastructure.Monitoring.AppSudoCommands.IsAppCommand(
            "/usr/bin/sh -c '" + sudo.Command.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'"));
    }

    private sealed class DockerNeedsSudoRunner : ISshRunner
    {
        public List<(string Command, SshRunOptions Options)> Calls { get; } = [];

        public Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options, CancellationToken cancellationToken)
        {
            Calls.Add((command, options));
            var refused = command == ContainerStatusQuery.Command && !options.UseSudo;
            return Task.FromResult(new RemoteCommandResult(refused ? 1 : 0, refused ? string.Empty : "id1|/api|running|0\n",
                refused ? "permission denied while trying to connect to the Docker daemon socket" : string.Empty, TimeSpan.Zero));
        }
    }

    private sealed class OneServer(DevOpsAgent.Domain.Servers.Server server) : IServerRepository
    {
        public Task<IReadOnlyList<DevOpsAgent.Domain.Servers.Server>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DevOpsAgent.Domain.Servers.Server>>([server]);
        public Task<DevOpsAgent.Domain.Servers.Server?> GetAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult<DevOpsAgent.Domain.Servers.Server?>(server);
        public Task AddAsync(DevOpsAgent.Domain.Servers.Server item, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SaveAsync(DevOpsAgent.Domain.Servers.Server item, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteAsync(DevOpsAgent.Domain.Servers.Server item, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class NoMetrics : IMetricRepository
    {
        public Task AddSamplesAsync(IReadOnlyCollection<MetricSample> samples, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<MetricSample>> GetRecentAsync(Guid serverId, DateTimeOffset sinceUtc, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MetricSample>>([]);
        public Task<IReadOnlyList<MetricSample>> GetLatestAsync(Guid serverId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MetricSample>>([]);
        public Task<IReadOnlyList<MetricSample>> GetUnrolledAsync(DateTimeOffset beforeUtc, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MetricSample>>([]);
        public Task SaveRollupsAsync(IReadOnlyCollection<MetricRollup> rollups, DateTimeOffset rawPruneBeforeUtc, DateTimeOffset rollupPruneBeforeUtc,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static List<ContainerCycle> Cycles(params (string Name, string Id, string State, int[] Counts)[] containers)
    {
        var length = containers.Max(container => container.Counts.Length);
        return Enumerable.Range(0, length).Select(index => new ContainerCycle(Start.AddMinutes(index),
            containers.Select(container => new ContainerStatus(container.Id, container.Name, container.State,
                container.Counts[Math.Min(index, container.Counts.Length - 1)])).ToArray())).ToList();
    }

    private static ContainerCycle Cycle(DateTimeOffset at, params (string Name, string Id, string State, int Count)[] containers) =>
        new(at, containers.Select(container => new ContainerStatus(container.Id, container.Name, container.State, container.Count)).ToArray());

    private sealed class MemorySettings : IAppSettingRepository
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken) => Task.FromResult(_values.GetValueOrDefault(key));
        public Task SetAsync(string key, string value, CancellationToken cancellationToken) { _values[key] = value; return Task.CompletedTask; }
    }

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
}
