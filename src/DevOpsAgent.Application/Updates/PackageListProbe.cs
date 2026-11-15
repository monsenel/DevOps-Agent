using System.Globalization;

namespace DevOpsAgent.Application.Updates;

/// <summary>
/// Read-only probe for "when were the package lists last refreshed", plus the parser for its output. apt: newest mtime of the
/// package lists (falling back to the package cache); dnf: newest repo solv-cache mtime. Needs no sudo and writes nothing.
/// </summary>
public static class PackageListProbe
{
    public const string Command =
        "t=''; if [ -d /var/lib/apt/lists ]; then "
        + "t=$(find /var/lib/apt/lists -maxdepth 1 -type f -name '*Packages*' -printf '%T@\\n' 2>/dev/null | sort -n | tail -n 1); "
        + "[ -n \"$t\" ] || t=$(stat -c %Y /var/cache/apt/pkgcache.bin 2>/dev/null); fi; "
        + "if [ -z \"$t\" ] && [ -d /var/cache/dnf ]; then "
        + "t=$(find /var/cache/dnf -maxdepth 2 -name '*.solv' ! -name '@System*' -printf '%T@\\n' 2>/dev/null | sort -n | tail -n 1); fi; "
        + "printf '%s\\n' \"$t\"";

    /// <summary>Parses the probe output (epoch seconds, optionally fractional) into UTC; null when unknown or implausible.</summary>
    public static DateTimeOffset? Parse(string? output)
    {
        var last = (output ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
        if (last is null || !double.TryParse(last, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)) return null;
        // 2001-09-09 .. 2100-01-01 keeps garbage (negative, zero, absurdly large) out.
        if (double.IsNaN(seconds) || seconds < 1_000_000_000 || seconds > 4_102_444_800) return null;
        return DateTimeOffset.FromUnixTimeSeconds((long)Math.Floor(seconds));
    }

    /// <summary>Text shown with a scan: when the lists were last refreshed, and whether the scan read cached lists only.</summary>
    public static string Describe(DateTimeOffset? refreshedAtUtc, bool cacheOnly, DateTimeOffset nowUtc)
    {
        var refreshed = refreshedAtUtc is { } at
            ? $"Package lists last refreshed {Age(nowUtc - at)}"
            : "Package lists last refreshed: unknown";
        return cacheOnly ? $"{refreshed} · cached lists only (read-only group)" : refreshed;
    }

    private static string Age(TimeSpan age) => age switch
    {
        { TotalMinutes: < 1 } => "just now",
        { TotalHours: < 1 } => $"{(int)age.TotalMinutes} min ago",
        { TotalDays: < 1 } => $"{(int)age.TotalHours} h ago",
        _ => $"{(int)age.TotalDays} d ago"
    };
}
