using DevOpsAgent.Application.Monitoring;
using DevOpsAgent.Domain.Monitoring;

namespace DevOpsAgent.Web.Hosted;

public sealed class AlertEngineHostedService(IServiceScopeFactory scopes, AlertStatusState status,
    ILogger<AlertEngineHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
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
