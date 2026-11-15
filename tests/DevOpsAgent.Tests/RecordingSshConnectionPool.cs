using DevOpsAgent.Application.Abstractions;

namespace DevOpsAgent.Tests;

internal sealed class RecordingSshConnectionPool : ISshConnectionPool
{
    public List<Guid> Evicted { get; } = [];

    public Task<ISshCommandSession> GetSessionAsync(Guid serverId, bool bypassBackoff, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Tests never open SSH connections.");

    public void Evict(Guid serverId) => Evicted.Add(serverId);

    public void Dispose()
    {
    }
}
