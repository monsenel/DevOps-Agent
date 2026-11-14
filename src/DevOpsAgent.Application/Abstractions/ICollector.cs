using System.Text.Json;
using DevOpsAgent.Domain.Servers;

namespace DevOpsAgent.Application.Abstractions;

public interface ICollector
{
    string Name { get; }
    Task<bool> IsApplicableAsync(ServerContext context, CancellationToken cancellationToken);
    Task<CollectorOutput> CollectAsync(ServerContext context, CancellationToken cancellationToken);
    CollectorResult Parse(CollectorOutput output);
}

public sealed class ServerContext(Server server, ISshRunner sshRunner)
{
    private readonly Dictionary<string, JsonElement> previousResults = new(StringComparer.Ordinal);

    public Server Server { get; } = server;
    public ISshRunner SshRunner { get; } = sshRunner;

    public bool TryGetPreviousResult<T>(string collectorName, out T? result) where T : class
    {
        if (previousResults.TryGetValue(collectorName, out var json))
        {
            result = json.Deserialize<T>();
            return result is not null;
        }

        result = null;
        return false;
    }

    internal void AddResult(string collectorName, JsonElement result) => previousResults[collectorName] = result.Clone();
}

public sealed record CollectorOutput(
    string StandardOutput,
    string StandardError,
    int? ExitCode,
    TimeSpan Duration);

public sealed record CollectorResult(JsonElement? Data = null, string? Error = null)
{
    public bool IsSuccess => Error is null;
}

public enum DiscoveryProgressStage
{
    Started,
    ServerStarted,
    CollectorStarted,
    CollectorCompleted,
    ServerCompleted,
    Completed
}

public sealed record DiscoveryProgress(
    Guid SnapshotId,
    DiscoveryProgressStage Stage,
    Guid? ServerId = null,
    string? ServerName = null,
    string? CollectorName = null,
    string? Message = null);
