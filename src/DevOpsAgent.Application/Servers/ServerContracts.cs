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
    DateTimeOffset? SudoPasswordRejectedAtUtc = null,
    DateTimeOffset? SudoPasswordOutcomeUnknownAtUtc = null,
    IReadOnlyList<string>? AllowedSshSources = null)
{
    /// <summary>The allowed SSH source addresses and ranges, normalised (T8.18); empty when none are configured.</summary>
    public IReadOnlyList<string> AllowedSshSources { get; init; } = AllowedSshSources ?? [];

    public ServerSudoStatus Sudo =>
        ServerSudoStatus.Describe(SudoCapable, HasSudoPassword, SudoPasswordRejectedAtUtc, SudoPasswordOutcomeUnknownAtUtc);
}

/// <summary>The outcome of saving a server's allowed SSH source list (T8.18).</summary>
/// <param name="Saved">The list was saved; <paramref name="Sources"/> holds it, normalised.</param>
/// <param name="Sources">The saved list; empty when nothing was saved.</param>
/// <param name="Errors">Per-line problems of the text (<c>line 3: "10.0.0.256" is not an IP address or CIDR range</c>); nothing was saved.</param>
/// <param name="ServerNotFound">No inventory server has that id.</param>
public sealed record SetAllowedSshSourcesResult(
    bool Saved, IReadOnlyList<string> Sources, IReadOnlyList<string> Errors, bool ServerNotFound = false)
{
    public static SetAllowedSshSourcesResult Success(IReadOnlyList<string> sources) => new(true, sources, []);
    public static SetAllowedSshSourcesResult Invalid(IReadOnlyList<string> errors) => new(false, [], errors);
    public static SetAllowedSshSourcesResult NotFound() => new(false, [], [], true);
}

/// <summary>How a server uses sudo, as the Servers page shows it (T7.R24).</summary>
/// <param name="Label">"sudo: NOPASSWD", "sudo: password", "sudo: none", with "not tested", "rejected" or "outcome unknown, paused"
/// where it applies.</param>
/// <param name="Detail">One sentence for the tooltip and screen readers.</param>
/// <param name="Paused">No password sudo is used: the stored password was rejected, or the outcome of an attempt is unknown.</param>
/// <param name="RetryAllowed">Retry now may try the password again: only after an unknown outcome, never after a rejection
/// (T7.R25, D-040 amendment 1).</param>
public sealed record ServerSudoStatus(string Label, string Detail, bool Paused, bool RetryAllowed = false)
{
    /// <summary>
    /// Why "Retry now" (and Test connection's sudo step) does nothing while the password is rejected (D-040 amendment). Shown
    /// next to the disabled button.
    /// </summary>
    public const string RetryBlockedReason =
        "Sudo is not retried: each wrong sudo password can count twice in pam_faillock (lockout risk). Edit or remove the sudo password first.";

    public static ServerSudoStatus Describe(bool? sudoCapable, bool hasSudoPassword, DateTimeOffset? sudoPasswordRejectedAtUtc,
        DateTimeOffset? sudoPasswordOutcomeUnknownAtUtc = null)
    {
        if (hasSudoPassword && sudoPasswordRejectedAtUtc is { } rejectedAt)
        {
            return new("sudo: password — rejected", $"The sudo password was rejected at {rejectedAt:yyyy-MM-dd HH:mm:ss} UTC. "
                + "No sudo is used on this server, and it is not retried, until the password is edited or removed.", true);
        }

        if (hasSudoPassword && sudoPasswordOutcomeUnknownAtUtc is { } unknownAt)
        {
            return new("sudo: password — outcome unknown, paused", $"A sudo command with the saved password ended at {unknownAt:yyyy-MM-dd HH:mm:ss} UTC "
                + "without confirming that sudo accepted the password (timeout, lost connection or cancellation). No password sudo is used "
                + "until you press Retry now (one attempt) or edit or remove the password.", true, RetryAllowed: true);
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
