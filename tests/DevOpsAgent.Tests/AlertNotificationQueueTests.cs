using DevOpsAgent.Application.Monitoring;
using DevOpsAgent.Application.Notifications;
using DevOpsAgent.Domain.Monitoring;

namespace DevOpsAgent.Tests;

public sealed class AlertNotificationQueueTests
{
    [Fact]
    public async Task NotifyReturnsAtOnceAndTheSenderReceivesOpenedAndResolvedInOrder()
    {
        var queue = new AlertNotificationQueue();

        await queue.NotifyOpenedAsync(Alert("first"), CancellationToken.None);
        await queue.NotifyResolvedAsync(Alert("second"), CancellationToken.None);

        var opened = await queue.Reader.ReadAsync();
        var resolved = await queue.Reader.ReadAsync();
        Assert.Equal("first", opened.Alert.Title);
        Assert.False(opened.Resolved);
        Assert.Equal("second", resolved.Alert.Title);
        Assert.True(resolved.Resolved);
        Assert.Equal(0, queue.DroppedCount);
    }

    [Fact]
    public async Task AFullQueueDropsTheNewestNotificationAndCountsIt()
    {
        var queue = new AlertNotificationQueue();

        for (var index = 0; index < AlertNotificationQueue.Capacity + 5; index++)
            await queue.NotifyOpenedAsync(Alert($"alert-{index}"), CancellationToken.None);

        Assert.Equal(5, queue.DroppedCount);
        var first = await queue.Reader.ReadAsync();
        Assert.Equal("alert-0", first.Alert.Title);
    }

    private static AlertDto Alert(string title) => new(Guid.NewGuid(), "rule", AlertSeverity.High, AlertState.Open, null,
        "target", title, "details", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, 1);
}
