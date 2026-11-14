namespace DevOpsAgent.Domain.Graph;

public sealed class GraphEdge
{
    private GraphEdge()
    {
    }

    public GraphEdge(
        Guid snapshotId,
        Guid sourceNodeId,
        Guid targetNodeId,
        GraphEdgeKind kind,
        int? port = null,
        string? protocol = null)
    {
        Id = Guid.NewGuid();
        SnapshotId = snapshotId;
        SourceNodeId = sourceNodeId;
        TargetNodeId = targetNodeId;
        Kind = kind;
        Port = port;
        Protocol = protocol;
    }

    public Guid Id { get; private set; }
    public Guid SnapshotId { get; private set; }
    public Guid SourceNodeId { get; private set; }
    public Guid TargetNodeId { get; private set; }
    public int? Port { get; private set; }
    public string? Protocol { get; private set; }
    public GraphEdgeKind Kind { get; private set; }
}
