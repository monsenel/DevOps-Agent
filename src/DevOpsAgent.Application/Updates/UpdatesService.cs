using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Domain.Updates;

namespace DevOpsAgent.Application.Updates;

public sealed class UpdatesService(
    IServerRepository servers,
    IUpdatePackageRepository packages,
    IUpdateScanRepository scans,
    IUpdatesCollector collector,
    TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<ServerUpdateStatus>> ListAsync(CancellationToken cancellationToken)
    {
        var inventory = await servers.ListAsync(cancellationToken);
        var saved = await packages.ListAsync(cancellationToken);
        var latestScans = (await scans.ListAsync(cancellationToken)).ToDictionary(scan => scan.ServerId);
        return inventory.Select(server =>
        {
            var items = saved.Where(item => item.ServerId == server.Id).ToArray();
            latestScans.TryGetValue(server.Id, out var scan);
            return new ServerUpdateStatus(server.Id, server.Name, server.Group,
                Enum.TryParse<PackageManagerKind>(scan?.PackageManager, true, out var manager) ? manager : null,
                scan?.CheckedAtUtc,
                items.Select(item => new UpdatePackageDto(item.Name, item.CurrentVersion, item.CandidateVersion, item.Repository, item.IsSecurity)).ToArray(), scan?.Error);
        }).ToArray();
    }

    public async Task<IReadOnlyList<ServerUpdateStatus>> ScanAsync(CancellationToken cancellationToken)
    {
        var inventory = await servers.ListAsync(cancellationToken);
        using var semaphore = new SemaphoreSlim(8);
        var scansResult = await Task.WhenAll(inventory.Select(async server =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var collected = await collector.CollectAsync(server.Id, cancellationToken);
                var rows = JsonSerializer.Deserialize<UpdatePackageDto[]>(collected.ResultJson) ?? [];
                return (Server: server, Collected: collected, Rows: rows, Error: (string?)null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) { return (Server: server, Collected: (CollectedUpdateResult?)null, Rows: Array.Empty<UpdatePackageDto>(), Error: exception.Message); }
            finally { semaphore.Release(); }
        }));

        var results = new List<ServerUpdateStatus>(scansResult.Length);
        foreach (var item in scansResult)
        {
            var checkedAt = timeProvider.GetUtcNow();
            if (item.Error is not null || item.Collected is null)
            {
                await scans.SaveAsync(new UpdateScan(item.Server.Id, null, checkedAt, item.Error), CancellationToken.None);
                results.Add(new ServerUpdateStatus(item.Server.Id, item.Server.Name, item.Server.Group, null, checkedAt, [], item.Error));
                continue;
            }
            await packages.ReplaceForServerAsync(item.Server.Id, item.Rows.Select(row => new UpdatePackage(item.Server.Id,
                row.Name, row.CurrentVersion, row.CandidateVersion, row.Repository, row.IsSecurity, checkedAt)).ToArray(), cancellationToken);
            await scans.SaveAsync(new UpdateScan(item.Server.Id, item.Collected.PackageManager.ToString(), checkedAt, null), cancellationToken);
            results.Add(new ServerUpdateStatus(item.Server.Id, item.Server.Name, item.Server.Group, item.Collected.PackageManager, checkedAt, item.Rows, null));
        }
        return results.OrderBy(result => result.ServerName, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
