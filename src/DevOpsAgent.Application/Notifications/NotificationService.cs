using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Monitoring;
using DevOpsAgent.Domain.Monitoring;

namespace DevOpsAgent.Application.Notifications;

public sealed class NotificationService(IAppSettingRepository settings, ISecretProtector protector, INotifier notifier)
    : IAlertNotificationService
{
    private const string SettingKey = "notifications.v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<NotificationSettings> GetAsync(CancellationToken cancellationToken)
    {
        var stored = await ReadAsync(cancellationToken);
        return ToPublicSettings(stored);
    }

    public async Task SaveAsync(NotificationSettingsInput input, CancellationToken cancellationToken)
    {
        Validate(input);
        var existing = await ReadAsync(cancellationToken);
        if (input.TelegramEnabled && string.IsNullOrWhiteSpace(input.TelegramBotToken) && string.IsNullOrWhiteSpace(existing.TelegramBotToken))
            throw new ArgumentException("Enter a Telegram bot token.");
        var next = new StoredSettings(input.TelegramEnabled,
            string.IsNullOrWhiteSpace(input.TelegramBotToken) ? existing.TelegramBotToken : protector.Protect(input.TelegramBotToken.Trim()),
            input.TelegramChatId.Trim(), input.NtfyEnabled, input.NtfyBaseUrl.Trim().TrimEnd('/'), input.NtfyTopic.Trim(),
            string.IsNullOrWhiteSpace(input.NtfyToken) ? existing.NtfyToken : protector.Protect(input.NtfyToken.Trim()),
            input.MinimumSeverity);
        await settings.SetAsync(SettingKey, JsonSerializer.Serialize(next, JsonOptions), cancellationToken);
    }

    public async Task TestAsync(CancellationToken cancellationToken)
    {
        var stored = await ReadAsync(cancellationToken);
        var destinations = GetDestinations(stored);
        if (destinations.Count == 0) throw new InvalidOperationException("Enable and configure at least one notification destination.");
        await SendToAllAsync(destinations, "DevOps Agent test notification. Your notification settings are working.", cancellationToken);
    }

    public async Task NotifyOpenedAsync(AlertDto alert, CancellationToken cancellationToken) =>
        await NotifyAlertAsync(alert, resolved: false, cancellationToken);

    public async Task NotifyResolvedAsync(AlertDto alert, CancellationToken cancellationToken) =>
        await NotifyAlertAsync(alert, resolved: true, cancellationToken);

    private async Task NotifyAlertAsync(AlertDto alert, bool resolved, CancellationToken cancellationToken)
    {
        var stored = await ReadAsync(cancellationToken);
        if (alert.Severity < stored.MinimumSeverity) return;
        var destinations = GetDestinations(stored);
        if (destinations.Count == 0) return;
        var state = resolved ? "RESOLVED" : "OPEN";
        var message = $"[{state}] {alert.Severity} alert: {alert.Title}\nTarget: {alert.Target}\nRule: {alert.Rule}\n{alert.Details}";
        foreach (var endpoint in destinations)
        {
            try { await notifier.SendAsync(endpoint, message, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { }
        }
    }

    private async Task SendToAllAsync(IReadOnlyList<NotificationEndpoint> destinations, string message,
        CancellationToken cancellationToken)
    {
        var failed = false;
        foreach (var endpoint in destinations)
        {
            try { await notifier.SendAsync(endpoint, message, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { failed = true; }
        }
        if (failed) throw new InvalidOperationException("One or more test notifications could not be delivered.");
    }

    private List<NotificationEndpoint> GetDestinations(StoredSettings stored)
    {
        var result = new List<NotificationEndpoint>();
        if (stored.TelegramEnabled && !string.IsNullOrWhiteSpace(stored.TelegramBotToken) && !string.IsNullOrWhiteSpace(stored.TelegramChatId))
            result.Add(new("Telegram", protector.Unprotect(stored.TelegramBotToken), stored.TelegramChatId, null));
        if (stored.NtfyEnabled && !string.IsNullOrWhiteSpace(stored.NtfyBaseUrl) && !string.IsNullOrWhiteSpace(stored.NtfyTopic))
            result.Add(new("ntfy", string.IsNullOrWhiteSpace(stored.NtfyToken) ? string.Empty : protector.Unprotect(stored.NtfyToken),
                stored.NtfyBaseUrl, stored.NtfyTopic));
        return result;
    }

    private async Task<StoredSettings> ReadAsync(CancellationToken cancellationToken)
    {
        var json = await settings.GetAsync(SettingKey, cancellationToken);
        return json is null ? StoredSettings.Default : JsonSerializer.Deserialize<StoredSettings>(json, JsonOptions) ?? StoredSettings.Default;
    }

    private static NotificationSettings ToPublicSettings(StoredSettings stored) => new(stored.TelegramEnabled,
        stored.TelegramChatId, !string.IsNullOrWhiteSpace(stored.TelegramBotToken), stored.NtfyEnabled,
        stored.NtfyBaseUrl, stored.NtfyTopic, !string.IsNullOrWhiteSpace(stored.NtfyToken), stored.MinimumSeverity);

    private static void Validate(NotificationSettingsInput input)
    {
        if (!Enum.IsDefined(input.MinimumSeverity)) throw new ArgumentException("Choose a valid minimum severity.");
        if (input.TelegramEnabled && string.IsNullOrWhiteSpace(input.TelegramChatId))
            throw new ArgumentException("Enter a Telegram chat ID.");
        if (input.NtfyEnabled)
        {
            if (string.IsNullOrWhiteSpace(input.NtfyTopic)) throw new ArgumentException("Enter an ntfy topic.");
            if (!Uri.TryCreate(input.NtfyBaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                throw new ArgumentException("Enter a valid ntfy HTTP or HTTPS base URL without credentials or query parameters.");
        }
    }

    private sealed record StoredSettings(bool TelegramEnabled, string TelegramBotToken, string TelegramChatId,
        bool NtfyEnabled, string NtfyBaseUrl, string NtfyTopic, string NtfyToken, AlertSeverity MinimumSeverity)
    {
        public static StoredSettings Default { get; } = new(false, string.Empty, string.Empty, false, "https://ntfy.sh", string.Empty,
            string.Empty, AlertSeverity.Medium);
    }
}
