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
    private static readonly TimeSpan AcceptTokenLifetime = TimeSpan.FromMinutes(5);
    private readonly SshConnectionOptions _options = options.Value;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly ConcurrentDictionary<string, HostKeyMismatch> _mismatches = new(StringComparer.OrdinalIgnoreCase);

    // The refused key itself (not only its fingerprint), so an accept stores exactly the key that was shown.
    private readonly ConcurrentDictionary<string, (string KeyType, byte[] Key)> _presented = new(StringComparer.OrdinalIgnoreCase);

    // One-time confirmation tokens issued when the Accept new key dialog opens (T7.R29 4).
    private readonly ConcurrentDictionary<string, AcceptTokenEntry> _acceptTokens = new(StringComparer.OrdinalIgnoreCase);

    private sealed record AcceptTokenEntry(string Token, string PresentedFingerprint, DateTimeOffset ExpiresAtUtc);

    public HostKeyDecision Check(string host, int port, string keyType, byte[] key)
    {
        var manualAvailable = TryReadOwnerKnownHosts(out var allManual);
        var manualForHost = KnownHostsLines.For(allManual, host, port);

        // T7.R29 (1): honour @revoked lines in the owner's known_hosts file; a revoked key is always refused and never trusted on first use.
        var revoked = manualForHost.Where(entry => entry.Marker == KnownHostMarker.Revoked).ToArray();
        if (revoked.Any(entry => entry.KeyData.Length == 0 || entry.KeyData.AsSpan().SequenceEqual(key)))
        {
            _presented[Name(host, port)] = (keyType, key.ToArray());
            _mismatches[Name(host, port)] = new HostKeyMismatch(host, port,
                manualForHost.Where(entry => entry.Marker == KnownHostMarker.None).Select(entry => Info(entry, true)).ToArray(),
                keyType, Fingerprint(key), timeProvider.GetUtcNow(), StoredIsManual: true, Revoked: true);
            _acceptTokens.TryRemove(Name(host, port), out _);
            return HostKeyDecision.Mismatch;
        }

        var manualPinned = manualForHost.Where(entry => entry.Marker == KnownHostMarker.None).ToArray();
        var managedPinned = manualPinned.Length > 0
            ? []
            : KnownHostsLines.For(KnownHostsLines.Read(_options.ManagedKnownHostsFile), host, port)
                .Where(entry => entry.Marker == KnownHostMarker.None)
                .ToArray();
        var stored = manualPinned.Length > 0 ? manualPinned : managedPinned;

        if (stored.Length == 0)
        {
            // T7.R29 (2): when the owner's Ssh:KnownHostsFile is configured but missing or unreadable (e.g. missing mount),
            // refuse trust-on-first-use so pinned hosts cannot silently become TOFU.
            if (!manualAvailable)
            {
                _presented[Name(host, port)] = (keyType, key.ToArray());
                _mismatches[Name(host, port)] = new HostKeyMismatch(host, port, [],
                    keyType, Fingerprint(key), timeProvider.GetUtcNow(), StoredIsManual: true);
                _acceptTokens.TryRemove(Name(host, port), out _);
                return HostKeyDecision.Mismatch;
            }

            return HostKeyDecision.FirstUse;
        }

        if (stored.Any(entry => entry.KeyData.AsSpan().SequenceEqual(key)))
        {
            _mismatches.TryRemove(Name(host, port), out _);
            _acceptTokens.TryRemove(Name(host, port), out _);
            return HostKeyDecision.Trusted;
        }

        _presented[Name(host, port)] = (keyType, key.ToArray());
        _mismatches[Name(host, port)] = new HostKeyMismatch(host, port, stored.Select(entry => Info(entry, manualPinned.Length > 0)).ToArray(),
            keyType, Fingerprint(key), timeProvider.GetUtcNow(), manualPinned.Length > 0);
        _acceptTokens.TryRemove(Name(host, port), out _);
        return HostKeyDecision.Mismatch;
    }

    /// <summary>Records the key of a host after its first successful connection, unless a key was stored meanwhile.</summary>
    public async Task RecordFirstUseAsync(string host, int port, string keyType, byte[] key, CancellationToken cancellationToken)
    {
        await _write.WaitAsync(cancellationToken);
        try
        {
            // T7.R29 (2): never record first-use keys when the configured owner known_hosts file is missing or unreadable.
            if (!TryReadOwnerKnownHosts(out var allManual)) return;
            var manualForHost = KnownHostsLines.For(allManual, host, port);
            if (manualForHost.Any(entry => entry.Marker == KnownHostMarker.Revoked
                    && (entry.KeyData.Length == 0 || entry.KeyData.AsSpan().SequenceEqual(key))))
                return;
            if (manualForHost.Any(entry => entry.Marker == KnownHostMarker.None)) return;
            var lines = ReadManagedLines();
            if (KnownHostsLines.For(lines.Select(KnownHostsLines.Parse).OfType<KnownHostLine>().ToArray(), host, port)
                .Any(entry => entry.Marker == KnownHostMarker.None)) return;
            lines.Add(Line(host, port, keyType, key));
            try
            {
                WriteManaged(lines);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The login has already succeeded: a file the app cannot write must not turn it into a failed login (it was
                // reported as "key file cannot be read"). The host stays unpinned and is trusted on first use again next time.
                logger.LogError("The SSH host key of {Host}:{Port} could not be recorded in {File} ({ErrorType}); it is not pinned. Set Ssh:ManagedKnownHostsFile to a writable path",
                    host, port, _options.ManagedKnownHostsFile, exception.GetType().Name);
                return;
            }

            logger.LogInformation("Recorded the SSH host key of {Host}:{Port} on first use ({Fingerprint})", host, port, Fingerprint(key));
        }
        finally
        {
            _write.Release();
        }
    }

    public IReadOnlyList<HostKeyInfo> GetKeys(string host, int port)
    {
        var manual = KnownHostsLines.For(KnownHostsLines.Read(_options.KnownHostsFile), host, port)
            .Where(entry => entry.Marker == KnownHostMarker.None)
            .ToArray();
        return manual.Length > 0
            ? manual.Select(entry => Info(entry, true)).ToArray()
            : KnownHostsLines.For(KnownHostsLines.Read(_options.ManagedKnownHostsFile), host, port)
                .Where(entry => entry.Marker == KnownHostMarker.None)
                .Select(entry => Info(entry, false))
                .ToArray();
    }

    public HostKeyMismatch? GetMismatch(string host, int port) => _mismatches.GetValueOrDefault(Name(host, port));

    public string IssueAcceptToken(string host, int port, string presentedFingerprint)
    {
        var keyName = Name(host, port);
        if (!_mismatches.TryGetValue(keyName, out var mismatch) || mismatch.PresentedFingerprint != presentedFingerprint)
            throw new InvalidOperationException("That key is no longer the one the server presented. Run Test connection and compare the fingerprint again.");
        if (mismatch.Revoked)
            throw new InvalidOperationException("The presented host key is marked @revoked in your known_hosts file and cannot be accepted.");
        if (mismatch.StoredIsManual)
            throw new InvalidOperationException("The stored key comes from your own known_hosts file; change it there.");

        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        _acceptTokens[keyName] = new AcceptTokenEntry(token, presentedFingerprint, timeProvider.GetUtcNow() + AcceptTokenLifetime);
        return token;
    }

    public async Task AcceptPresentedKeyAsync(string host, int port, string presentedFingerprint, string confirmationToken, CancellationToken cancellationToken)
    {
        var keyName = Name(host, port);
        if (!_mismatches.TryGetValue(keyName, out var mismatch) || mismatch.PresentedFingerprint != presentedFingerprint)
            throw new InvalidOperationException("That key is no longer the one the server presented. Run Test connection and compare the fingerprint again.");
        if (mismatch.Revoked)
            throw new InvalidOperationException("The presented host key is marked @revoked in your known_hosts file and cannot be accepted.");
        if (mismatch.StoredIsManual)
            throw new InvalidOperationException("The stored key comes from your own known_hosts file; change it there.");
        if (!_presented.TryGetValue(keyName, out var presented) || Fingerprint(presented.Key) != presentedFingerprint)
            throw new InvalidOperationException("The presented key is not available any more. Run Test connection and try again.");

        // T7.R29 (4): require and atomically consume the one-time confirmation token issued by the dialog.
        if (string.IsNullOrWhiteSpace(confirmationToken)
            || !_acceptTokens.TryGetValue(keyName, out var issued)
            || !CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(issued.Token),
                System.Text.Encoding.UTF8.GetBytes(confirmationToken))
            || issued.PresentedFingerprint != presentedFingerprint
            || timeProvider.GetUtcNow() > issued.ExpiresAtUtc
            || !_acceptTokens.TryRemove(KeyValuePair.Create(keyName, issued)))
        {
            throw new InvalidOperationException("The confirmation token is missing, invalid, expired, or already used. Re-open Accept new key and confirm the fingerprint again.");
        }

        await _write.WaitAsync(cancellationToken);
        try
        {
            // T7.R29 (7): keep one managed entry per (host:port, keyType) so dual-key-type hosts do not flip-flop.
            // Remove existing managed lines for this host only when they have the same key type or are unreadable/damaged.
            var hostNames = KnownHostsLines.Names(host, port);
            var lines = ReadManagedLines()
                .Where(line =>
                {
                    if (KnownHostsLines.Parse(line) is not { } entry) return true;
                    if (!hostNames.Any(name => entry.HostNames.Contains(name, StringComparer.OrdinalIgnoreCase))) return true;
                    if (entry.KeyData.Length == 0) return false; // replace damaged lines for this host
                    return !string.Equals(entry.KeyType, presented.KeyType, StringComparison.OrdinalIgnoreCase);
                })
                .ToList();
            lines.Add(Line(host, port, presented.KeyType, presented.Key));
            WriteManaged(lines);
            _mismatches.TryRemove(keyName, out _);
            logger.LogWarning("The owner accepted a new SSH host key for {Host}:{Port} ({KeyType} {Fingerprint}); it replaces any previous managed {KeyType} key",
                host, port, presented.KeyType, presentedFingerprint, presented.KeyType);
        }
        finally
        {
            _write.Release();
        }
    }

    private bool TryReadOwnerKnownHosts(out IReadOnlyList<KnownHostLine> entries)
    {
        if (string.IsNullOrWhiteSpace(_options.KnownHostsFile))
        {
            entries = [];
            return true;
        }

        if (KnownHostsLines.TryRead(_options.KnownHostsFile, out entries, out var error))
        {
            return true;
        }

        logger.LogError("The configured owner known_hosts file {File} is missing or unreadable ({ErrorType}); first-use SSH host-key trust is refused",
            _options.KnownHostsFile, error?.GetType().Name ?? "FileNotFoundException");
        entries = [];
        return false;
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

    // Written to a temporary file, flushed to disk (fsync, T7.R29 3), and moved over the old one, so a power loss never leaves an empty file.
    private void WriteManaged(IEnumerable<string> lines)
    {
        var path = _options.ManagedKnownHostsFile;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            foreach (var line in lines) writer.WriteLine(line);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
    }
}

internal enum KnownHostMarker
{
    None = 0,
    Revoked = 1,
    CertAuthority = 2
}

internal sealed record KnownHostLine(IReadOnlyList<string> HostNames, string KeyType, byte[] KeyData, string? Comment, KnownHostMarker Marker = KnownHostMarker.None);

internal static class KnownHostsLines
{
    public static IReadOnlyList<KnownHostLine> Read(string? path) =>
        TryRead(path, out var entries, out _) ? entries : [];

    public static bool TryRead(string? path, out IReadOnlyList<KnownHostLine> entries, out Exception? error)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            entries = [];
            error = null;
            return true;
        }

        try
        {
            entries = File.ReadLines(path).Select(Parse).OfType<KnownHostLine>().ToArray();
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            entries = [];
            error = exception;
            return false;
        }
    }

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

    // Parses standard entries as well as OpenSSH @revoked and @cert-authority markers (T7.R29 1).
    public static KnownHostLine? Parse(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#')) return null;

        var marker = KnownHostMarker.None;
        var offset = 0;
        string[] fields;
        if (trimmed.StartsWith('@'))
        {
            fields = trimmed.Split((char[]?)null, 5, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2) return null;
            if (string.Equals(fields[0], "@revoked", StringComparison.OrdinalIgnoreCase)) marker = KnownHostMarker.Revoked;
            else if (string.Equals(fields[0], "@cert-authority", StringComparison.OrdinalIgnoreCase)) marker = KnownHostMarker.CertAuthority;
            else return null;
            offset = 1;
        }
        else
        {
            fields = trimmed.Split((char[]?)null, 4, StringSplitOptions.RemoveEmptyEntries);
        }

        var names = fields[offset].Split(',', StringSplitOptions.RemoveEmptyEntries);
        // A line that names a host but whose key cannot be read (truncated, damaged base64) still pins it: with no key to
        // match, the host is refused. Skipping the line would make the host unknown, trusted on first use and re-recorded.
        if (fields.Length - offset < 3)
            return new KnownHostLine(names, fields.Length - offset > 1 ? fields[offset + 1] : string.Empty, [], null, marker);
        try
        {
            return new KnownHostLine(names, fields[offset + 1], Convert.FromBase64String(fields[offset + 2]),
                fields.Length - offset > 3 ? fields[offset + 3].Trim() : null, marker);
        }
        catch (FormatException)
        {
            return new KnownHostLine(names, fields[offset + 1], [], null, marker);
        }
    }
}
