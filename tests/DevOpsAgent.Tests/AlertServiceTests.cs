using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Monitoring;
using DevOpsAgent.Application.Notifications;
using DevOpsAgent.Domain.Monitoring;

namespace DevOpsAgent.Tests;

public sealed class AlertServiceTests
{
    [Fact]
    public async Task EvaluationDeduplicatesAndResolvesWhenConditionClears()
    {
        var fixture = new Fixture();
        fixture.Signals.Conditions = [new AlertCondition("disk:srv1", "threshold-disk", AlertSeverity.High, Guid.NewGuid(),
            "srv1", "Disk usage is high", "91% used")];

        await fixture.Service.EvaluateAsync(CancellationToken.None);
        await fixture.Service.EvaluateAsync(CancellationToken.None);
        Assert.Single(fixture.Notifications.Opened);
        var open = Assert.Single(await fixture.Service.ListAsync(AlertState.Open, CancellationToken.None));
        Assert.Equal(2, open.Count);
        Assert.Equal(1, await fixture.Service.GetOpenCountAsync(CancellationToken.None));

        Assert.True(await fixture.Service.AcknowledgeAsync(open.Id, CancellationToken.None));
        Assert.Empty(await fixture.Service.ListAsync(AlertState.Open, CancellationToken.None));
        fixture.Signals.Conditions = [];
        await fixture.Service.EvaluateAsync(CancellationToken.None);
        var resolved = Assert.Single(await fixture.Service.ListAsync(AlertState.Resolved, CancellationToken.None));
        Assert.Equal(AlertState.Resolved, resolved.State);
        Assert.Single(fixture.Notifications.Resolved);
    }

    [Fact]
    public async Task ReappearingConditionReopensTheSameDeduplicatedAlert()
    {
        var fixture = new Fixture();
        fixture.Signals.Conditions = [new AlertCondition("health:check1", "health-check-failing", AlertSeverity.High, null,
            "api", "API failing", "timeout")];
        await fixture.Service.EvaluateAsync(CancellationToken.None);
        var first = Assert.Single(await fixture.Service.ListAsync(AlertState.Open, CancellationToken.None));
        Assert.True(await fixture.Service.ResolveAsync(first.Id, CancellationToken.None));
        await fixture.Service.EvaluateAsync(CancellationToken.None);

        var reopened = Assert.Single(await fixture.Service.ListAsync(AlertState.Open, CancellationToken.None));
        Assert.Equal(first.Id, reopened.Id);
        Assert.Equal(2, reopened.Count);
    }

    [Fact]
    public async Task DuplicateConditionKeysDoNotBreakEvaluationOrOpenTwoAlerts()
    {
        var fixture = new Fixture();
        fixture.Signals.Conditions =
        [
            new AlertCondition("dup:srv1", "threshold-disk", AlertSeverity.High, null, "srv1", "Disk usage is high", "91% used"),
            new AlertCondition("dup:srv1", "threshold-disk", AlertSeverity.High, null, "srv1", "Disk usage is high", "92% used")
        ];

        await fixture.Service.EvaluateAsync(CancellationToken.None);
        await fixture.Service.EvaluateAsync(CancellationToken.None);

        var open = Assert.Single(await fixture.Service.ListAsync(AlertState.Open, CancellationToken.None));
        Assert.Equal("91% used", open.Details);
        Assert.Single(fixture.Notifications.Opened);
    }

    private sealed class Fixture
    {
        public FakeAlertRepository Repository { get; } = new();
        public FakeSignals Signals { get; } = new();
        public FakeNotifications Notifications { get; } = new();
        public AlertService Service { get; }

        public Fixture() => Service = new AlertService(Repository, Signals, TimeProvider.System, Notifications);
    }

    private sealed class FakeNotifications : IAlertNotificationService
    {
        public List<AlertDto> Opened { get; } = [];
        public List<AlertDto> Resolved { get; } = [];
        public Task NotifyOpenedAsync(AlertDto alert, CancellationToken cancellationToken) { Opened.Add(alert); return Task.CompletedTask; }
        public Task NotifyResolvedAsync(AlertDto alert, CancellationToken cancellationToken) { Resolved.Add(alert); return Task.CompletedTask; }
    }

    private sealed class FakeSignals : IAlertSignalSource
    {
        public IReadOnlyList<AlertCondition> Conditions { get; set; } = [];
        public Task<IReadOnlyList<AlertCondition>> GetActiveConditionsAsync(CancellationToken cancellationToken) => Task.FromResult(Conditions);
    }

    private sealed class FakeAlertRepository : IAlertRepository
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
}
