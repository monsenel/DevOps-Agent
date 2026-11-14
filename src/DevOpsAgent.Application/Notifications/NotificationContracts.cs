using DevOpsAgent.Domain.Monitoring;
using DevOpsAgent.Application.Monitoring;

namespace DevOpsAgent.Application.Notifications;

public sealed record NotificationSettings(bool TelegramEnabled, string TelegramChatId, bool HasTelegramBotToken,
    bool NtfyEnabled, string NtfyBaseUrl, string NtfyTopic, bool HasNtfyToken, AlertSeverity MinimumSeverity);

public sealed record NotificationSettingsInput(bool TelegramEnabled, string TelegramBotToken, string TelegramChatId,
    bool NtfyEnabled, string NtfyBaseUrl, string NtfyTopic, string NtfyToken, AlertSeverity MinimumSeverity);

public sealed record NotificationEndpoint(string Kind, string Secret, string Destination, string? SecondaryDestination);

public interface INotifier
{
    Task SendAsync(NotificationEndpoint endpoint, string message, CancellationToken cancellationToken);
}

public interface IAlertNotificationService
{
    Task NotifyOpenedAsync(AlertDto alert, CancellationToken cancellationToken);
    Task NotifyResolvedAsync(AlertDto alert, CancellationToken cancellationToken);
}
