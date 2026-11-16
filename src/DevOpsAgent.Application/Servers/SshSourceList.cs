using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace DevOpsAgent.Application.Servers;

/// <summary>The outcome of <see cref="SshSourceList.Parse"/>: the normalised entries, or the per-line problems (never both).</summary>
public sealed record SshSourceParseResult(IReadOnlyList<string> Entries, IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// A server's allowed SSH source addresses (T8.18): IPv4/IPv6 addresses and CIDR ranges, one per line in the editor, stored
/// normalised. A pure helper: no I/O, no logging.
/// </summary>
public static class SshSourceList
{
    public const int MaxEntries = 100;
    public const int MaxEntryLength = 64;

    /// <summary>The longest text <see cref="Parse"/> accepts (100 entries plus comments fit well inside it).</summary>
    public const int MaxTextLength = 16 * 1024;

    private static readonly char[] Separators = [',', ';', ' ', '\t', '\v', '\f'];

    /// <summary>
    /// Parses the editor text. Entries are separated by line breaks, commas, semicolons or whitespace; <c>#</c> starts a comment
    /// that runs to the end of the line; blank lines are ignored. Each entry is an IPv4 or IPv6 address or a CIDR range and is
    /// returned normalised (see <see cref="TryNormalize"/>); duplicates are removed, the first occurrence keeps its place.
    /// Entries are only returned when every line is valid.
    /// </summary>
    public static SshSourceParseResult Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new SshSourceParseResult([], []);
        if (text.Length > MaxTextLength)
            return new SshSourceParseResult([], [$"The list is too long: at most {MaxTextLength} characters are allowed."]);

        var entries = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var errors = new List<string>();
        var lines = text.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (line.IndexOf('#') is var comment and >= 0) line = line[..comment];
            foreach (var token in line.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (token.Length > MaxEntryLength)
                {
                    errors.Add($"line {index + 1}: \"{Shorten(token)}\" is longer than {MaxEntryLength} characters");
                }
                else if (!TryNormalize(token, out var normalized))
                {
                    errors.Add($"line {index + 1}: \"{token}\" is not an IP address or CIDR range");
                }
                else if (seen.Add(normalized))
                {
                    entries.Add(normalized);
                }
            }
        }

        if (errors.Count == 0 && entries.Count > MaxEntries)
            errors.Add($"Too many entries: at most {MaxEntries} are allowed (found {entries.Count}).");

        return errors.Count == 0 ? new SshSourceParseResult(entries, []) : new SshSourceParseResult([], errors);
    }

    /// <summary>One entry per line, for the editor.</summary>
    public static string Format(IReadOnlyList<string> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return string.Join('\n', entries);
    }

    /// <summary>
    /// Whether <paramref name="address"/> is one of the <paramref name="allowed"/> addresses or inside one of the allowed
    /// ranges. An IPv4-mapped IPv6 address (<c>::ffff:a.b.c.d</c>) counts as its IPv4 address. An invalid address, and an entry
    /// that is not a valid address or range, never match. A scope id (<c>%eth0</c>) and surrounding brackets on the address are
    /// ignored.
    /// </summary>
    public static bool Matches(IReadOnlyList<string> allowed, string address)
    {
        if (allowed is null || allowed.Count == 0 || string.IsNullOrWhiteSpace(address)) return false;

        var text = address.Trim();
        if (text.Length > 2 && text[0] == '[' && text[^1] == ']') text = text[1..^1];
        if (text.IndexOf('%') is var scope and >= 0) text = text[..scope];
        if (!TryParseAddress(text, out var candidate)) return false;
        candidate = Canonical(candidate);

        foreach (var entry in allowed)
        {
            if (entry is null || !TryParseEntry(entry.Trim(), out var network, out var prefix)) continue;
            if (network.AddressFamily == candidate.AddressFamily && InRange(network, prefix, candidate)) return true;
        }

        return false;
    }

    /// <summary>The list as stored in <c>Server.AllowedSshSourcesJson</c>.</summary>
    public static string ToJson(IReadOnlyList<string> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return JsonSerializer.Serialize(entries);
    }

    /// <summary>Reads the stored JSON; damaged or empty JSON gives an empty list.</summary>
    public static IReadOnlyList<string> FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<string[]>(json)?.Where(entry => !string.IsNullOrWhiteSpace(entry)).ToArray() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Normal form of one entry: an address is trimmed and lower-case (IPv6 compressed); an IPv4-mapped IPv6 address becomes its
    /// IPv4 address; a CIDR range keeps only its network bits (<c>192.168.20.5/24</c> becomes <c>192.168.20.0/24</c>); a
    /// single-host range (<c>/32</c>, <c>/128</c>) becomes the plain address.
    /// </summary>
    public static bool TryNormalize(string entry, out string normalized)
    {
        normalized = string.Empty;
        if (entry is null || !TryParseEntry(entry.Trim(), out var network, out var prefix)) return false;
        var hostBits = network.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        normalized = prefix == hostBits ? network.ToString() : $"{network}/{prefix}";
        return true;
    }

    private static bool TryParseEntry(string entry, out IPAddress network, out int prefix)
    {
        network = IPAddress.None;
        prefix = 0;
        if (entry.Length == 0) return false;

        var slash = entry.IndexOf('/');
        var addressText = slash < 0 ? entry : entry[..slash];
        if (!TryParseAddress(addressText, out var address)) return false;

        var hostBits = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        prefix = hostBits;
        if (slash >= 0)
        {
            var prefixText = entry[(slash + 1)..];
            if (prefixText.Length is 0 or > 3 || !prefixText.All(char.IsAsciiDigit)) return false;
            prefix = int.Parse(prefixText, System.Globalization.CultureInfo.InvariantCulture);
            if (prefix > hostBits) return false;
        }

        // ::ffff:a.b.c.d is the IPv4 address a.b.c.d; a range wider than the mapped /96 block stays an IPv6 range.
        if (address.IsIPv4MappedToIPv6 && prefix >= 96)
        {
            address = address.MapToIPv4();
            prefix -= 96;
        }

        network = Mask(address, prefix);
        return true;
    }

    /// <summary>Strict address parsing: .NET also accepts "10.1", "1" or "0x7f.1" as IPv4, and "%scope" or brackets on IPv6.</summary>
    private static bool TryParseAddress(string text, out IPAddress address)
    {
        address = IPAddress.None;
        if (text.Contains(':'))
        {
            if (!text.All(character => char.IsAsciiHexDigit(character) || character is ':' or '.')) return false;
            if (!IPAddress.TryParse(text, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetworkV6) return false;
            address = parsed;
            return true;
        }

        var parts = text.Split('.');
        if (parts.Length != 4) return false;
        var bytes = new byte[4];
        for (var index = 0; index < 4; index++)
        {
            var part = parts[index];
            // No leading zeros: "010" is octal in some parsers and decimal in others.
            if (part.Length is 0 or > 3 || !part.All(char.IsAsciiDigit) || (part.Length > 1 && part[0] == '0')) return false;
            var value = int.Parse(part, System.Globalization.CultureInfo.InvariantCulture);
            if (value > 255) return false;
            bytes[index] = (byte)value;
        }

        address = new IPAddress(bytes);
        return true;
    }

    private static IPAddress Canonical(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static IPAddress Mask(IPAddress address, int prefix)
    {
        var bytes = address.GetAddressBytes();
        for (var index = 0; index < bytes.Length; index++)
        {
            var bitsInByte = Math.Clamp(prefix - index * 8, 0, 8);
            var mask = bitsInByte == 0 ? 0 : (byte)(0xFF << (8 - bitsInByte));
            bytes[index] &= (byte)mask;
        }

        return new IPAddress(bytes);
    }

    private static bool InRange(IPAddress network, int prefix, IPAddress candidate) =>
        Mask(candidate, prefix).Equals(network);

    private static string Shorten(string token) => token.Length <= 40 ? token : token[..40] + "…";
}
