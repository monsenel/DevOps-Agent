using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using DevOpsAgent.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DevOpsAgent.Infrastructure.Ssh;

public enum HostKeyDecision
{
    /// <summary>The key is one of the stored keys of the host.</summary>
    Trusted,
    /// <summary>No key is stored for the host: trusted on first use, recorded once the connection succeeds.</summary>
    FirstUse,
    /// <summary>Keys are stored for the host and the presented one is not among them: refused.</summary>
    Mismatch
}

/// <summary>
/// Host key trust (T8.4, D-041). The owner's known_hosts (<see cref="SshConnectionOptions.KnownHostsFile"/>, read-only) decides
/// alone for every host it lists. Other hosts are looked up in the app-managed file
/// (<see cref="SshConnectionOptions.ManagedKnownHostsFile"/>); a host found in neither is trusted on first use and recorded
/// there, with a <c>dva-first-seen=</c> comment, after its first successful connection. A refused key is remembered (in
/// memory) so the server page can show it and the owner can accept exactly that key.
/// </summary>
public sealed class ManagedKnownHosts(IOptions<SshConnectionOptions> options, TimeProvider timeProvider, ILogger<ManagedKnownHosts> logger)
    : IHostKeyStore
{
    private const string FirstSeenTag = "dva-first-seen=";
    private readonly SshConnectionOptions _options = options.Value;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly ConcurrentDictionary<string, HostKeyMismatch> _mismatches = new(StringComparer.OrdinalIgnoreCase);

    // The refused key itself (not only its fingerprint), so an accept stores exactly the key that was shown.
    private readonly ConcurrentDictionary<string, (string KeyType, byte[] Key)> _presented = new(StringComparer.OrdinalIgnoreCase);

    public HostKeyDecision Check(string host, int port, string keyType, byte[] key)
    {
        var manual = KnownHostsLines.For(KnownHostsLines.Read(_options.KnownHostsFile), host, port);
        var stored = manual.Count > 0 ? manual : KnownHostsLines.For(KnownHostsLines.Read(_options.ManagedKnownHostsFile), host, port);
        if (stored.Count == 0) return HostKeyDecision.FirstUse;
        if (stored.Any(entry => entry.KeyData.AsSpan().SequenceEqual(key)))
        {
            _mismatches.TryRemove(Name(host, port), out _);
            return HostKeyDecision.Trusted;
        }

        _presented[Name(host, port)] = (keyType, key.ToArray());
        _mismatches[Name(host, port)] = new HostKeyMismatch(host, port, stored.Select(entry => Info(entry, manual.Count > 0)).ToArray(),
            keyType, Fingerprint(key), timeProvider.GetUtcNow(), manual.Count > 0);
        return HostKeyDecision.Mismatch;
    }

    /// <summary>Records the key of a host after its first successful connection, unless a key was stored meanwhile.</summary>
    public async Task RecordFirstUseAsync(string host, int port, string keyType, byte[] key, CancellationToken cancellationToken)
    {
        await _write.WaitAsync(cancellationToken);
        try
        {
            if (KnownHostsLines.For(KnownHostsLines.Read(_options.KnownHostsFile), host, port).Count > 0) return;
            var lines = ReadManagedLines();
            if (KnownHostsLines.For(lines.Select(KnownHostsLines.Parse).OfType<KnownHostLine>().ToArray(), host, port).Count > 0) return;
            lines.Add(Line(host, port, keyType, key));
            WriteManaged(lines);
            logger.LogInformation("Recorded the SSH host key of {Host}:{Port} on first use ({Fingerprint})", host, port, Fingerprint(key));
        }
        finally
        {
            _write.Release();
        }
    }

    public IReadOnlyList<HostKeyInfo> GetKeys(string host, int port)
    {
        var manual = KnownHostsLines.For(KnownHostsLines.Read(_options.KnownHostsFile), host, port);
        return manual.Count > 0
            ? manual.Select(entry => Info(entry, true)).ToArray()
            : KnownHostsLines.For(KnownHostsLines.Read(_options.ManagedKnownHostsFile), host, port).Select(entry => Info(entry, false)).ToArray();
    }

    public HostKeyMismatch? GetMismatch(string host, int port) => _mismatches.GetValueOrDefault(Name(host, port));

    public async Task AcceptPresentedKeyAsync(string host, int port, string presentedFingerprint, CancellationToken cancellationToken)
    {
        if (!_mismatches.TryGetValue(Name(host, port), out var mismatch) || mismatch.PresentedFingerprint != presentedFingerprint)
            throw new InvalidOperationException("That key is no longer the one the server presented. Run Test connection and compare the fingerprint again.");
        if (mismatch.StoredIsManual)
            throw new InvalidOperationException("The stored key comes from your own known_hosts file; change it there.");
        if (!_presented.TryGetValue(Name(host, port), out var presented) || Fingerprint(presented.Key) != presentedFingerprint)
            throw new InvalidOperationException("The presented key is not available any more. Run Test connection and try again.");

        await _write.WaitAsync(cancellationToken);
        try
        {
            var lines = ReadManagedLines()
                .Where(line => KnownHostsLines.Parse(line) is not { } entry || !KnownHostsLines.Names(host, port).Any(name =>
                    entry.HostNames.Contains(name, StringComparer.OrdinalIgnoreCase)))
                .ToList();
            lines.Add(Line(host, port, presented.KeyType, presented.Key));
            WriteManaged(lines);
            _mismatches.TryRemove(Name(host, port), out _);
            logger.LogWarning("The owner accepted a new SSH host key for {Host}:{Port} ({Fingerprint}); it replaces the stored key",
                host, port, presentedFingerprint);
        }
        finally
        {
            _write.Release();
        }
    }

    public static string Fingerprint(byte[] key) => "SHA256:" + Convert.ToBase64String(SHA256.HashData(key)).TrimEnd('=');

    private static string Name(string host, int port) => KnownHostsLines.Names(host, port)[^1];

    private string Line(string host, int port, string keyType, byte[] key) =>
        $"{(port == 22 ? host : $"[{host}]:{port.ToString(CultureInfo.InvariantCulture)}")} {keyType} {Convert.ToBase64String(key)} "
        + $"{FirstSeenTag}{timeProvider.GetUtcNow().UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)}";

    private static HostKeyInfo Info(KnownHostLine entry, bool manual) => new(entry.KeyType,
        entry.KeyData.Length == 0 ? "unreadable entry" : Fingerprint(entry.KeyData),
        !manual && entry.Comment is { } comment && comment.StartsWith(FirstSeenTag, StringComparison.Ordinal)
            && DateTimeOffset.TryParse(comment[FirstSeenTag.Length..], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var seen)
            ? seen : null,
        manual);

    private List<string> ReadManagedLines() =>
        File.Exists(_options.ManagedKnownHostsFile) ? File.ReadAllLines(_options.ManagedKnownHostsFile).ToList() : [];

    // Written to a temporary file and moved over the old one, so a crash never leaves a half-written file.
    private void WriteManaged(IEnumerable<string> lines)
    {
        var path = _options.ManagedKnownHostsFile;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        File.WriteAllLines(temporary, lines);
        File.Move(temporary, path, overwrite: true);
    }
}

internal sealed record KnownHostLine(IReadOnlyList<string> HostNames, string KeyType, byte[] KeyData, string? Comment);

internal static class KnownHostsLines
{
    public static IReadOnlyList<KnownHostLine> Read(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? File.ReadLines(path).Select(Parse).OfType<KnownHostLine>().ToArray() : [];

    /// <summary>The names a host is listed under: plain host for port 22 (and "[host]:22"), "[host]:port" otherwise.</summary>
    public static string[] Names(string host, int port) => port == 22
        ? [host, $"[{host}]:{port.ToString(CultureInfo.InvariantCulture)}"]
        : [$"[{host}]:{port.ToString(CultureInfo.InvariantCulture)}"];

    public static IReadOnlyList<KnownHostLine> For(IReadOnlyList<KnownHostLine> entries, string host, int port)
    {
        var names = Names(host, port);
        return entries.Where(entry => Matches(entry, names)).ToArray();
    }

    // OpenSSH's rules for the host field: a comma list of names, `*` and `?` patterns, `!` negations and hashed names
    // (|1|salt|hash, what HashKnownHosts writes). A host the owner's file pins this way must be found there: if it were not,
    // it would be trusted on first use and the pin ignored.
    private static bool Matches(KnownHostLine entry, string[] names)
    {
        var matched = false;
        foreach (var pattern in entry.HostNames)
        {
            var negated = pattern.StartsWith('!');
            if (!PatternMatches(negated ? pattern[1..] : pattern, names)) continue;
            if (negated) return false;
            matched = true;
        }

        return matched;
    }

    private static bool PatternMatches(string pattern, string[] names)
    {
        if (pattern.StartsWith("|1|", StringComparison.Ordinal)) return HashedMatches(pattern, names);
        if (pattern.IndexOfAny(['*', '?']) >= 0)
        {
            var glob = "^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal) + "$";
            return names.Any(name => Regex.IsMatch(name, glob, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)));
        }

        return names.Contains(pattern, StringComparer.OrdinalIgnoreCase);
    }

    // |1|base64(salt)|base64(HMAC-SHA1(salt, name)); OpenSSH hashes the name in lower case.
    private static bool HashedMatches(string pattern, string[] names)
    {
        var parts = pattern.Split('|');
        if (parts.Length != 4 || parts[1] != "1") return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            return names.Any(name => CryptographicOperations.FixedTimeEquals(
                HMACSHA1.HashData(salt, System.Text.Encoding.UTF8.GetBytes(name.ToLowerInvariant())), expected));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    // @markers (@revoked, @cert-authority) are not used by the app and are skipped, as before.
    public static KnownHostLine? Parse(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith('@')) return null;
        var fields = trimmed.Split((char[]?)null, 4, StringSplitOptions.RemoveEmptyEntries);
        var names = fields[0].Split(',', StringSplitOptions.RemoveEmptyEntries);
        // A line that names a host but whose key cannot be read (truncated, damaged base64) still pins it: with no key to
        // match, the host is refused. Skipping the line would make the host unknown, trusted on first use and re-recorded.
        if (fields.Length < 3) return new KnownHostLine(names, fields.Length > 1 ? fields[1] : string.Empty, [], null);
        try
        {
            return new KnownHostLine(names, fields[1], Convert.FromBase64String(fields[2]), fields.Length > 3 ? fields[3].Trim() : null);
        }
        catch (FormatException)
        {
            return new KnownHostLine(names, fields[1], [], null);
        }
    }
}
