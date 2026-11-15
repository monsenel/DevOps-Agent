using System.Collections.Concurrent;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Monitoring;
using DevOpsAgent.Application.Servers;
using DevOpsAgent.Domain.Monitoring;
using DevOpsAgent.Domain.Servers;
using DevOpsAgent.Infrastructure.Ssh;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevOpsAgent.Tests;

/// <summary>
/// T7.R17 test bench: the real pool, runner and reachability tracker over a scripted transport, so hosts can be made to
/// connect slowly, never connect, or accept commands that never finish (with or without honouring cancellation).
/// </summary>
internal sealed class HangLab : IDisposable
{
    private readonly List<Server> _servers = [];

    public HangLab(TimeProvider? time = null)
    {
        Time = time ?? new ManualClock();
        Servers = new HangServers(_servers);
        Reachability = new HostReachabilityTracker(Time);
        var services = new ServiceCollection().AddSingleton<IServerRepository>(Servers).BuildServiceProvider();
        Pool = new SshConnectionPool(services.GetRequiredService<IServiceScopeFactory>(), Transports, Reachability,
            NullLogger<SshConnectionPool>.Instance, Lifetime);
        Runner = new SshRunner(Pool);
    }

    public TimeProvider Time { get; }
    public HangServers Servers { get; }
    public ScriptedTransports Transports { get; } = new();
    public StoppingLifetime Lifetime { get; } = new();
    public HostReachabilityTracker Reachability { get; }
    public SshConnectionPool Pool { get; }
    public SshRunner Runner { get; }
    public IReadOnlyList<Server> Inventory => _servers;

    public Server Add(string name, string host, Action<HostScript>? script = null, Server? jump = null)
    {
        var server = new Server(name, host, 22, "dva", "key", "staging");
        if (jump is not null) server.Update(name, host, 22, "dva", "key", jump.Id, "staging", "[]", null);
        _servers.Add(server);
        script?.Invoke(Transports.Script(host));
        return server;
    }

    public Task<RemoteCommandResult> Run(Server server, TimeSpan timeout, CancellationToken cancellationToken = default, bool bypass = false) =>
        Runner.RunAsync(server.Id, "true", new SshRunOptions(timeout, BypassBackoff: bypass), cancellationToken);

    public void Dispose() => Pool.Dispose();
}

internal sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan elapsed) => _now += elapsed;
}

/// <summary>A stand-in for the host's <see cref="IHostApplicationLifetime"/>: <see cref="StopApplication"/> is SIGTERM.</summary>
internal sealed class StoppingLifetime : IHostApplicationLifetime
{
    private readonly CancellationTokenSource _stopping = new();
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => _stopping.Token;
    public CancellationToken ApplicationStopped => CancellationToken.None;
    public void StopApplication() => _stopping.Cancel();
}

internal enum CommandMode
{
    Ok,
    /// <summary>Never finishes, but ends when its token is cancelled (a well-behaved transport).</summary>
    HangHonouringToken,
    /// <summary>Never finishes and never looks at its token (SSH.NET on a dead peer).</summary>
    HangIgnoringToken
}

internal sealed class HostScript
{
    public TimeSpan ConnectDelay { get; set; }
    /// <summary>A connect that outlasts <see cref="ConnectDelay"/>'s token only when it honours cancellation.</summary>
    public bool ConnectHonoursToken { get; set; } = true;
    public bool ConnectNeverEnds { get; set; }
    public CommandMode Commands { get; set; } = CommandMode.Ok;
}

internal sealed class ScriptedTransports : ISshTransportFactory
{
    private readonly ConcurrentDictionary<string, HostScript> _scripts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _attempts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _commands = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<ScriptedTransport> _created = new();

    public HostScript Script(string host) => _scripts.GetOrAdd(host, static _ => new HostScript());
    public int Attempts(Server server) => _attempts.GetValueOrDefault(server.Host);
    public int Commands(Server server) => _commands.GetValueOrDefault(server.Host);
    public IReadOnlyList<ScriptedTransport> Created => _created.ToArray();
    public IReadOnlyList<ScriptedTransport> CreatedFor(Server server) => _created.Where(item => item.Host == server.Host).ToArray();

    internal void CountCommand(string host) => _commands.AddOrUpdate(host, 1, static (_, count) => count + 1);

    public async Task<ISshTransport> ConnectAsync(Server server, ISshTransport? jump, CancellationToken cancellationToken)
    {
        _attempts.AddOrUpdate(server.Host, 1, static (_, count) => count + 1);
        var script = Script(server.Host);
        var token = script.ConnectHonoursToken ? cancellationToken : CancellationToken.None;
        if (script.ConnectNeverEnds) await Task.Delay(Timeout.Infinite, token);
        else if (script.ConnectDelay > TimeSpan.Zero) await Task.Delay(script.ConnectDelay, token);

        var transport = new ScriptedTransport(this, server.Host, script);
        _created.Enqueue(transport);
        return transport;
    }
}

internal sealed class ScriptedTransport(ScriptedTransports owner, string host, HostScript script) : ISshTransport
{
    private static readonly string Sample = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "metrics-sample-1.txt"));
    private readonly TaskCompletionSource _never = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    public string Host { get; } = host;
    public bool Disposed => Volatile.Read(ref _disposed) != 0;
    public bool IsConnected => !Disposed;

    public async Task<RemoteCommandResult> ExecuteAsync(string command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        owner.CountCommand(Host);
        switch (script.Commands)
        {
            case CommandMode.HangHonouringToken:
                await Task.Delay(Timeout.Infinite, cancellationToken);
                break;
            case CommandMode.HangIgnoringToken:
                await _never.Task;
                break;
        }

        return new RemoteCommandResult(0, command == MetricsParser.CollectionCommand ? Sample : "ok\n",
            string.Empty, TimeSpan.FromMilliseconds(1));
    }

    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
}

internal sealed class HangServers(List<Server> servers) : IServerRepository
{
    public Task<IReadOnlyList<Server>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Server>>(servers.ToArray());
    public Task<Server?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(servers.SingleOrDefault(server => server.Id == id));
    public Task AddAsync(Server server, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task SaveAsync(Server server, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task DeleteAsync(Server server, CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class HangMetrics : IMetricRepository
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

internal sealed class HangSettings : IAppSettingRepository
{
    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);
    public Task<string?> GetAsync(string key, CancellationToken cancellationToken) => Task.FromResult(_values.GetValueOrDefault(key));
    public Task SetAsync(string key, string value, CancellationToken cancellationToken) { _values[key] = value; return Task.CompletedTask; }
}
