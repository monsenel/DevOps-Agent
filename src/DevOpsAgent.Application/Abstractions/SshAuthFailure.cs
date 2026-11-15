namespace DevOpsAgent.Application.Abstractions;

/// <summary>
/// Why an SSH login could not be completed although the server may be reachable (T7.R19): the server refused the key, or the
/// key itself could not be used. None of these is a connection-level failure, so none of them is "unreachable".
/// </summary>
public enum SshAuthFailure
{
    /// <summary>The server answered and refused the offered key (SSH.NET <c>SshAuthenticationException</c>).</summary>
    ServerRejectedKey = 0,

    /// <summary>The identity file named in the inventory does not exist.</summary>
    KeyFileNotFound = 1,

    /// <summary>The identity file exists but cannot be read (permissions, a directory).</summary>
    KeyFileUnreadable = 2,

    /// <summary>The identity file is not a usable private key, or the stored passphrase does not open it.</summary>
    KeyInvalidOrWrongPassphrase = 3,

    /// <summary>The key is passphrase protected and the server has no passphrase stored.</summary>
    PassphraseRequired = 4
}

/// <summary>
/// An authentication or key error. Its message is fixed text per <see cref="SshAuthFailure"/>: it never carries key material,
/// a passphrase, a key file name or path, a user name or whatever message the underlying library produced, so it is safe to
/// show in the UI, store in an alert and write to a log. The original exception is deliberately not kept.
/// </summary>
public sealed class SshAuthFailedException(SshAuthFailure reason) : Exception(Describe(reason))
{
    public SshAuthFailure Reason { get; } = reason;

    /// <summary>The safe wording of a reason, as it appears in "authentication failed (<c>reason</c>)".</summary>
    public static string Describe(SshAuthFailure reason) => reason switch
    {
        SshAuthFailure.ServerRejectedKey => "the server rejected the SSH key",
        SshAuthFailure.KeyFileNotFound => "key file not found",
        SshAuthFailure.KeyFileUnreadable => "key file cannot be read",
        SshAuthFailure.KeyInvalidOrWrongPassphrase => "wrong passphrase or invalid key file",
        SshAuthFailure.PassphraseRequired => "the key needs a passphrase and none is stored",
        _ => "authentication failed"
    };
}
