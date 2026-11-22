using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Updates;

namespace DevOpsAgent.Web.Hosted;

/// <summary>
/// Runs periodic OS package update scans on the owner-configured interval (T9.1). Read-only groups remain cache-only (D-027).
/// </summary>
public sealed class UpdateScanHostedService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    IHostApplicationLifetime lifetime,
    ILogger<UpdateScanHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);
    private DateTimeOffset? _lastAttemptAt;

    protected override async Task ExecuteAsync(CancellationToken serviceStopping)
    {
        using var shutdown = new DetachedCancellation(lifetime.ApplicationStopping);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(serviceStopping, shutdown.Token);
        var stoppingToken = linked.Token;

        try
        {
            await using var initScope = scopeFactory.CreateAsyncScope();
            var scans = initScope.ServiceProvider.GetRequiredService<IUpdateScanRepository>();
            var existing = await scans.ListAsync(stoppingToken);
            if (existing.Count > 0)
            {
                _lastAttemptAt = existing.Max(scan => scan.CheckedAtUtc);
            }
        }
        catch (OperationCanceledException exception) when (exception is SshShutdownException || stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not read latest update scans; scheduling from the current time.");
            _lastAttemptAt = timeProvider.GetUtcNow();
        }

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
            catch (OperationCanceledException exception) when (exception is SshShutdownException || stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Scheduled update scan did not complete.");
            }
        }
    }

    public async Task<bool> RunOnceIfDueAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var schedule = scope.ServiceProvider.GetRequiredService<UpdateScheduleService>();
        var intervalHours = await schedule.GetIntervalHoursAsync(cancellationToken);
        if (intervalHours <= UpdateScheduleService.DisabledIntervalHours)
        {
            return false;
        }

        var servers = scope.ServiceProvider.GetRequiredService<IServerRepository>();
        if ((await servers.ListAsync(cancellationToken)).Count == 0)
        {
            return false;
        }

        var scans = scope.ServiceProvider.GetRequiredService<IUpdateScanRepository>();
        var existing = await scans.ListAsync(cancellationToken);
        DateTimeOffset? latestStored = existing.Count > 0 ? existing.Max(scan => scan.CheckedAtUtc) : null;
        DateTimeOffset? referenceTime = latestStored is not null && (_lastAttemptAt is null || latestStored > _lastAttemptAt)
            ? latestStored
            : _lastAttemptAt;

        var now = timeProvider.GetUtcNow();
        if (!UpdateScheduleService.IsDue(referenceTime, intervalHours, now))
        {
            return false;
        }

        _lastAttemptAt = now;
        var updates = scope.ServiceProvider.GetRequiredService<UpdatesService>();
        var results = await updates.ScanAsync(cancellationToken);
        var securityCount = results.Sum(server => server.Packages.Count(package => package.IsSecurity));
        logger.LogInformation("Scheduled update scan finished across {ServerCount} server(s) with {SecurityUpdates} pending security update(s).",
            results.Count, securityCount);
        return true;
    }
}
