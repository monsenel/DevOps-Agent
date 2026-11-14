namespace DevOpsAgent.Application.Agent;

/// <summary>
/// Blocks read-only file tools (read_file, tail_log) from reading well-known secret stores. The check is lexical;
/// it cannot see through symlinks, so it complements (not replaces) the unprivileged SSH account.
/// </summary>
public static class SensitivePathPolicy
{
    private static readonly string[] KeyFilePrefixes = ["id_rsa", "id_ed25519", "id_ecdsa"];
    private static readonly string[] KeyFileSuffixes = [".pem", ".key"];

    /// <summary>Returns an error message when the path must not be read, otherwise null.</summary>
    public static string? Check(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "A file path is required.";
        if (path.Any(char.IsControl)) return "The path contains control characters.";

        var segments = new List<string>();
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..")
            {
                // Resolve lexically so "/home/u/x/../.ssh/id_rsa" cannot slip past the segment checks.
                if (segments.Count > 0) segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment);
        }

        if (segments.Count == 0) return null;
        if (segments.Any(segment => segment.Equals(".ssh", StringComparison.OrdinalIgnoreCase)))
            return "Reading SSH directories is not allowed.";

        var name = segments[^1];
        if (name.StartsWith(".env", StringComparison.OrdinalIgnoreCase))
            return "Reading environment files is not allowed.";
        if (KeyFileSuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            || KeyFilePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            return "Reading private key files is not allowed.";

        if (segments.Count >= 2 && segments[0].Equals("proc", StringComparison.Ordinal)
            && name.Equals("environ", StringComparison.Ordinal))
            return "Reading process environments is not allowed.";

        if (segments.Count == 2 && segments[0].Equals("etc", StringComparison.Ordinal)
            && (name.StartsWith("shadow", StringComparison.Ordinal) || name.StartsWith("gshadow", StringComparison.Ordinal)))
            return "Reading password databases is not allowed.";

        return null;
    }

    public static void EnsureAllowed(string? path)
    {
        if (Check(path) is { } error) throw new ArgumentException(error, nameof(path));
    }
}
