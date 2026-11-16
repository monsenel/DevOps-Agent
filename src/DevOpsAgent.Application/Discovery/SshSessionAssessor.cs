using DevOpsAgent.Application.Servers;

namespace DevOpsAgent.Application.Discovery;

/// <summary>
/// Judges the SSH sessions of one server against its allowed-sources list and its jump host (T8.18): the glue between
/// <see cref="SshSessionPolicy"/> and <see cref="SshSourceList"/>. Pure: no I/O, no logging.
/// </summary>
public static class SshSessionAssessor
{
    /// <param name="sessions">The inbound SSH sessions recorded at the last discovery.</param>
    /// <param name="allowedSources">The server's allowed sources (addresses and CIDR ranges); empty when none is configured.</param>
    /// <param name="jumpHostAddresses">The addresses of the server's jump host. A session from one of them is allowed without a
    /// list entry (exact address, not a range).</param>
    public static SshSessionStatus Assess(
        IReadOnlyList<SshSessionInfo> sessions,
        IReadOnlyList<string> allowedSources,
        IReadOnlyCollection<string> jumpHostAddresses)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(allowedSources);
        ArgumentNullException.ThrowIfNull(jumpHostAddresses);

        var jumpHosts = jumpHostAddresses.Select(Normalize).Where(address => address.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        return SshSessionPolicy.Evaluate(
            sessions,
            allowListConfigured: allowedSources.Count > 0,
            isAllowed: address => SshSourceList.Matches(allowedSources, address),
            isImplicitlyAllowed: address => jumpHosts.Contains(Normalize(address)));
    }

    /// <summary>
    /// The address in its normal form for an exact comparison: trimmed, without IPv6 brackets or scope id, an IPv4-mapped IPv6
    /// address as its IPv4 address, IPv6 compressed and lower-case. Text that is not an address is only trimmed and lower-cased.
    /// </summary>
    internal static string Normalize(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return string.Empty;
        var text = address.Trim();
        if (text.Length > 2 && text[0] == '[' && text[^1] == ']') text = text[1..^1];
        if (text.IndexOf('%') is var scope and >= 0) text = text[..scope];
        return !text.Contains('/') && SshSourceList.TryNormalize(text, out var normalized)
            ? normalized
            : text.ToLowerInvariant();
    }
}
