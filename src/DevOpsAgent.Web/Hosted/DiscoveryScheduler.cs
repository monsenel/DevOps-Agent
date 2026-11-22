using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;

namespace DevOpsAgent.Web.Hosted;

public sealed class DiscoveryScheduler(
    IServiceScopeFactory scopeFactory,
    IDiscoveryCoordinator discoveryCoordinator,
    TimeProvider timeProvider,
    IHostApplicationLifetime lifetime,
    ILogger<DiscoveryScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken serviceStopping)
    {
        // The token of a hosted service is only cancelled when the host reaches that service in its sequential stop, which
        // can be long after SIGTERM while an earlier service is still stopping (T7.R17). ApplicationStopping fires first; it is
        // re-delivered on a pool thread so the SIGTERM thread never runs this service's unwinding.
        using var shutdown = new DetachedCancellation(lifetime.ApplicationStopping);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(serviceStopping, shutdown.Token);
        var stoppingToken = linked.Token;
        var lastAttemptAt = timeProvider.GetUtcNow();
        try
        {
            using var scope = scopeFactory.CreateScope();
            var snapshots = scope.ServiceProvider.GetRequiredService<ISnapshotRepository>();
            if (await snapshots.GetLatestRunAsync(stoppingToken) is { } latest)
            {
                lastAttemptAt = latest.StartedAtUtc;
            }
        }
        catch (OperationCanceledException exception) when (exception is SshShutdownException || stoppingToken.IsCancellationRequested)
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
                var intervalHours = await schedule.GetIntervalHoursAsync(stoppingToken);
                var latest = await snapshots.GetLatestRunAsync(stoppingToken);
                var referenceTime = latest?.StartedAtUtc > lastAttemptAt ? latest.StartedAtUtc : lastAttemptAt;
                if (timeProvider.GetUtcNow() < referenceTime.AddHours(intervalHours)) continue;

                lastAttemptAt = timeProvider.GetUtcNow();
                var snapshot = await discoveryCoordinator.RunAsync(progress: null, stoppingToken);
                logger.LogInformation("Scheduled discovery {SnapshotId} completed with {SucceededServers} succeeded and {FailedServers} failed servers.",
                    snapshot.Id, snapshot.SucceededServerCount, snapshot.FailedServerCount);
            }
            catch (OperationCanceledException exception) when (exception is SshShutdownException || stoppingToken.IsCancellationRequested)
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
