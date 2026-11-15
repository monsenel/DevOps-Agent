using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Monitoring;
using DevOpsAgent.Application.Servers;
using DevOpsAgent.Domain.Monitoring;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Infrastructure.Ssh;
using Microsoft.Extensions.DependencyInjection;

namespace DevOpsAgent.Tests;

public sealed class HostReachabilityTests
{
    // Scaled stand-ins for the real 15 s connect timeout and 60 s metric interval (same 1:4 ratio).
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan CycleInterval = TimeSpan.FromMilliseconds(1600);

    [Fact]
    public void BackoffStartsAtOneMinuteDoublesToFifteenAndResetsOnSuccess()
    {
        var time = new ManualTime();
        var tracker = new HostReachabilityTracker(time);
        var id = Guid.NewGuid();

        var expected = new[] { 1, 2, 4, 8, 15, 15 };
        foreach (var minutes in expected)
        {
            Assert.True(tracker.TryBeginAttempt(id, bypassBackoff: false, out _));
            tracker.RecordFailure(id, new SocketException());
            var state = tracker.Get(id)!;
            Assert.Equal(TimeSpan.FromMinutes(minutes), state.NextRetryUtc - time.GetUtcNow());
            Assert.False(tracker.TryBeginAttempt(id, bypassBackoff: false, out var blocked));
            Assert.Same(state, blocked);
            time.Advance(TimeSpan.FromMinutes(minutes));
        }

        Assert.True(tracker.TryBeginAttempt(id, bypassBackoff: false, out _));
        // Only one retry at a time once the backoff has expired.
        Assert.False(tracker.TryBeginAttempt(id, bypassBackoff: false, out var inFlight));
        Assert.True(inFlight!.RetryInProgress);
        tracker.RecordSuccess(id);
        Assert.Null(tracker.Get(id));
    }

    [Fact]
    public void BypassAllowsAnAttemptDuringBackoffAndSinceIsKept()
    {
        var time = new ManualTime();
        var tracker = new HostReachabilityTracker(time);
        var id = Guid.NewGuid();
        tracker.RecordFailure(id, new SocketException());
        var since = tracker.Get(id)!.UnreachableSinceUtc;
        time.Advance(TimeSpan.FromSeconds(10));

        Assert.False(tracker.TryBeginAttempt(id, bypassBackoff: false, out _));
        Assert.True(tracker.TryBeginAttempt(id, bypassBackoff: true, out _));
        tracker.RecordFailure(id, new SocketException());

        Assert.Equal(since, tracker.Get(id)!.UnreachableSinceUtc);
        Assert.Equal(2, tracker.Get(id)!.ConsecutiveFailures);
    }

    [Fact]
    public async Task OneDeadHostAmongTenHealthyNeverSlowsAMetricCycle()
    {
        var lab = new Lab();
        var dead = lab.AddServer("dead", "192.0.2.1", dead: true);
        var healthy = Enumerable.Range(1, 10).Select(index => lab.AddServer($"srv-{index}", $"10.0.0.{index}")).ToArray();

        var durations = new List<TimeSpan>();
        for (var cycle = 0; cycle < 10; cycle++)
        {
            durations.Add(await lab.PollAsync());
            lab.Time.Advance(TimeSpan.FromSeconds(60));
        }

        Assert.All(durations, duration => Assert.True(duration < CycleInterval, $"cycle took {duration.TotalMilliseconds:0} ms"));
        // The dead host is attempted only at 0, 1, 3 and 7 minutes (backoff 1, 2, 4 then 8 minutes); every other cycle
        // skips it without waiting.
        Assert.Equal(4, lab.Transports.Attempts(dead));
        var waited = durations.Select((duration, minute) => (duration, minute)).Where(item => item.duration >= ConnectTimeout)
            .Select(item => item.minute).ToArray();
        Assert.Equal(new[] { 0, 1, 3, 7 }, waited);
        Assert.All(healthy, server => Assert.Equal(10, lab.Metrics.CyclesStored(server.Id)));
        Assert.Contains("dead is unreachable (since", lab.Runner.LastError(dead.Id), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServersBehindADeadJumpHostAreUnreachableWithoutConnectionAttempts()
    {
        var lab = new Lab();
        var jump = lab.AddServer("edge", "203.0.113.10", dead: true);
        var behind = Enumerable.Range(1, 10).Select(index => lab.AddServer($"app-{index}", $"10.1.0.{index}", jump: jump)).ToArray();

        var first = await lab.PollAsync();
        lab.Time.Advance(TimeSpan.FromSeconds(30));
        var second = await lab.PollAsync();

        Assert.True(first < CycleInterval, $"first cycle took {first.TotalMilliseconds:0} ms");
        Assert.True(second < ConnectTimeout, $"second cycle took {second.TotalMilliseconds:0} ms");
        // One attempt to the jump host for the whole cycle, none to the servers behind it.
        Assert.Equal(1, lab.Transports.Attempts(jump));
        Assert.All(behind, server => Assert.Equal(0, lab.Transports.Attempts(server)));
        Assert.All(behind, server => Assert.Contains($"{server.Name} is unreachable via jump host edge (since",
            lab.Runner.LastError(server.Id), StringComparison.Ordinal));
        Assert.Equal($"{behind[0].Name} is unreachable via jump host edge",
            ServerReachability.Describe(behind[0].Id, lab.Inventory(), lab.Reachability)![..$"{behind[0].Name} is unreachable via jump host edge".Length]);
    }

    [Fact]
    public async Task RetryNowBypassesBackoffAndRecoveryIsImmediate()
    {
        var lab = new Lab();
        var jump = lab.AddServer("edge", "203.0.113.10", dead: true);
        var app = lab.AddServer("app", "10.1.0.1", jump: jump);
        await lab.PollAsync();
        Assert.NotNull(ServerReachability.Describe(app.Id, lab.Inventory(), lab.Reachability));

        lab.Transports.Revive(jump);
        // Still in backoff: a normal cycle does not try yet.
        await lab.PollAsync();
        Assert.Equal(1, lab.Transports.Attempts(jump));

        // "Retry now" on the app server is a capability test that bypasses the backoff for its whole route.
        var result = await new ServerCapabilityService(lab.Runner, lab.Servers, lab.Time).TestAsync(app.Id, CancellationToken.None);

        Assert.True(result.Reachable, result.Error);
        Assert.Null(lab.Reachability.Get(jump.Id));
        Assert.Null(ServerReachability.Describe(app.Id, lab.Inventory(), lab.Reachability));
        await lab.PollAsync();
        Assert.Equal(1, lab.Metrics.CyclesStored(app.Id));
        Assert.Equal(1, lab.Transports.Attempts(app));
    }

    private sealed class Lab
    {
        private readonly List<Server> _servers = [];

        public Lab()
        {
            Servers = new FakeServers(_servers);
            Reachability = new HostReachabilityTracker(Time);
            var services = new ServiceCollection().AddSingleton<IServerRepository>(Servers).BuildServiceProvider();
            Pool = new SshConnectionPool(services.GetRequiredService<IServiceScopeFactory>(), Transports, Reachability,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<SshConnectionPool>.Instance);
            Runner = new RecordingRunner(new SshRunner(Pool));
        }

        public ManualTime Time { get; } = new();
        public FakeServers Servers { get; }
        public HostReachabilityTracker Reachability { get; }
        public FakeTransports Transports { get; } = new();
        public SshConnectionPool Pool { get; }
        public RecordingRunner Runner { get; }
        public FakeMetrics Metrics { get; } = new();

        public Server AddServer(string name, string host, bool dead = false, Server? jump = null)
        {
            var server = new Server(name, host, 22, "dva", "key", "staging");
            if (jump is not null) server.Update(name, host, 22, "dva", "key", jump.Id, "staging", "[]", null);
            _servers.Add(server);
            if (dead) Transports.Kill(server);
            return server;
        }

        public IReadOnlyDictionary<Guid, (string Name, Guid? JumpServerId)> Inventory() =>
            _servers.ToDictionary(server => server.Id, server => (server.Name, server.JumpServerId));

        public async Task<TimeSpan> PollAsync()
        {
            var stopwatch = Stopwatch.StartNew();
            await new MetricService(Servers, Metrics, Runner, Time).PollAsync(CancellationToken.None);
            return stopwatch.Elapsed;
        }
    }

    private sealed class FakeTransports : ISshTransportFactory
    {
        private readonly ConcurrentDictionary<string, bool> _dead = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, int> _attempts = new(StringComparer.Ordinal);

        public void Kill(Server server) => _dead[server.Host] = true;
        public void Revive(Server server) => _dead[server.Host] = false;
        public int Attempts(Server server) => _attempts.GetValueOrDefault(server.Host);

        public async Task<ISshTransport> ConnectAsync(Server server, ISshTransport? jump, CancellationToken cancellationToken)
        {
            _attempts.AddOrUpdate(server.Host, 1, static (_, count) => count + 1);
            if (jump is not null && !jump.IsConnected) throw new SocketException((int)SocketError.ConnectionReset);
            if (_dead.GetValueOrDefault(server.Host))
            {
                // A blackholed host: the connect waits for its full timeout, then fails.
                await Task.Delay(ConnectTimeout, cancellationToken);
                throw new SocketException((int)SocketError.TimedOut);
            }

            return new FakeTransport();
        }
    }

    private sealed class FakeTransport : ISshTransport
    {
        private static readonly string Sample = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "metrics-sample-1.txt"));
        public bool IsConnected { get; private set; } = true;

        public Task<RemoteCommandResult> ExecuteAsync(string command, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(new RemoteCommandResult(0,
                command == MetricsParser.CollectionCommand ? Sample : "PRETTY_NAME=\"Debian\"\nNAME=\"Debian GNU/Linux\"\nVERSION_ID=\"12\"\n",
                string.Empty, TimeSpan.FromMilliseconds(1)));

        public void Dispose() => IsConnected = false;
    }

    private sealed class RecordingRunner(ISshRunner inner) : ISshRunner
    {
        private readonly ConcurrentDictionary<Guid, string> _errors = new();

        public string LastError(Guid serverId) => _errors.GetValueOrDefault(serverId, string.Empty);

        public async Task<RemoteCommandResult> RunAsync(Guid serverId, string command, SshRunOptions options, CancellationToken cancellationToken)
        {
            try { return await inner.RunAsync(serverId, command, options, cancellationToken); }
            catch (Exception exception)
            {
                _errors[serverId] = exception.Message;
                throw;
            }
        }
    }

    private sealed class FakeServers(List<Server> servers) : IServerRepository
    {
        public Task<IReadOnlyList<Server>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Server>>(servers.ToArray());
        public Task<Server?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(servers.SingleOrDefault(server => server.Id == id));
        public Task AddAsync(Server server, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SaveAsync(Server server, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task DeleteAsync(Server server, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeMetrics : IMetricRepository
    {
        private readonly ConcurrentDictionary<Guid, int> _cycles = new();
        public int CyclesStored(Guid serverId) => _cycles.GetValueOrDefault(serverId);

        public Task AddSamplesAsync(IReadOnlyCollection<MetricSample> samples, CancellationToken cancellationToken)
        {
            foreach (var serverId in samples.Select(sample => sample.ServerId).Distinct())
                _cycles.AddOrUpdate(serverId, 1, static (_, count) => count + 1);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<MetricSample>> GetRecentAsync(Guid serverId, DateTimeOffset sinceUtc, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MetricSample>>([]);
        public Task<IReadOnlyList<MetricSample>> GetLatestAsync(Guid serverId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MetricSample>>([]);
        public Task<IReadOnlyList<MetricSample>> GetUnrolledAsync(DateTimeOffset beforeUtc, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MetricSample>>([]);
        public Task SaveRollupsAsync(IReadOnlyCollection<MetricRollup> rollups, DateTimeOffset rawPruneBeforeUtc, DateTimeOffset rollupPruneBeforeUtc, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
}
