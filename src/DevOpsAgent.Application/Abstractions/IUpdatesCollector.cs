using DevOpsAgent.Application.Updates;
using DevOpsAgent.Domain.Servers;

namespace DevOpsAgent.Application.Abstractions;

/// <summary>
/// Runs an update scan over SSH only. It must not touch the database: <see cref="UpdatesService"/> calls it for several
/// servers in parallel and resolves the server and its execution mode beforehand.
/// </summary>
public interface IUpdatesCollector
{
    /// <param name="cacheOnly">Read-only group: read cached package lists only, never refresh them (D-027).</param>
    Task<CollectedUpdateResult> CollectAsync(Server server, bool cacheOnly, CancellationToken cancellationToken);
}
