namespace DevOpsAgent.Application.Agent;

/// <summary>
/// Blocks read-only file tools (read_file, tail_log) from reading well-known secret stores. The check is lexical;
/// it cannot see through symlinks, so it complements (not replaces) the unprivileged SSH account.
/// </summary>
public static class SensitivePathPolicy
{
    private static readonly string[] KeyFilePrefixes = ["id_rsa", "id_ed25519", "id_ecdsa"];

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
        var parent = segments.Count >= 2 ? segments[^2] : string.Empty;
        if (name.StartsWith(".env", StringComparison.OrdinalIgnoreCase))
            return "Reading environment files is not allowed.";
        if (KeyFilePrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            || name.EndsWith(".key", StringComparison.OrdinalIgnoreCase)
            || IsPrivateKeyPem(name)
            || IsKeyContainer(name)
            || name.StartsWith("ssh_host_", StringComparison.Ordinal) && name.EndsWith("_key", StringComparison.Ordinal))
            return "Reading private key files is not allowed.";

        if (name.EndsWith("_history", StringComparison.OrdinalIgnoreCase))
            return "Reading shell and client history files is not allowed.";
        if (name.Equals(".git-credentials", StringComparison.OrdinalIgnoreCase)
            || name.Equals(".netrc", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(".htpasswd", StringComparison.OrdinalIgnoreCase)
            || parent.Equals(".aws", StringComparison.OrdinalIgnoreCase) && name.Equals("credentials", StringComparison.OrdinalIgnoreCase)
            || parent.Equals(".docker", StringComparison.OrdinalIgnoreCase) && name.Equals("config.json", StringComparison.OrdinalIgnoreCase))
            return "Reading credential files is not allowed.";

        if (segments[0].Equals("proc", StringComparison.Ordinal) && segments.Count >= 3
            && (segments[1] is "self" or "thread-self" || segments[1].All(char.IsAsciiDigit))
            && segments.Skip(2).Any(segment => segment is "environ" or "root"))
            return "Reading process environments and root filesystems is not allowed.";
        if (name.Equals("environ", StringComparison.Ordinal) && segments[0].Equals("proc", StringComparison.Ordinal))
            return "Reading process environments is not allowed.";

        // shadow and gshadow (and their backups) anywhere that ends in .../etc/shadow*, e.g. a mounted backup.
        if (parent.Equals("etc", StringComparison.Ordinal)
            && (name.StartsWith("shadow", StringComparison.Ordinal) || name.StartsWith("gshadow", StringComparison.Ordinal)))
            return "Reading password databases is not allowed.";
        if (segments.Count >= 2 && segments[0].Equals("etc", StringComparison.Ordinal)
            && segments[1].Equals("wireguard", StringComparison.Ordinal))
            return "Reading WireGuard configuration is not allowed.";
        for (var index = 0; index + 1 < segments.Count; index++)
        {
            if (segments[index].Equals("ssl", StringComparison.Ordinal) && segments[index + 1].Equals("private", StringComparison.Ordinal))
                return "Reading private TLS key directories is not allowed.";
        }

        return null;
    }

    // PEM files may hold private keys (or key + certificate bundles), so every .pem is blocked except well-known public names.
    // *.crt stays readable. Keystore containers always hold private keys.
    private static readonly string[] PublicPemNames = ["fullchain.pem", "cert.pem", "chain.pem", "ca.pem", "ca-bundle.pem"];
    private static readonly string[] KeyContainerSuffixes = [".p12", ".pfx", ".jks", ".keystore"];

    private static bool IsPrivateKeyPem(string name) =>
        name.EndsWith(".pem", StringComparison.OrdinalIgnoreCase)
        && !PublicPemNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static bool IsKeyContainer(string name) =>
        KeyContainerSuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    public static void EnsureAllowed(string? path)
    {
        if (Check(path) is { } error) throw new ArgumentException(error, nameof(path));
    }
}
