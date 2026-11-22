using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Discovery;
using DevOpsAgent.Domain.Discovery;

namespace DevOpsAgent.Web.Hosted;

/// <summary>
/// Executes discovery inside an independent DI scope linked only to host shutdown (T7.R8), so closing a browser tab
/// never disposes the <c>AppDbContext</c> or cancels the run.
/// </summary>
public sealed class HostDiscoveryScopeRunner(
    IServiceScopeFactory scopeFactory,
    IHostApplicationLifetime lifetime) : IDiscoveryScopeRunner
{
    public async Task<Snapshot> ExecuteAsync(
        Func<DiscoveryService, IProgress<DiscoveryProgress>, CancellationToken, Task<Snapshot>> work,
        IProgress<DiscoveryProgress> progress)
    {
        using var shutdown = new DetachedCancellation(lifetime.ApplicationStopping);
        await using var scope = scopeFactory.CreateAsyncScope();
        var discovery = scope.ServiceProvider.GetRequiredService<DiscoveryService>();
        return await work(discovery, progress, shutdown.Token);
    }
}
