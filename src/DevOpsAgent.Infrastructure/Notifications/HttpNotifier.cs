using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DevOpsAgent.Application.Notifications;
using Microsoft.Extensions.Logging;

namespace DevOpsAgent.Infrastructure.Notifications;

public sealed class HttpNotifier(HttpClient httpClient, ILogger<HttpNotifier> logger) : INotifier
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task SendAsync(NotificationEndpoint endpoint, string message, CancellationToken cancellationToken)
    {
        try
        {
            switch (endpoint.Kind)
            {
                case "Telegram": await SendTelegramAsync(endpoint, message, cancellationToken); break;
                case "ntfy": await SendNtfyAsync(endpoint, message, cancellationToken); break;
                default: throw new ArgumentException("Unknown notification provider.", nameof(endpoint));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogWarning("Notification delivery failed for {Provider} ({FailureType}).", endpoint.Kind, exception.GetType().Name);
            throw;
        }
    }

    private async Task SendTelegramAsync(NotificationEndpoint endpoint, string message, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(
            $"https://api.telegram.org/bot{endpoint.Secret}/sendMessage",
            new { chat_id = endpoint.Destination, text = message }, JsonOptions, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            throw new HttpRequestException("Telegram rejected the notification.");
    }

    private async Task SendNtfyAsync(NotificationEndpoint endpoint, string message, CancellationToken cancellationToken)
    {
        var baseUri = new Uri(endpoint.Destination.TrimEnd('/') + "/", UriKind.Absolute);
        var topicUri = new Uri(baseUri, Uri.EscapeDataString(endpoint.SecondaryDestination ?? string.Empty));
        using var request = new HttpRequestMessage(HttpMethod.Post, topicUri)
        {
            Content = new StringContent(message, Encoding.UTF8, "text/plain")
        };
        if (!string.IsNullOrWhiteSpace(endpoint.Secret))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Secret);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
