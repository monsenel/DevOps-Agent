using System.Globalization;

namespace DevOpsAgent.Infrastructure.Ssh;

public sealed class KnownHostsVerifier
{
    private readonly IReadOnlyList<KnownHostEntry> _entries;

    public KnownHostsVerifier(string filePath)
    {
        _entries = File.Exists(filePath)
            ? File.ReadLines(filePath).Select(Parse).Where(entry => entry is not null).Select(entry => entry!).ToArray()
            : [];
    }

    public bool Matches(string host, int port, byte[] keyData)
    {
        var expectedNames = port == 22
            ? new[] { host, $"[{host}]:{port.ToString(CultureInfo.InvariantCulture)}" }
            : [$"[{host}]:{port.ToString(CultureInfo.InvariantCulture)}"];

        return _entries.Any(entry => entry.HostNames.Any(name => expectedNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            && entry.KeyData.AsSpan().SequenceEqual(keyData));
    }

    private static KnownHostEntry? Parse(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#') || trimmed.StartsWith('@'))
        {
            return null;
        }

        var fields = trimmed.Split((char[]?)null, 4, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 3 || fields[0].StartsWith('|') || fields[0].StartsWith('!'))
        {
            return null;
        }

        try
        {
            return new KnownHostEntry(fields[0].Split(',', StringSplitOptions.RemoveEmptyEntries), Convert.FromBase64String(fields[2]));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private sealed record KnownHostEntry(IReadOnlyList<string> HostNames, byte[] KeyData);
}
