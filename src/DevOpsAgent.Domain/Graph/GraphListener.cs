namespace DevOpsAgent.Domain.Graph;

public sealed class GraphListener
{
    private GraphListener()
    {
    }

    public GraphListener(
        Guid snapshotId,
        Guid nodeId,
        string address,
        int port,
        string protocol,
        string? processName,
        int? processId)
    {
        Id = Guid.NewGuid();
        SnapshotId = snapshotId;
        NodeId = nodeId;
        Address = address;
        Port = port;
        Protocol = protocol;
        ProcessName = processName;
        ProcessId = processId;
    }

    public Guid Id { get; private set; }
    public Guid SnapshotId { get; private set; }
    public Guid NodeId { get; private set; }
    public string Address { get; private set; } = string.Empty;
    public int Port { get; private set; }
    public string Protocol { get; private set; } = string.Empty;
    public string? ProcessName { get; private set; }
    public int? ProcessId { get; private set; }
}
