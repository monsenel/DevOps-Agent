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

public sealed record ServerContext(Server Server, ISshRunner SshRunner);

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
