using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Monitoring;
using DevOpsAgent.Domain.Monitoring;

namespace DevOpsAgent.Application.Notifications;

public sealed class NotificationService(IAppSettingRepository settings, ISecretProtector protector, INotifier notifier,
    IServerRepository servers) : IAlertNotificationService
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
        if (input.NtfyEnabled && string.IsNullOrWhiteSpace(input.NtfyTopic) && string.IsNullOrWhiteSpace(existing.ProtectedNtfyTopic))
            throw new ArgumentException("Enter an ntfy topic.");
        var next = new StoredSettings(input.TelegramEnabled,
            string.IsNullOrWhiteSpace(input.TelegramBotToken) ? existing.TelegramBotToken : protector.Protect(input.TelegramBotToken.Trim()),
            input.TelegramChatId.Trim(), input.NtfyEnabled, input.NtfyBaseUrl.Trim().TrimEnd('/'), null,
            string.IsNullOrWhiteSpace(input.NtfyToken) ? existing.NtfyToken : protector.Protect(input.NtfyToken.Trim()),
            input.MinimumSeverity,
            string.IsNullOrWhiteSpace(input.NtfyTopic) ? existing.ProtectedNtfyTopic : protector.Protect(input.NtfyTopic.Trim()));
        await WriteAsync(next, cancellationToken);
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

    /// <summary>
    /// D-028: a notification carries only server name, rule, severity and state. Alert titles, targets and details can hold
    /// IPs, user names, commands or sudo arguments, so they never leave the app.
    /// </summary>
    public static string FormatAlertMessage(string serverName, string rule, AlertSeverity severity, bool resolved) =>
        $"[{severity}] {serverName} · {rule} · {(resolved ? "resolved" : "opened")}";

    private async Task NotifyAlertAsync(AlertDto alert, bool resolved, CancellationToken cancellationToken)
    {
        var stored = await ReadAsync(cancellationToken);
        if (alert.Severity < stored.MinimumSeverity) return;
        var destinations = GetDestinations(stored);
        if (destinations.Count == 0) return;
        var server = alert.ServerId is { } serverId ? await servers.GetAsync(serverId, cancellationToken) : null;
        var message = FormatAlertMessage(server?.Name ?? "DevOps Agent", alert.Rule, alert.Severity, resolved);
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
        if (stored.NtfyEnabled && !string.IsNullOrWhiteSpace(stored.NtfyBaseUrl) && !string.IsNullOrWhiteSpace(stored.ProtectedNtfyTopic))
            result.Add(new("ntfy", string.IsNullOrWhiteSpace(stored.NtfyToken) ? string.Empty : protector.Unprotect(stored.NtfyToken),
                stored.NtfyBaseUrl, protector.Unprotect(stored.ProtectedNtfyTopic)));
        return result;
    }

    private async Task<StoredSettings> ReadAsync(CancellationToken cancellationToken)
    {
        var json = await settings.GetAsync(SettingKey, cancellationToken);
        var stored = json is null ? StoredSettings.Default : JsonSerializer.Deserialize<StoredSettings>(json, JsonOptions) ?? StoredSettings.Default;
        if (string.IsNullOrWhiteSpace(stored.NtfyTopic)) return stored;

        // Settings written before D-028 kept the ntfy topic in plain text: protect it and rewrite the setting once.
        var migrated = stored with
        {
            NtfyTopic = null,
            ProtectedNtfyTopic = string.IsNullOrWhiteSpace(stored.ProtectedNtfyTopic) ? protector.Protect(stored.NtfyTopic.Trim()) : stored.ProtectedNtfyTopic
        };
        await WriteAsync(migrated, cancellationToken);
        return migrated;
    }

    private Task WriteAsync(StoredSettings value, CancellationToken cancellationToken) =>
        settings.SetAsync(SettingKey, JsonSerializer.Serialize(value, JsonOptions), cancellationToken);

    private static NotificationSettings ToPublicSettings(StoredSettings stored) => new(stored.TelegramEnabled,
        stored.TelegramChatId, !string.IsNullOrWhiteSpace(stored.TelegramBotToken), stored.NtfyEnabled,
        stored.NtfyBaseUrl, !string.IsNullOrWhiteSpace(stored.ProtectedNtfyTopic), !string.IsNullOrWhiteSpace(stored.NtfyToken),
        stored.MinimumSeverity);

    private static void Validate(NotificationSettingsInput input)
    {
        if (!Enum.IsDefined(input.MinimumSeverity)) throw new ArgumentException("Choose a valid minimum severity.");
        if (input.TelegramEnabled && string.IsNullOrWhiteSpace(input.TelegramChatId))
            throw new ArgumentException("Enter a Telegram chat ID.");
        if (input.NtfyEnabled)
        {
            if (!Uri.TryCreate(input.NtfyBaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                throw new ArgumentException("Enter a valid ntfy HTTP or HTTPS base URL without credentials or query parameters.");
        }
    }

    /// <param name="NtfyTopic">Legacy plain-text topic; only read for migration, never written.</param>
    private sealed record StoredSettings(bool TelegramEnabled, string TelegramBotToken, string TelegramChatId,
        bool NtfyEnabled, string NtfyBaseUrl, string? NtfyTopic, string NtfyToken, AlertSeverity MinimumSeverity,
        string? ProtectedNtfyTopic = null)
    {
        public static StoredSettings Default { get; } = new(false, string.Empty, string.Empty, false, "https://ntfy.sh", null,
            string.Empty, AlertSeverity.Medium);
    }
}
