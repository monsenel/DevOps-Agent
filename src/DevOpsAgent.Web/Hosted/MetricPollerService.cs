using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Monitoring;

namespace DevOpsAgent.Web.Hosted;

public sealed class MetricPollerService(IServiceScopeFactory scopeFactory, IHostApplicationLifetime lifetime,
    ILogger<MetricPollerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken serviceStopping)
    {
        // The token of a hosted service is only cancelled when the host reaches that service in its sequential stop, which
        // can be long after SIGTERM while an earlier service is still stopping (T7.R17). ApplicationStopping fires first; it is
        // re-delivered on a pool thread so the SIGTERM thread never runs this service's unwinding.
        using var shutdown = new DetachedCancellation(lifetime.ApplicationStopping);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(serviceStopping, shutdown.Token);
        var stoppingToken = linked.Token;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        while (!stoppingToken.IsCancellationRequested)
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<MetricService>().PollAsync(stoppingToken);
            }
            catch (OperationCanceledException exception) when (exception is SshShutdownException || stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogWarning(exception, "Metric polling cycle failed"); }
            // T7.R1: a cycle must finish within its 60 s interval even when hosts are unreachable.
            logger.LogInformation("Metric polling cycle finished in {ElapsedMs} ms", started.ElapsedMilliseconds);

            try { await timer.WaitForNextTickAsync(stoppingToken); }
            catch (OperationCanceledException exception) when (exception is SshShutdownException || stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
