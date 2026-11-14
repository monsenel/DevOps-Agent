using System.Text.Json;

namespace DevOpsAgent.Application.Discovery;

public enum GraphView
{
    Service,
    Network
}

public sealed record GraphSnapshotDto(
    Guid SnapshotId,
    string View,
    IReadOnlyList<GraphElementDto> Nodes,
    IReadOnlyList<GraphElementDto> Edges,
    IReadOnlyList<GraphListenerDto> Listeners,
    IReadOnlyList<GraphEvidenceDto> Evidence);

public sealed record GraphElementDto(JsonElement Data);

public sealed record GraphListenerDto(Guid Id, Guid NodeId, string Address, int Port, string Protocol, string? ProcessName, int? ProcessId);

public sealed record GraphEvidenceDto(Guid Id, Guid? NodeId, Guid? EdgeId, string Collector, string Description);
