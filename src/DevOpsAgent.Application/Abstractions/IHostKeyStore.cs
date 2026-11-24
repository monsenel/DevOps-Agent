namespace DevOpsAgent.Application.Abstractions;

/// <param name="Fingerprint">OpenSSH style, <c>SHA256:…</c>.</param>
/// <param name="FirstSeenUtc">When the app recorded it on first use; null for keys added by hand.</param>
/// <param name="Manual">From the owner's own known_hosts file (read-only to the app), not recorded by the app.</param>
public sealed record HostKeyInfo(string KeyType, string Fingerprint, DateTimeOffset? FirstSeenUtc, bool Manual);

/// <summary>A server presented a key that does not match the stored one; the connection was refused.</summary>
/// <param name="StoredIsManual">The stored key comes from the owner's known_hosts file, which the app does not change.</param>
/// <param name="Revoked">The presented key matched a <c>@revoked</c> entry in the owner's known_hosts file (T7.R29).</param>
public sealed record HostKeyMismatch(string Host, int Port, IReadOnlyList<HostKeyInfo> Stored, string PresentedKeyType,
    string PresentedFingerprint, DateTimeOffset SeenAtUtc, bool StoredIsManual, bool Revoked = false);

/// <summary>
/// SSH host keys (T8.4, D-041): the owner's known_hosts file is honoured as before; a host it does not list is trusted on its
/// first successful connection and its key recorded in the app-managed known_hosts file. A changed key is refused until the
/// owner accepts the presented key on the server page.
/// </summary>
public interface IHostKeyStore
{
    IReadOnlyList<HostKeyInfo> GetKeys(string host, int port);

    /// <summary>The last refused key of this host, while it is still refused.</summary>
    HostKeyMismatch? GetMismatch(string host, int port);

    /// <summary>
    /// Issues a one-time confirmation token for the currently refused key of <paramref name="host"/>:<paramref name="port"/>
    /// (T7.R29). The token must be supplied to <see cref="AcceptPresentedKeyAsync"/> and is consumed on first use.
    /// </summary>
    string IssueAcceptToken(string host, int port, string presentedFingerprint);

    /// <summary>
    /// Replaces the app-managed key of the same key type for the host with the refused key whose fingerprint and one-time
    /// confirmation token the owner confirmed. Fails when that key is no longer the refused one, the token is invalid or
    /// already used, or when the stored key is the owner's own (manual) entry or revoked.
    /// </summary>
    Task AcceptPresentedKeyAsync(string host, int port, string presentedFingerprint, string confirmationToken, CancellationToken cancellationToken);
}
