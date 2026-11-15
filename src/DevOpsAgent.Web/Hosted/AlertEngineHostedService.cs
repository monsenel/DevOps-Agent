using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Monitoring;
using DevOpsAgent.Domain.Monitoring;

namespace DevOpsAgent.Web.Hosted;

public sealed class AlertEngineHostedService(IServiceScopeFactory scopes, AlertStatusState status,
    IHostApplicationLifetime lifetime, ILogger<AlertEngineHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken serviceStopping)
    {
        // The token of a hosted service is only cancelled when the host reaches that service in its sequential stop, which
        // can be long after SIGTERM while an earlier service is still stopping (T7.R17). ApplicationStopping fires first; it is
        // re-delivered on a pool thread so the SIGTERM thread never runs this service's unwinding.
        using var shutdown = new DetachedCancellation(lifetime.ApplicationStopping);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(serviceStopping, shutdown.Token);
        var stoppingToken = linked.Token;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                using var scope = scopes.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<AlertService>();
                await service.EvaluateAsync(stoppingToken);
                status.Set(await service.GetOpenCountAsync(stoppingToken));
            }
            catch (OperationCanceledException exception) when (exception is SshShutdownException || stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "Alert evaluation failed."); }
            logger.LogInformation("Alert evaluation finished in {ElapsedMs} ms", started.ElapsedMilliseconds);
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

public sealed class AlertStatusState
{
    public event Action? Changed;
    public int OpenCount { get; private set; }
    public void Set(int count)
    {
        if (OpenCount == count) return;
        OpenCount = count;
        Changed?.Invoke();
    }
}
