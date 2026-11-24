using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Notifications;

namespace DevOpsAgent.Web.Hosted;

/// <summary>
/// Checks periodically whether the daily infrastructure digest is due and delivers it via configured notifiers.
/// </summary>
public sealed class DailyDigestHostedService(
    IServiceScopeFactory scopeFactory,
    IHostApplicationLifetime lifetime,
    ILogger<DailyDigestHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken serviceStopping)
    {
        using var shutdown = new DetachedCancellation(lifetime.ApplicationStopping);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(serviceStopping, shutdown.Token);
        var stoppingToken = linked.Token;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await RunOnceIfDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Daily digest check failed.");
            }
        }
    }

    public async Task<bool> RunOnceIfDueAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var digest = scope.ServiceProvider.GetRequiredService<DailyDigestService>();
        var result = await digest.SendIfDueAsync(cancellationToken);
        if (result.Sent)
        {
            logger.LogInformation("Delivered daily digest to {EndpointCount} notification endpoint(s).", result.DeliveredEndpoints);
        }

        return result.Sent;
    }
}
