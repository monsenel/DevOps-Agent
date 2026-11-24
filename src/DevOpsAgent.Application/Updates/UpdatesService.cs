using System.Text.Json;
using DevOpsAgent.Application.Abstractions;
using DevOpsAgent.Application.Agent;
using DevOpsAgent.Domain.Agent;
using DevOpsAgent.Domain.Updates;

namespace DevOpsAgent.Application.Updates;

public sealed class UpdatesService(
    IServerRepository servers,
    IUpdatePackageRepository packages,
    IUpdateScanRepository scans,
    IUpdatesCollector collector,
    ExecutionModeService executionModes,
    TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<ServerUpdateStatus>> ListAsync(CancellationToken cancellationToken)
    {
        var inventory = await servers.ListAsync(cancellationToken);
        var saved = await packages.ListAsync(cancellationToken);
        var latestScans = (await scans.ListAsync(cancellationToken)).ToDictionary(scan => scan.ServerId);
        var readOnly = await ReadOnlyGroupsAsync(inventory, cancellationToken);
        return inventory.Select(server =>
        {
            var items = saved.Where(item => item.ServerId == server.Id).ToArray();
            latestScans.TryGetValue(server.Id, out var scan);
            return new ServerUpdateStatus(server.Id, server.Name, server.Group,
                Enum.TryParse<PackageManagerKind>(scan?.PackageManager, true, out var manager) ? manager : null,
                scan?.CheckedAtUtc,
                items.Select(item => new UpdatePackageDto(item.Name, item.CurrentVersion, item.CandidateVersion, item.Repository, item.IsSecurity)).ToArray(), scan?.Error,
                scan?.PackageListsRefreshedAtUtc, scan?.CacheOnly ?? false, readOnly.Contains(server.Group), ReadSources(scan?.SourcesJson));
        }).ToArray();
    }

    private static IReadOnlyList<PackageSourceDto> ReadSources(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<PackageSourceDto[]>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    // Read one group at a time: the scoped DbContext must not be used concurrently.
    private async Task<HashSet<string>> ReadOnlyGroupsAsync(IEnumerable<Domain.Servers.Server> inventory, CancellationToken cancellationToken)
    {
        var readOnly = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in inventory.Select(server => server.Group).Distinct(StringComparer.Ordinal))
            if ((await executionModes.GetForGroupAsync(group, cancellationToken)).Mode == ExecutionMode.ReadOnly) readOnly.Add(group);
        return readOnly;
    }

    public async Task<IReadOnlyList<ServerUpdateStatus>> ScanAsync(CancellationToken cancellationToken)
    {
        var inventory = await servers.ListAsync(cancellationToken);
        // Database reads happen before the parallel SSH phase: the scoped DbContext must not be used concurrently.
        // A read-only group (or an unreadable policy, which fails closed to read-only) is never written to, not even by apt-get update.
        var cacheOnly = new Dictionary<Guid, bool>();
        foreach (var server in inventory)
            cacheOnly[server.Id] = (await executionModes.GetForGroupAsync(server.Group, cancellationToken)).Mode == ExecutionMode.ReadOnly;
        using var semaphore = new SemaphoreSlim(8);
        var scansResult = await Task.WhenAll(inventory.Select(async server =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var collected = await collector.CollectAsync(server, cacheOnly[server.Id], cancellationToken);
                var rows = JsonSerializer.Deserialize<UpdatePackageDto[]>(collected.ResultJson) ?? [];
                return (Server: server, Collected: collected, Rows: rows, Error: (string?)null);
            }
            catch (SshShutdownException) { throw; }
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
                results.Add(new ServerUpdateStatus(item.Server.Id, item.Server.Name, item.Server.Group, null, checkedAt, [], item.Error,
                    ReadOnly: cacheOnly[item.Server.Id]));
                continue;
            }
            await packages.ReplaceForServerAsync(item.Server.Id, item.Rows.Select(row => new UpdatePackage(item.Server.Id,
                row.Name, row.CurrentVersion, row.CandidateVersion, row.Repository, row.IsSecurity, checkedAt)).ToArray(), cancellationToken);
            await scans.SaveAsync(new UpdateScan(item.Server.Id, item.Collected.PackageManager.ToString(), checkedAt, null,
                item.Collected.PackageListsRefreshedAtUtc, item.Collected.CacheOnly, item.Collected.SourcesJson), cancellationToken);
            results.Add(new ServerUpdateStatus(item.Server.Id, item.Server.Name, item.Server.Group, item.Collected.PackageManager, checkedAt, item.Rows, null,
                item.Collected.PackageListsRefreshedAtUtc, item.Collected.CacheOnly, cacheOnly[item.Server.Id], ReadSources(item.Collected.SourcesJson)));
        }
        return results.OrderBy(result => result.ServerName, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
