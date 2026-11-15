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
    /// When the stored sudo password was rejected. While set no sudo command is sent to the server (pam_faillock), not even by
    /// "Retry now" / "Test connection", until the password is edited or removed (D-040).
    /// </summary>
    public DateTimeOffset? SudoPasswordRejectedAtUtc { get; private set; }

    /// <summary>
    /// When a password sudo ended without its confirmation marker and without sudo's rejection (timeout, lost connection,
    /// cancellation, shutdown, an unknown message): whether the password was tried is unknown, so no password sudo is used on
    /// the server until the owner presses Retry now / Test connection or edits or removes the password (T7.R25).
    /// </summary>
    public DateTimeOffset? SudoPasswordOutcomeUnknownAtUtc { get; private set; }

    /// <summary>
    /// Written before the password is sent and cleared when the outcome is known (T7.R25). Still set when no attempt is running
    /// (the app stopped or crashed mid-attempt) means the outcome is unknown.
    /// </summary>
    public DateTimeOffset? SudoPasswordAttemptStartedAtUtc { get; private set; }

    /// <summary>
    /// Since when the server's "sudo-auth-failed" alert is open: set by a rejection or an unknown outcome, kept when the
    /// password is edited (an edit is not proof that sudo works), cleared by the next confirmed password sudo or by removing the
    /// password (D-040).
    /// </summary>
    public DateTimeOffset? SudoAuthFailedSinceUtc { get; private set; }

    public DateTimeOffset? LastSeenAtUtc { get; private set; }
    public string? OsInfo { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }

    public bool HasSudoPassword => !string.IsNullOrEmpty(ProtectedSudoPassword);

    /// <summary>The stored sudo password is not used: it was rejected, or the outcome of an attempt is unknown.</summary>
    public bool SudoPasswordPaused => HasSudoPassword && (SudoPasswordRejectedAtUtc is not null || SudoPasswordOutcomeUnknownAtUtc is not null);

    /// <summary>Whether the app may use sudo on this server: it passed the sudo probe and its sudo password is not paused.</summary>
    public bool CanUseSudo => SudoCapable == true && SudoPasswordRejectedAtUtc is null && SudoPasswordOutcomeUnknownAtUtc is null;

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
        SudoPasswordOutcomeUnknownAtUtc = null;
        SudoPasswordAttemptStartedAtUtc = null;
    }

    /// <summary>Removes the stored sudo password; sudo goes back to <c>sudo -n</c> and the sudo-auth-failed alert resolves.</summary>
    public void ClearSudoPassword()
    {
        ProtectedSudoPassword = null;
        SudoPasswordRejectedAtUtc = null;
        SudoPasswordOutcomeUnknownAtUtc = null;
        SudoPasswordAttemptStartedAtUtc = null;
        SudoAuthFailedSinceUtc = null;
    }

    public void RecordSudoPasswordRejected(DateTimeOffset rejectedAtUtc)
    {
        SudoPasswordAttemptStartedAtUtc = null;
        SudoPasswordRejectedAtUtc ??= rejectedAtUtc;
        SudoAuthFailedSinceUtc ??= rejectedAtUtc;
    }

    /// <summary>The password is about to be sent (T7.R25).</summary>
    public void RecordSudoPasswordAttemptStarted(DateTimeOffset startedAtUtc) => SudoPasswordAttemptStartedAtUtc = startedAtUtc;

    /// <summary>An attempt ended without the confirmation marker and without a rejection: paused until Retry now or an edit.</summary>
    public void RecordSudoPasswordOutcomeUnknown(DateTimeOffset atUtc)
    {
        SudoPasswordAttemptStartedAtUtc = null;
        SudoPasswordOutcomeUnknownAtUtc ??= atUtc;
        SudoAuthFailedSinceUtc ??= atUtc;
    }

    /// <summary>The owner pressed Retry now / Test connection: the next probe may try the password once (a rejection stays).</summary>
    public void ClearSudoPasswordOutcomeUnknown() => SudoPasswordOutcomeUnknownAtUtc = null;

    /// <summary>The confirmation marker came back: the password works, and the sudo-auth-failed alert resolves.</summary>
    public void RecordSudoPasswordSucceeded()
    {
        SudoPasswordAttemptStartedAtUtc = null;
        SudoAuthFailedSinceUtc = null;
    }

    /// <param name="sudoCapable">The sudo probe's result; null when the probe was not run (a rejected sudo password), which
    /// keeps the previous value.</param>
    public void RecordCapabilities(bool? sudoCapable, string? osInfo, DateTimeOffset lastSeenAtUtc)
    {
        if (sudoCapable is { } capable) SudoCapable = capable;
        OsInfo = osInfo;
        LastSeenAtUtc = lastSeenAtUtc;
    }
}
