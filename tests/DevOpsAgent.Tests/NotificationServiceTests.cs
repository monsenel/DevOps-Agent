using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Monitoring;
using DevOpsAgent.Application.Notifications;
using DevOpsAgent.Domain.Monitoring;
using DevOpsAgent.Domain.Servers;

namespace DevOpsAgent.Tests;

public sealed class NotificationServiceTests
{
    [Fact]
    public async Task SaveProtectsTokensAndTopicAndBlankReplacementPreservesThem()
    {
        var fixture = new Fixture();
        await fixture.Service.SaveAsync(new(true, "telegram-secret", "1234", true, "https://ntfy.example", "private-topic",
            "ntfy-secret", AlertSeverity.Medium), CancellationToken.None);

        var stored = Assert.IsType<string>(fixture.Settings.Values["notifications.v1"]);
        Assert.DoesNotContain("telegram-secret", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("ntfy-secret", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("private-topic", stored, StringComparison.Ordinal);
        var publicSettings = await fixture.Service.GetAsync(CancellationToken.None);
        Assert.True(publicSettings.HasTelegramBotToken);
        Assert.True(publicSettings.HasNtfyToken);
        Assert.True(publicSettings.HasNtfyTopic);
        Assert.DoesNotContain("private-topic", publicSettings.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret", publicSettings.ToString(), StringComparison.Ordinal);

        await fixture.Service.SaveAsync(new(true, string.Empty, "1234", true, "https://ntfy.example", string.Empty,
            string.Empty, AlertSeverity.High), CancellationToken.None);
        await fixture.Service.TestAsync(CancellationToken.None);
        Assert.Equal(2, fixture.Notifier.Sent.Count);
        Assert.Contains(fixture.Notifier.Sent, message => message.Endpoint.Secret == "telegram-secret");
        Assert.Contains(fixture.Notifier.Sent, message => message.Endpoint.Secret == "ntfy-secret"
            && message.Endpoint.SecondaryDestination == "private-topic");
    }

    [Fact]
    public async Task EnablingNtfyRequiresATopicWhenNoneIsStored()
    {
        var fixture = new Fixture();

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.SaveAsync(new(false, string.Empty, string.Empty, true,
            "https://ntfy.example", string.Empty, string.Empty, AlertSeverity.High), CancellationToken.None));
    }

    [Fact]
    public async Task LegacyPlainTextTopicIsProtectedOnFirstReadWithoutLosingIt()
    {
        var fixture = new Fixture();
        fixture.Settings.Values["notifications.v1"] =
            "{\"telegramEnabled\":false,\"telegramBotToken\":\"\",\"telegramChatId\":\"\",\"ntfyEnabled\":true,"
            + "\"ntfyBaseUrl\":\"https://ntfy.example\",\"ntfyTopic\":\"legacy-topic\",\"ntfyToken\":\"\",\"minimumSeverity\":1}";

        var publicSettings = await fixture.Service.GetAsync(CancellationToken.None);
        await fixture.Service.TestAsync(CancellationToken.None);

        Assert.True(publicSettings.HasNtfyTopic);
        Assert.DoesNotContain("legacy-topic", fixture.Settings.Values["notifications.v1"], StringComparison.Ordinal);
        Assert.Equal("legacy-topic", Assert.Single(fixture.Notifier.Sent).Endpoint.SecondaryDestination);
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
        Assert.EndsWith("· opened", sent.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AlertNotificationsCarryOnlyServerNameRuleSeverityAndState()
    {
        var fixture = new Fixture();
        var server = new Server("srv-edge", "203.0.113.7", 22, "dva", "key", "staging");
        fixture.Servers.Add(server);
        await fixture.Service.SaveAsync(new(true, "bot", "1234", true, "https://ntfy.example", "ops",
            string.Empty, AlertSeverity.Low), CancellationToken.None);
        var alert = new AlertDto(Guid.NewGuid(), "ssh-unusual-sudo", AlertSeverity.High, AlertState.Open, server.Id, "203.0.113.7",
            "Sudo by mallory from 198.51.100.9", "user=mallory ip=198.51.100.9 COMMAND=/usr/bin/curl -u admin:hunter2",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, 1);

        await fixture.Service.NotifyOpenedAsync(alert, CancellationToken.None);
        await fixture.Service.NotifyResolvedAsync(alert with { State = AlertState.Resolved }, CancellationToken.None);

        Assert.Equal(4, fixture.Notifier.Sent.Count);
        Assert.All(fixture.Notifier.Sent, sent =>
        {
            foreach (var leaked in new[] { "203.0.113.7", "198.51.100.9", "mallory", "curl", "hunter2", "admin", "Sudo by" })
                Assert.DoesNotContain(leaked, sent.Message, StringComparison.Ordinal);
        });
        Assert.Contains(fixture.Notifier.Sent, sent => sent.Message == "[High] srv-edge · ssh-unusual-sudo · opened");
        Assert.Contains(fixture.Notifier.Sent, sent => sent.Message == "[High] srv-edge · ssh-unusual-sudo · resolved");
    }

    private sealed class Fixture
    {
        public FakeSettings Settings { get; } = new();
        public FakeProtector Protector { get; } = new();
        public FakeNotifier Notifier { get; } = new();
        public List<Server> Servers { get; } = [];
        public NotificationService Service { get; }
        public Fixture() => Service = new NotificationService(Settings, Protector, Notifier, new FakeServers(Servers));
    }

    private sealed class FakeServers(List<Server> servers) : IServerRepository
    {
        public Task<IReadOnlyList<Server>> ListAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Server>>(servers);
        public Task<Server?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(servers.SingleOrDefault(server => server.Id == id));
        public Task AddAsync(Server server, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveAsync(Server server, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAsync(Server server, CancellationToken cancellationToken) => throw new NotSupportedException();
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
