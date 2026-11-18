using System.Threading.Channels;
using DevOpsAgent.Application.Monitoring;

namespace DevOpsAgent.Application.Notifications;

/// <summary>
/// Hands alert notifications to one background sender, so a slow or unreachable Telegram/ntfy endpoint never delays
/// alert evaluation (T7.R7). The queue is bounded: when it is full the newest notification is dropped and counted, which
/// keeps the evaluation cycle's memory and timing bounded. Alerts themselves are always stored; only the message is lost.
/// </summary>
public sealed class AlertNotificationQueue : IAlertNotificationService
{
    public const int Capacity = 256;

    private readonly Channel<NotificationJob> channel = Channel.CreateBounded<NotificationJob>(new BoundedChannelOptions(Capacity)
    {
        // Wait: TryWrite fails (never blocks) when full, so the drop is counted. DropWrite would report success for a discarded item.
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false
    });

    private long dropped;

    public ChannelReader<NotificationJob> Reader => channel.Reader;

    /// <summary>Notifications that could not be queued because the queue was full.</summary>
    public long DroppedCount => Interlocked.Read(ref dropped);

    public Task NotifyOpenedAsync(AlertDto alert, CancellationToken cancellationToken)
    {
        Enqueue(new NotificationJob(alert, Resolved: false));
        return Task.CompletedTask;
    }

    public Task NotifyResolvedAsync(AlertDto alert, CancellationToken cancellationToken)
    {
        Enqueue(new NotificationJob(alert, Resolved: true));
        return Task.CompletedTask;
    }

    private void Enqueue(NotificationJob job)
    {
        if (!channel.Writer.TryWrite(job)) Interlocked.Increment(ref dropped);
    }

    public sealed record NotificationJob(AlertDto Alert, bool Resolved);
}
