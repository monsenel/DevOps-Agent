using DevOpsAgent.Application.Notifications;

namespace DevOpsAgent.Web.Hosted;

/// <summary>Sends queued alert notifications one at a time, each in its own scope (T7.R7).</summary>
public sealed class AlertNotificationDispatcher(AlertNotificationQueue queue, IServiceScopeFactory scopes,
    ILogger<AlertNotificationDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        long reportedDrops = 0;
        try
        {
            await foreach (var job in queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    var notifications = scope.ServiceProvider.GetRequiredService<NotificationService>();
                    if (job.Resolved) await notifications.NotifyResolvedAsync(job.Alert, stoppingToken);
                    else await notifications.NotifyOpenedAsync(job.Alert, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Alert notification could not be processed ({FailureType}).", exception.GetType().Name);
                }

                var drops = queue.DroppedCount;
                if (drops != reportedDrops)
                {
                    logger.LogWarning("Alert notification queue is full; {Dropped} notification(s) dropped so far.", drops);
                    reportedDrops = drops;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
