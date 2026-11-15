using System.Net.Sockets;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Monitoring;
using DevOpsAgent.Application.Notifications;
using DevOpsAgent.Domain.Monitoring;
using DevOpsAgent.Domain.Servers;
using Microsoft.Extensions.Logging.Abstractions;
using Renci.SshNet.Common;

namespace DevOpsAgent.Tests;

/// <summary>
/// D-039 amendment: a failed SSH login (and a login whose automatic retries are paused) is its own alert rule,
/// <c>ssh-auth-failed</c>, with its own key per server, never <c>server-unreachable</c>. The notification carries the rule name
/// (D-028: server name, rule, severity and state, nothing else).
/// </summary>
public sealed class SshAuthFailedRuleTests
{
    private const string Canary = "-----BEGIN OPENSSH PRIVATE KEY----- hunter2-passphrase-canary /keys/id_secret";

    private static Exception Rejected() => new SshAuthenticationException(Canary);
    private static Exception Down() => new SocketException((int)SocketError.TimedOut);

    private static async Task<IReadOnlyList<AlertCondition>> EvaluateAsync(HangLab lab) =>
        await SshAuthFailureConsumerTests.AlertSource(lab).GetActiveConditionsAsync(CancellationToken.None);

    // ---- The signal source: the right rule per failure kind ----

    [Fact]
    public async Task EachFailureKindEmitsItsOwnRule()
    {
        var clock = new ManualClock();
        using var lab = new HangLab(clock);
        var auth = lab.Add("auth", "10.0.0.1", script => script.ConnectFails = Rejected);
        var missingKey = lab.Add("nokey", "10.0.0.2", script => script.ConnectFails = () => new FileNotFoundException(Canary, "/keys/id_secret"));
        var down = lab.Add("down", "10.0.0.3", script => script.ConnectFails = Down);
        var mismatch = lab.Add("mismatch", "10.0.0.4", script => script.ConnectFails = () => new SshHostKeyMismatchException("10.0.0.4", 22, "abc"));
        var stuck = lab.Add("stuck", "10.0.0.5");
        var healthy = lab.Add("ok", "10.0.0.6");
        // Four command timeouts in a row: the host is in command-timeout backoff.
        for (var count = 0; count < IHostReachability.CommandTimeoutThreshold; count++)
            lab.Reachability.RecordCommandTimeout(stuck.Id, new TimeoutException());

        var conditions = await EvaluateAsync(lab);

        string RuleOf(Server server) => Assert.Single(conditions, condition => condition.ServerId == server.Id && condition.Rule.StartsWith("s", StringComparison.Ordinal)).Rule;
        Assert.Equal("ssh-auth-failed", RuleOf(auth));
        Assert.Equal("ssh-auth-failed", RuleOf(missingKey));
        Assert.Equal("server-unreachable", RuleOf(down));
        Assert.Equal("ssh-host-key-mismatch", RuleOf(mismatch));
        Assert.Equal("server-unreachable", RuleOf(stuck));
        Assert.DoesNotContain(conditions, condition => condition.ServerId == healthy.Id && condition.Rule is "ssh-auth-failed" or "server-unreachable");

        var authAlert = conditions.Single(condition => condition.ServerId == auth.Id && condition.Rule == "ssh-auth-failed");
        Assert.Equal($"ssh-auth-failed:{auth.Id:N}", authAlert.Key);
        Assert.Equal(AlertSeverity.High, authAlert.Severity);
        Assert.Equal("auth: SSH authentication failed", authAlert.Title);
        // One condition per server for the connectivity family: never both rules for one host.
        Assert.All(new[] { auth, missingKey, down, stuck }, server =>
            Assert.Single(conditions, condition => condition.ServerId == server.Id && condition.Rule is "ssh-auth-failed" or "server-unreachable"));
    }

    [Fact]
    public async Task APausedLoginKeepsTheSshAuthFailedRuleAndSaysSo()
    {
        var clock = new ManualClock();
        using var lab = new HangLab(clock);
        var host = lab.Add("srv", "10.0.0.1", script => script.ConnectFails = Rejected);
        var source = SshAuthFailureConsumerTests.AlertSource(lab);

        AlertCondition? last = null;
        for (var cycle = 0; cycle < 8; cycle++)
        {
            var conditions = await source.GetActiveConditionsAsync(CancellationToken.None);
            last = Assert.Single(conditions, condition => condition.ServerId == host.Id);
            Assert.Equal("ssh-auth-failed", last.Rule);
            Assert.Equal($"ssh-auth-failed:{host.Id:N}", last.Key);
            clock.Advance(TimeSpan.FromSeconds(30));
        }

        Assert.Equal(IHostReachability.AuthFailedAttemptLimit, lab.Transports.Attempts(host));
        Assert.Contains("automatic retries paused", last!.Details, StringComparison.Ordinal);
        Assert.Equal("srv: SSH authentication failed", last.Title);
    }

    // ---- The alert lifecycle: transitions in both directions ----

    [Fact]
    public async Task UnreachableThenAuthFailedResolvesOneAlertAndOpensTheOtherAndBackNeverBothOpen()
    {
        var clock = new ManualClock();
        using var lab = new HangLab(clock);
        var host = lab.Add("srv", "10.0.0.1", script => script.ConnectFails = Down);
        var alerts = new AlertFixture(lab, clock);

        async Task<string[]> OpenRulesAsync()
        {
            await alerts.Service.EvaluateAsync(CancellationToken.None);
            var open = (await alerts.Service.ListAsync(AlertState.Open, CancellationToken.None)).Where(alert => alert.ServerId == host.Id).ToArray();
            Assert.True(open.Length <= 1, "both connectivity alerts are open for one host");
            return open.Select(alert => alert.Rule).ToArray();
        }

        Assert.Equal(["server-unreachable"], await OpenRulesAsync());

        // The host comes up but rejects the key (an edit evicts the old failure, as in the app).
        lab.Pool.Evict(host.Id);
        lab.Transports.Script(host.Host).ConnectFails = Rejected;
        Assert.Equal(["ssh-auth-failed"], await OpenRulesAsync());
        Assert.Contains(alerts.Notifications.Resolved, alert => alert.Rule == "server-unreachable" && alert.ServerId == host.Id);
        Assert.Contains(alerts.Notifications.Opened, alert => alert.Rule == "ssh-auth-failed" && alert.ServerId == host.Id);

        // ... and back: the key is fine but the host goes dark.
        lab.Pool.Evict(host.Id);
        lab.Transports.Script(host.Host).ConnectFails = Down;
        Assert.Equal(["server-unreachable"], await OpenRulesAsync());
        Assert.Equal(1, alerts.Notifications.Resolved.Count(alert => alert.Rule == "ssh-auth-failed"));

        // Recovery resolves whatever is open.
        lab.Pool.Evict(host.Id);
        lab.Transports.Script(host.Host).ConnectFails = null;
        Assert.Empty(await OpenRulesAsync());
    }

    [Fact]
    public async Task ADependentBehindAnAuthFailedJumpHostHasItsOwnSshAuthFailedAlertAndFollowsTheJumpHostBackAndForth()
    {
        var clock = new ManualClock();
        using var lab = new HangLab(clock);
        var edge = lab.Add("edge", "10.0.0.1", script => script.ConnectFails = Rejected);
        var app = lab.Add("app", "10.0.0.2", jump: edge);
        var alerts = new AlertFixture(lab, clock);

        async Task<(string Edge, string App)> RulesAsync()
        {
            await alerts.Service.EvaluateAsync(CancellationToken.None);
            var open = await alerts.Service.ListAsync(AlertState.Open, CancellationToken.None);
            return (Assert.Single(open, alert => alert.ServerId == edge.Id).Rule, Assert.Single(open, alert => alert.ServerId == app.Id).Rule);
        }

        Assert.Equal(("ssh-auth-failed", "ssh-auth-failed"), await RulesAsync());
        var appAlert = Assert.Single(await alerts.Service.ListAsync(AlertState.Open, CancellationToken.None), alert => alert.ServerId == app.Id);
        Assert.Equal("app: SSH authentication failed on jump host edge", appAlert.Title);

        // The jump host stops answering altogether: both follow to the outage rule.
        lab.Pool.Evict(edge.Id);
        lab.Pool.Evict(app.Id);
        lab.Transports.Script(edge.Host).ConnectFails = Down;
        Assert.Equal(("server-unreachable", "server-unreachable"), await RulesAsync());

        // ... and back to a rejected key.
        lab.Pool.Evict(edge.Id);
        lab.Pool.Evict(app.Id);
        lab.Transports.Script(edge.Host).ConnectFails = Rejected;
        Assert.Equal(("ssh-auth-failed", "ssh-auth-failed"), await RulesAsync());
    }

    // ---- Notifications (D-028) ----

    [Fact]
    public async Task TheNotificationNamesTheRuleAndCarriesNoKeyPathUserOrAddress()
    {
        var clock = new ManualClock();
        using var lab = new HangLab(clock);
        var host = lab.Add("srv-auth", "203.0.113.7", script => script.ConnectFails = Rejected);
        var alerts = new AlertFixture(lab, clock);
        await alerts.Notifications.Service.SaveAsync(new(false, string.Empty, string.Empty, true, "https://ntfy.example", "ops",
            string.Empty, AlertSeverity.Low), CancellationToken.None);

        await alerts.Service.EvaluateAsync(CancellationToken.None);
        lab.Transports.Script(host.Host).ConnectFails = null;
        lab.Pool.Evict(host.Id);
        await alerts.Service.EvaluateAsync(CancellationToken.None);

        var sent = alerts.Notifications.Notifier.Sent.Select(item => item.Message).ToArray();
        Assert.Equal(["[High] srv-auth · ssh-auth-failed · opened", "[High] srv-auth · ssh-auth-failed · resolved"], sent);
        Assert.All(sent, message =>
        {
            foreach (var leaked in new[] { "203.0.113.7", "dva", "/keys", "id_secret", "PRIVATE KEY", "hunter2", "rejected", "authentication", "server-unreachable" })
                Assert.DoesNotContain(leaked, message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void TheFormattedMessageUsesTheRuleNameOnly() =>
        Assert.Equal("[High] srv · ssh-auth-failed · opened",
            NotificationService.FormatAlertMessage("srv", "ssh-auth-failed", AlertSeverity.High, resolved: false));

    // ---- Fixture ----

    private sealed class AlertFixture
    {
        public AlertFixture(HangLab lab, TimeProvider clock)
        {
            Notifications = new NotificationFixture(lab);
            Service = new AlertService(new Repository(), SshAuthFailureConsumerTests.AlertSource(lab), clock, Notifications.Recorder,
                NullLogger<AlertService>.Instance);
        }

        public NotificationFixture Notifications { get; }
        public AlertService Service { get; }
    }

    /// <summary>Records the opened/resolved alerts that reach the notification service and what it would have sent.</summary>
    private sealed class NotificationFixture
    {
        private readonly FakeSettings _settings = new();

        public NotificationFixture(HangLab lab)
        {
            Notifier = new FakeNotifier();
            Service = new NotificationService(_settings, new FakeProtector(), Notifier, lab.Servers);
            Recorder = new RecordingNotifications(Service);
        }

        public FakeNotifier Notifier { get; }
        public NotificationService Service { get; }
        public RecordingNotifications Recorder { get; }
        public List<AlertDto> Opened => Recorder.Opened;
        public List<AlertDto> Resolved => Recorder.Resolved;
    }

    private sealed class RecordingNotifications(IAlertNotificationService inner) : IAlertNotificationService
    {
        public List<AlertDto> Opened { get; } = [];
        public List<AlertDto> Resolved { get; } = [];

        public Task NotifyOpenedAsync(AlertDto alert, CancellationToken cancellationToken)
        {
            Opened.Add(alert);
            return inner.NotifyOpenedAsync(alert, cancellationToken);
        }

        public Task NotifyResolvedAsync(AlertDto alert, CancellationToken cancellationToken)
        {
            Resolved.Add(alert);
            return inner.NotifyResolvedAsync(alert, cancellationToken);
        }
    }

    private sealed class Repository : IAlertRepository
    {
        private readonly List<Alert> _alerts = [];
        public Task<IReadOnlyList<Alert>> ListAsync(AlertState? state, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Alert>>(_alerts.Where(item => state is null || item.State == state).OrderByDescending(item => item.LastSeenAtUtc).ToArray());
        public Task<Alert?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(_alerts.FirstOrDefault(item => item.Id == id));
        public Task<IReadOnlyList<Alert>> ListActiveAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Alert>>(_alerts.Where(item => item.State != AlertState.Resolved).ToArray());
        public Task AddAsync(Alert alert, CancellationToken cancellationToken) { _alerts.Add(alert); return Task.CompletedTask; }
        public Task SaveAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeSettings : IAppSettingRepository
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);
        public Task SetAsync(string key, string value, CancellationToken cancellationToken) { _values[key] = value; return Task.CompletedTask; }
    }

    private sealed class FakeProtector : ISecretProtector
    {
        public string Protect(string plaintext) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plaintext));
        public string Unprotect(string protectedValue) => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue));
    }

    private sealed class FakeNotifier : INotifier
    {
        public List<(NotificationEndpoint Endpoint, string Message)> Sent { get; } = [];
        public Task SendAsync(NotificationEndpoint endpoint, string message, CancellationToken cancellationToken)
        {
            Sent.Add((endpoint, message));
            return Task.CompletedTask;
        }
    }
}
