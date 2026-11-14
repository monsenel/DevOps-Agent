using System.Net;
using System.Text;
using DevOpsAgent.Application.Notifications;
using DevOpsAgent.Infrastructure.Notifications;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevOpsAgent.Tests;

public sealed class HttpNotifierTests
{
    [Fact]
    public async Task TelegramUsesSendMessageAndReportsApiRejection()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json")
        });
        var notifier = new HttpNotifier(new HttpClient(handler), NullLogger<HttpNotifier>.Instance);

        await notifier.SendAsync(new("Telegram", "bot-token", "chat-1", null), "test alert", CancellationToken.None);

        Assert.Contains("/botbot-token/sendMessage", handler.RequestUri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("\"chat_id\":\"chat-1\"", handler.Body, StringComparison.Ordinal);
        Assert.Contains("test alert", handler.Body, StringComparison.Ordinal);

        var rejected = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"ok\":false}", Encoding.UTF8, "application/json")
        });
        var rejectedNotifier = new HttpNotifier(new HttpClient(rejected), NullLogger<HttpNotifier>.Instance);
        await Assert.ThrowsAsync<HttpRequestException>(() => rejectedNotifier.SendAsync(
            new("Telegram", "bot-token", "chat-1", null), "test alert", CancellationToken.None));
    }

    [Fact]
    public async Task NtfyPostsToTopicWithOptionalBearerToken()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var notifier = new HttpNotifier(new HttpClient(handler), NullLogger<HttpNotifier>.Instance);

        await notifier.SendAsync(new("ntfy", "ntfy-token", "https://ntfy.example/base", "ops room"),
            "test alert", CancellationToken.None);

        Assert.Equal("https://ntfy.example/base/ops%20room", handler.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer ntfy-token", handler.Authorization);
        Assert.Equal("test alert", handler.Body);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public string Body { get; private set; } = string.Empty;
        public string? Authorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Authorization = request.Headers.Authorization?.ToString();
            return respond(request);
        }
    }
}
