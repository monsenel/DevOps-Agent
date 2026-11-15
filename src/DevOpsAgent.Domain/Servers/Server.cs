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

    /// <summary>
    /// The sudo password for hosts where NOPASSWD sudo is not possible, protected with Data Protection. When set, sudo runs as
    /// <c>sudo -S -p ''</c> with the password on stdin; when null, as <c>sudo -n</c>.
    /// </summary>
    public string? ProtectedSudoPassword { get; private set; }

    /// <summary>
    /// When the stored sudo password was rejected. While set no sudo command is sent to the server (pam_faillock), until the
    /// password is edited or the owner presses "Retry now" / "Test connection".
    /// </summary>
    public DateTimeOffset? SudoPasswordRejectedAtUtc { get; private set; }

    public DateTimeOffset? LastSeenAtUtc { get; private set; }
    public string? OsInfo { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }

    public bool HasSudoPassword => !string.IsNullOrEmpty(ProtectedSudoPassword);

    /// <summary>Whether the app may use sudo on this server: it passed the sudo probe and its sudo password is not rejected.</summary>
    public bool CanUseSudo => SudoCapable == true && SudoPasswordRejectedAtUtc is null;

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

    /// <summary>Replaces the stored sudo password (a new value also ends a rejection: it has not been tried yet).</summary>
    public void SetSudoPassword(string protectedSudoPassword)
    {
        ArgumentException.ThrowIfNullOrEmpty(protectedSudoPassword);
        ProtectedSudoPassword = protectedSudoPassword;
        SudoPasswordRejectedAtUtc = null;
    }

    /// <summary>Removes the stored sudo password; sudo goes back to <c>sudo -n</c>.</summary>
    public void ClearSudoPassword()
    {
        ProtectedSudoPassword = null;
        SudoPasswordRejectedAtUtc = null;
    }

    public void RecordSudoPasswordRejected(DateTimeOffset rejectedAtUtc) => SudoPasswordRejectedAtUtc ??= rejectedAtUtc;

    public void ClearSudoPasswordRejection() => SudoPasswordRejectedAtUtc = null;

    public void RecordCapabilities(bool sudoCapable, string? osInfo, DateTimeOffset lastSeenAtUtc)
    {
        SudoCapable = sudoCapable;
        OsInfo = osInfo;
        LastSeenAtUtc = lastSeenAtUtc;
    }
}
