namespace DevOpsAgent.Application.Servers;

/// <param name="SudoPassword">A new sudo password (T7.R24); null or empty keeps the stored one. Never read back.</param>
/// <param name="RemoveSudoPassword">Remove the stored sudo password; sudo goes back to <c>sudo -n</c>.</param>
public sealed record ServerInput(
    string Name,
    string Host,
    int Port,
    string User,
    string KeyFile,
    string? KeyPassphrase,
    Guid? JumpServerId,
    string Group,
    IReadOnlyList<string> Tags,
    string? SudoPassword = null,
    bool RemoveSudoPassword = false)
{
    // A record's generated ToString would print the password.
    public override string ToString() => $"ServerInput {{ Name = {Name}, Host = {Host}, Port = {Port} }}";
}

public sealed record ServerSummary(
    Guid Id,
    string Name,
    string Host,
    int Port,
    string User,
    string KeyFile,
    bool HasKeyPassphrase,
    Guid? JumpServerId,
    string? JumpServerName,
    string Group,
    IReadOnlyList<string> Tags,
    bool? SudoCapable,
    DateTimeOffset? LastSeenAtUtc,
    string? OsInfo,
    bool HasSudoPassword = false,
    DateTimeOffset? SudoPasswordRejectedAtUtc = null)
{
    public ServerSudoStatus Sudo => ServerSudoStatus.Describe(SudoCapable, HasSudoPassword, SudoPasswordRejectedAtUtc);
}

/// <summary>How a server uses sudo, as the Servers page shows it (T7.R24).</summary>
/// <param name="Label">"sudo: NOPASSWD", "sudo: password", "sudo: none", with "not tested" or "rejected" where it applies.</param>
/// <param name="Detail">One sentence for the tooltip and screen readers.</param>
/// <param name="Paused">The stored password was rejected and no sudo is used until it is edited or "Retry now" is pressed.</param>
public sealed record ServerSudoStatus(string Label, string Detail, bool Paused)
{
    public static ServerSudoStatus Describe(bool? sudoCapable, bool hasSudoPassword, DateTimeOffset? sudoPasswordRejectedAtUtc)
    {
        if (hasSudoPassword && sudoPasswordRejectedAtUtc is { } rejectedAt)
        {
            return new("sudo: password — rejected", $"The sudo password was rejected at {rejectedAt:yyyy-MM-dd HH:mm:ss} UTC. "
                + "No sudo is used on this server until the password is edited or Retry now is pressed.", true);
        }

        return (hasSudoPassword, sudoCapable) switch
        {
            (true, true) => new("sudo: password", "sudo runs with the saved password (sudo -S, password on stdin).", false),
            (true, null) => new("sudo: password · not tested", "A sudo password is saved. Run Test connection to check it.", false),
            (true, false) => new("sudo: none", "A sudo password is saved, but the last test could not use sudo. Run Test connection.", false),
            (false, true) => new("sudo: NOPASSWD", "Passwordless sudo (sudo -n).", false),
            (false, false) => new("sudo: none", "sudo is not available without a password.", false),
            _ => new("sudo: not tested", "Run Test connection to check sudo.", false)
        };
    }
}
