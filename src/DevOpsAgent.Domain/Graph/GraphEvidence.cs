namespace DevOpsAgent.Domain.Graph;

public sealed class GraphEvidence
{
    private GraphEvidence()
    {
    }

    public GraphEvidence(Guid snapshotId, Guid? nodeId, Guid? edgeId, string collector, string description)
    {
        Id = Guid.NewGuid();
        SnapshotId = snapshotId;
        NodeId = nodeId;
        EdgeId = edgeId;
        Collector = collector;
        Description = description;
    }

    public Guid Id { get; private set; }
    public Guid SnapshotId { get; private set; }
    public Guid? NodeId { get; private set; }
    public Guid? EdgeId { get; private set; }
    public string Collector { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
}
