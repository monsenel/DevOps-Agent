using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Monitoring;
using DevOpsAgent.Application.Servers;
using DevOpsAgent.Domain.Monitoring;
using DevOpsAgent.Infrastructure.Monitoring;
using DevOpsAgent.Infrastructure.Ssh;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DevOpsAgent.Tests;

/// <summary>
/// T7.R26: every stored sudo password of every server is removed from every server's output, from alerts and notifications
/// and from everything sent to an LLM; passwords shorter than 6 characters are refused; the Servers page hears about
/// background sudo state changes.
/// </summary>
public sealed partial class SudoPasswordTests
{
    [Fact]
    public async Task PasswordOfServerA_IsRedactedFromTheOutputOfServerB()
    {
        await using var lab = await SudoLab.CreateAsync(Password);
        var other = await lab.Inventory.CreateAsync(lab.Input("db-again") with { User = "dva" }, CancellationToken.None);

        var plain = await lab.Runner.RunAsync(other.Id, $"printf '%s' '{Password}'", Plain, CancellationToken.None);

        Assert.DoesNotContain(Password, plain.StandardOutput + plain.StandardError);
        Assert.Contains(SudoPasswordGate.Redacted, plain.StandardOutput);
    }

    [Fact]
    public async Task PasswordOfServerA_TypedAsAUserName_IsNotStoredInServerBsAuthScanState()
    {
        await using var lab = await SudoLab.CreateAsync(Password);
        // The same host registered a second time, without a password, as the measured case.
        var other = await lab.Inventory.CreateAsync(lab.Input("db-again") with { User = "dva" }, CancellationToken.None);
        var otherServer = (await lab.Repository.GetAsync(other.Id, CancellationToken.None))!;
        var stamp = DateTimeOffset.UtcNow.AddSeconds(-20).ToString("yyyy-MM-dd'T'HH:mm:sszzz");
        lab.Session.PlainOutput = $"{stamp} db sshd[4242]: Failed password for invalid user {Password} from 10.88.10.1 port 50000 ssh2\n";
        var settings = new MemorySettings();
        var scanner = new AuthLogScanner(lab.Runner, settings, Options.Create(new SecurityMonitoringOptions()), TimeProvider.System);

        await scanner.ScanAsync(otherServer, CancellationToken.None);

        var state = Assert.Single(settings.Values).Value;
        Assert.DoesNotContain(Password, state);
        Assert.Contains(SudoPasswordGate.Redacted, state);
    }

    [Fact]
    public async Task EditedPassword_IsRedactedFromOtherServersAtOnce_AndARemovedOneIsDroppedFromTheList()
    {
        await using var lab = await SudoLab.CreateAsync(Password);
        var other = await lab.Inventory.CreateAsync(lab.Input("db-again") with { User = "dva" }, CancellationToken.None);
        const string newPassword = "Replaced-Pw-31";
        await lab.Runner.RunAsync(other.Id, "uptime -p", Plain, CancellationToken.None); // the list is now cached

        await lab.EditAsync(lab.Input("db") with { SudoPassword = newPassword });
        var after = await lab.Runner.RunAsync(other.Id, $"printf '%s' '{newPassword}'", Plain, CancellationToken.None);
        Assert.DoesNotContain(newPassword, after.StandardOutput);

        await lab.EditAsync(lab.Input("db") with { RemoveSudoPassword = true });
        Assert.Empty(await lab.Gate.GetAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("Q")]
    [InlineData("Zq9#k")]
    [InlineData("Xw!3 ")]
    public async Task ShortPasswords_AreRefused_WithoutEchoingThem(string password)
    {
        await using var lab = await SudoLab.CreateAsync(sudoPassword: null);

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => lab.EditAsync(lab.Input("db") with { SudoPassword = password }));

        Assert.Contains("at least 6 characters", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(password.Trim(), refused.Message);
        Assert.False((await lab.GetServerAsync()).HasSudoPassword);
        // Exactly the minimum is accepted.
        await lab.EditAsync(lab.Input("db") with { SudoPassword = "abcdef" });
        Assert.True((await lab.GetServerAsync()).HasSudoPassword);
    }

    [Fact]
    public async Task BackgroundSudoStateChanges_AreAnnounced()
    {
        var events = new ServerStateEvents();
        await using var lab = await SudoLab.CreateAsync(WrongPassword, events: events);
        var changed = new List<Guid>();
        events.Changed += changed.Add;

        await lab.Runner.RunAsync(lab.ServerId, "ss -tulpnH", Sudo, CancellationToken.None); // rejected
        Assert.Equal([lab.ServerId], changed);

        await lab.EditAsync(lab.Input("db") with { SudoPassword = Password });
        lab.Session.SudoAnswer = () => throw new Renci.SshNet.Common.SshOperationTimeoutException("timed out");
        await Assert.ThrowsAsync<Renci.SshNet.Common.SshOperationTimeoutException>(() =>
            lab.Runner.RunAsync(lab.ServerId, "ss -tulpnH", Sudo, CancellationToken.None)); // unknown
        Assert.Equal(2, changed.Count);

        lab.Session.SudoAnswer = null;
        await lab.Runner.ProbeSudoAsync(lab.ServerId, Plain, CancellationToken.None); // Retry now lifts it, then confirmed
        Assert.True(changed.Count >= 4);
        Assert.All(changed, id => Assert.Equal(lab.ServerId, id));
    }

    [Fact]
    public async Task AlertTextAndNotifications_NeverHoldAStoredPassword()
    {
        var signals = new FixedSignals(new AlertCondition("ssh-failed-burst:x", "ssh-failed-burst", AlertSeverity.High, Guid.NewGuid(),
            "db.test", "SSH login attack detected", $"usernames: root, {Password}; successful login: no"));
        var notifications = new RecordingNotifications();
        var repository = new MemoryAlerts();
        var service = new AlertService(repository, signals, TimeProvider.System, notifications, NullLogger<AlertService>.Instance,
            new FixedSecrets(Password));

        await service.EvaluateAsync(CancellationToken.None);

        var alert = Assert.Single(repository.Items);
        Assert.DoesNotContain(Password, alert.Details);
        Assert.Contains("[REDACTED]", alert.Details);
        Assert.All(notifications.Sent, dto => Assert.DoesNotContain(Password, dto.Details + dto.Title + dto.Target));
    }

    [Fact]
    public async Task LlmInput_NeverHoldsAStoredPassword_EvenForALocalProviderWithoutRedaction()
    {
        await using var chat = await ChatServiceHarness.CreateAsync(new FixedSecrets(Password), baseUrl: "http://127.0.0.1:11434/v1",
            type: Domain.Llm.ProviderType.Ollama);
        var conversation = await chat.Service.CreateAsync(CancellationToken.None);

        await chat.SendAsync(conversation, $"Why does sudo reject {Password} on db?");

        Assert.NotEmpty(chat.Requests);
        Assert.DoesNotContain(Password, chat.SentText);
        Assert.Contains("[REDACTED]", chat.SentText);
    }

    [Fact]
    public async Task WhenTheListOfPasswordsCannotBeRead_OutputIsStillRedactedWithThePasswordsSeenSoFar()
    {
        await using var lab = await SudoLab.CreateAsync(Password);
        var other = await lab.Inventory.CreateAsync(lab.Input("db-again") with { User = "dva" }, CancellationToken.None);
        await lab.Runner.RunAsync(other.Id, "uptime -p", Plain, CancellationToken.None); // the passwords are known to the process now
        lab.Gate.Invalidate();
        lab.ListSwitch.Fail = true;

        var result = await lab.Runner.RunAsync(other.Id, $"printf '%s' '{Password}'", Plain, CancellationToken.None);

        Assert.DoesNotContain(Password, result.StandardOutput + result.StandardError);
        Assert.Contains(SudoPasswordGate.Redacted, result.StandardOutput);
    }

    [Fact]
    public async Task TheStoredMessage_AndTheConversationTitle_NeverHoldAStoredPassword()
    {
        await using var chat = await ChatServiceHarness.CreateAsync(new FixedSecrets(Password), baseUrl: "http://127.0.0.1:11434/v1",
            type: Domain.Llm.ProviderType.Ollama);
        var conversation = await chat.Service.CreateAsync(CancellationToken.None);

        await chat.SendAsync(conversation, $"{Password} is rejected on db, why?");

        var messages = await chat.Context.ChatMessages.ToListAsync();
        Assert.NotEmpty(messages);
        Assert.All(messages, message => Assert.DoesNotContain(Password, message.Content));
        Assert.Contains("[REDACTED] is rejected on db, why?", messages.Single(message => message.Role == "user").Content);
        Assert.DoesNotContain(Password, (await chat.Context.ChatConversations.SingleAsync()).Title);
    }

    private sealed class MemorySettings : IAppSettingRepository
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(Values.TryGetValue(key, out var value) ? value : null);
        public Task SetAsync(string key, string value, CancellationToken cancellationToken)
        {
            Values[key] = value;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedSignals(params AlertCondition[] conditions) : IAlertSignalSource
    {
        public Task<IReadOnlyList<AlertCondition>> GetActiveConditionsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AlertCondition>>(conditions);
    }

    private sealed class RecordingNotifications : DevOpsAgent.Application.Notifications.IAlertNotificationService
    {
        public List<AlertDto> Sent { get; } = [];
        public Task NotifyOpenedAsync(AlertDto alert, CancellationToken cancellationToken) { Sent.Add(alert); return Task.CompletedTask; }
        public Task NotifyResolvedAsync(AlertDto alert, CancellationToken cancellationToken) { Sent.Add(alert); return Task.CompletedTask; }
    }

    private sealed class MemoryAlerts : IAlertRepository
    {
        public List<Alert> Items { get; } = [];
        public Task<IReadOnlyList<Alert>> ListAsync(AlertState? state, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Alert>>(Items.Where(item => state is null || item.State == state).ToArray());
        public Task<Alert?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Items.FirstOrDefault(item => item.Id == id));
        public Task AddAsync(Alert alert, CancellationToken cancellationToken) { Items.Add(alert); return Task.CompletedTask; }
        public Task SaveAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<Alert>> ListActiveAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Alert>>(Items.Where(item => item.State != AlertState.Resolved).ToArray());
    }
}
