namespace DevOpsAgent.Domain.Servers;

public sealed class Server
{
    private Server()
    {
    }

    public Server(string name, string host, int port, string user, string keyFile, string group)
    {
        Id = Guid.NewGuid();
        Name = name;
        Host = host;
        Port = port;
        User = user;
        KeyFile = keyFile;
        Group = group;
        TagsJson = "[]";
        CreatedAtUtc = DateTimeOffset.UtcNow;
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Host { get; private set; } = string.Empty;
    public int Port { get; private set; }
    public string User { get; private set; } = string.Empty;
    public string KeyFile { get; private set; } = string.Empty;
    public string? ProtectedKeyPassphrase { get; private set; }
    public Guid? JumpServerId { get; private set; }
    public string Group { get; private set; } = string.Empty;
    public string TagsJson { get; private set; } = "[]";
    public bool? SudoCapable { get; private set; }
    public DateTimeOffset? LastSeenAtUtc { get; private set; }
    public string? OsInfo { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }

    public void Update(
        string name,
        string host,
        int port,
        string user,
        string keyFile,
        Guid? jumpServerId,
        string group,
        string tagsJson,
        string? protectedKeyPassphrase)
    {
        Name = name;
        Host = host;
        Port = port;
        User = user;
        KeyFile = keyFile;
        JumpServerId = jumpServerId;
        Group = group;
        TagsJson = tagsJson;
        if (protectedKeyPassphrase is not null)
        {
            ProtectedKeyPassphrase = protectedKeyPassphrase;
        }
    }

    public void RecordCapabilities(bool sudoCapable, string? osInfo, DateTimeOffset lastSeenAtUtc)
    {
        SudoCapable = sudoCapable;
        OsInfo = osInfo;
        LastSeenAtUtc = lastSeenAtUtc;
    }
}
