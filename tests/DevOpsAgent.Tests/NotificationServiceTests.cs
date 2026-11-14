using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Monitoring;
using DevOpsAgent.Application.Notifications;
using DevOpsAgent.Domain.Monitoring;

namespace DevOpsAgent.Tests;

public sealed class NotificationServiceTests
{
    [Fact]
    public async Task SaveProtectsTokensAndBlankReplacementPreservesThem()
    {
        var fixture = new Fixture();
        await fixture.Service.SaveAsync(new(true, "telegram-secret", "1234", true, "https://ntfy.example", "private-topic",
            "ntfy-secret", AlertSeverity.Medium), CancellationToken.None);

        var stored = Assert.IsType<string>(fixture.Settings.Values["notifications.v1"]);
        Assert.DoesNotContain("telegram-secret", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("ntfy-secret", stored, StringComparison.Ordinal);
        var publicSettings = await fixture.Service.GetAsync(CancellationToken.None);
        Assert.True(publicSettings.HasTelegramBotToken);
        Assert.True(publicSettings.HasNtfyToken);

        await fixture.Service.SaveAsync(new(true, string.Empty, "1234", true, "https://ntfy.example", "private-topic",
            string.Empty, AlertSeverity.High), CancellationToken.None);
        await fixture.Service.TestAsync(CancellationToken.None);
        Assert.Equal(2, fixture.Notifier.Sent.Count);
        Assert.Contains(fixture.Notifier.Sent, message => message.Endpoint.Secret == "telegram-secret");
        Assert.Contains(fixture.Notifier.Sent, message => message.Endpoint.Secret == "ntfy-secret");
    }

    [Fact]
    public async Task AlertNotificationHonorsMinimumSeverity()
    {
        var fixture = new Fixture();
        await fixture.Service.SaveAsync(new(false, string.Empty, string.Empty, true, "https://ntfy.example", "ops",
            string.Empty, AlertSeverity.High), CancellationToken.None);
        var low = new AlertDto(Guid.NewGuid(), "disk", AlertSeverity.Medium, AlertState.Open, null, "server", "Disk high",
            "details", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, 1);
        var high = low with { Id = Guid.NewGuid(), Severity = AlertSeverity.High };

        await fixture.Service.NotifyOpenedAsync(low, CancellationToken.None);
        await fixture.Service.NotifyOpenedAsync(high, CancellationToken.None);

        var sent = Assert.Single(fixture.Notifier.Sent);
        Assert.Contains("[OPEN]", sent.Message, StringComparison.Ordinal);
    }

    private sealed class Fixture
    {
        public FakeSettings Settings { get; } = new();
        public FakeProtector Protector { get; } = new();
        public FakeNotifier Notifier { get; } = new();
        public NotificationService Service { get; }
        public Fixture() => Service = new NotificationService(Settings, Protector, Notifier);
    }

    private sealed class FakeSettings : IAppSettingRepository
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(Values.TryGetValue(key, out var value) ? value : null);
        public Task SetAsync(string key, string value, CancellationToken cancellationToken)
        { Values[key] = value; return Task.CompletedTask; }
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
        { Sent.Add((endpoint, message)); return Task.CompletedTask; }
    }
}
