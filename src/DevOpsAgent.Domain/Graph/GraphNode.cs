namespace DevOpsAgent.Domain.Graph;

public sealed class GraphNode
{
    private GraphNode()
    {
    }

    public GraphNode(
        Guid snapshotId,
        string key,
        GraphNodeKind kind,
        string name,
        Guid? parentNodeId,
        Guid? serverId,
        string attributesJson)
    {
        Id = Guid.NewGuid();
        SnapshotId = snapshotId;
        Key = key;
        Kind = kind;
        Name = name;
        ParentNodeId = parentNodeId;
        ServerId = serverId;
        AttributesJson = attributesJson;
    }

    public Guid Id { get; private set; }
    public Guid SnapshotId { get; private set; }
    public string Key { get; private set; } = string.Empty;
    public GraphNodeKind Kind { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public Guid? ParentNodeId { get; private set; }
    public Guid? ServerId { get; private set; }
    public string AttributesJson { get; private set; } = "{}";
}
