using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;

namespace DevOpsAgent.Web.Hosted;

public sealed class DiscoveryScheduler(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<DiscoveryScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastAttemptAt = timeProvider.GetUtcNow();
        try
        {
            using var scope = scopeFactory.CreateScope();
            var snapshots = scope.ServiceProvider.GetRequiredService<ISnapshotRepository>();
            if (await snapshots.GetLatestAsync(stoppingToken) is { } latest)
            {
                lastAttemptAt = latest.StartedAtUtc;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            // An unhandled exception here would stop the whole host; fall back to "now" and keep the scheduler alive.
            logger.LogWarning(exception, "Could not read the latest discovery snapshot; scheduling from the current time.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            try
            {
                using var scope = scopeFactory.CreateScope();
                var schedule = scope.ServiceProvider.GetRequiredService<DiscoveryScheduleService>();
                var snapshots = scope.ServiceProvider.GetRequiredService<ISnapshotRepository>();
                var discovery = scope.ServiceProvider.GetRequiredService<DiscoveryService>();
                var intervalHours = await schedule.GetIntervalHoursAsync(stoppingToken);
                var latest = await snapshots.GetLatestAsync(stoppingToken);
                var referenceTime = latest?.StartedAtUtc > lastAttemptAt ? latest.StartedAtUtc : lastAttemptAt;
                if (timeProvider.GetUtcNow() < referenceTime.AddHours(intervalHours)) continue;

                lastAttemptAt = timeProvider.GetUtcNow();
                var snapshot = await discovery.RunAsync(progress: null, stoppingToken);
                logger.LogInformation("Scheduled discovery {SnapshotId} completed with {SucceededServers} succeeded and {FailedServers} failed servers.",
                    snapshot.Id, snapshot.SucceededServerCount, snapshot.FailedServerCount);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Scheduled discovery did not complete.");
            }
        }
    }
}
