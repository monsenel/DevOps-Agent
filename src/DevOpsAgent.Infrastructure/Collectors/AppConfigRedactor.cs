using System.Text.RegularExpressions;
using DevOpsAgent.Application.Chat;

namespace DevOpsAgent.Infrastructure.Collectors;

/// <summary>
/// Redacts all credentials, passwords, API keys, and tokens from raw application config and reverse proxy outputs
/// BEFORE they are stored or parsed, while preserving hostnames, IP addresses, ports, schemes, and database/repo names.
/// </summary>
public static partial class AppConfigRedactor
{
    private const string Masked = "[REDACTED]";

    // URL credentials: scheme://user:pass@host -> scheme://[REDACTED]@host
    [GeneratedRegex(@"(?<scheme>[A-Za-z][A-Za-z0-9+.\-]*://)(?<creds>[^/\s@""'<>]+)@",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex UrlCredsRegex();

    // Key-value connection string passwords (Npgsql, ADO.NET, StackExchange.Redis, libpq primary_conninfo):
    // Password=secret; | pwd=secret | password=secret (inside quotes or semicolon/comma/space delimited)
    [GeneratedRegex(@"(?<key>(?:^|[;,\s""'])(?:password|passwd|pwd|passphrase|secret|accesskey|accountkey|sharedaccesskey|auth_token|token)\s*=\s*)(?<val>[^;,\s""'<>]+)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ConnStringSecretRegex();

    // URL query string secrets: ?password=xxx or &token=xxx
    [GeneratedRegex(@"(?<prefix>[?&](?:password|passwd|pwd|token|api_key|apikey|secret|access_token)=)(?<val>[^&\s""'<>]+)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex QuerySecretRegex();

    // Redis / config space-separated secret directives: requirepass <val>, masterauth <val>, acl ... ><pass>
    [GeneratedRegex(@"^(?<head>\s*(?:requirepass|masterauth|auth-pass)\s+)(?<val>.+)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex RedisSecretDirectiveRegex();

    // XML secret tags (e.g. Maven settings.xml <password>...</password>, <passphrase>...</passphrase>, <privateKey>...</privateKey>)
    [GeneratedRegex(@"(?<open><(?:password|passphrase|secret|token|apiKey|privateKey)[^>]*>)(?<val>[^<]+)(?<close></(?:password|passphrase|secret|token|apiKey|privateKey)>)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex XmlSecretTagRegex();

    // npmrc / env / properties / yaml lines where the key itself is secret (e.g. API_SECRET_KEY=..., :_authToken=..., _auth=...)
    [GeneratedRegex(@"^(?<head>\s*(?:export\s+)?[^=:\s#]*(?:password|passwd|secret|token|api[_-]?key|private[_-]?key|auth[_-]?token|_auth|_password|credential)[^=:\s#]*\s*(?:=|:)\s*)(?<val>.+)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SecretAssignmentRegex();

    // JSON secret fields: "Password": "value", "ApiKey": "value" (only when value does not look like a connection string with Host=)
    [GeneratedRegex(@"(?<head>""[^""]*(?:password|passwd|secret|token|api[_-]?key|private[_-]?key)[^""]*""\s*:\s*"")(?<val>[^""]+)(?<tail>"")",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex JsonSecretFieldRegex();

    // Authorization headers (Basic / Bearer)
    [GeneratedRegex(@"(?<scheme>\b(?:Basic|Bearer))\s+[A-Za-z0-9._~+/=\-]{6,4096}",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AuthHeaderRegex();

    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var lines = text.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            lines[index] = RedactLine(lines[index]);
        }

        return string.Join('\n', lines);
    }

    private static string RedactLine(string line)
    {
        if (line.Length == 0) return line;
        // Preserve collector section markers verbatim.
        if (line.StartsWith("__DVA_", StringComparison.Ordinal)) return line;

        try
        {
            var current = line;
            current = UrlCredsRegex().Replace(current, "${scheme}" + Masked + "@");
            current = QuerySecretRegex().Replace(current, "${prefix}" + Masked);
            current = XmlSecretTagRegex().Replace(current, "${open}" + Masked + "${close}");
            current = RedisSecretDirectiveRegex().Replace(current, "${head}" + Masked);
            current = ConnStringSecretRegex().Replace(current, "${key}" + Masked);
            current = AuthHeaderRegex().Replace(current, "${scheme} " + Masked);
            current = JsonSecretFieldRegex().Replace(current, match =>
            {
                var value = match.Groups["val"].Value;
                if (value.Contains("://", StringComparison.Ordinal) || value.Contains("Host=", StringComparison.OrdinalIgnoreCase))
                {
                    return match.Value;
                }

                return $"{match.Groups["head"].Value}{Masked}{match.Groups["tail"].Value}";
            });

            // Only mask entire RHS of a secret-named assignment when RHS is NOT a URL/connection string whose host we still want.
            current = SecretAssignmentRegex().Replace(current, match =>
            {
                var value = match.Groups["val"].Value.Trim();
                if (value.Contains("://", StringComparison.Ordinal) || value.Contains("Host=", StringComparison.OrdinalIgnoreCase))
                {
                    return match.Value;
                }

                return $"{match.Groups["head"].Value}{Masked}";
            });

            return ChatContentRedactor.Redact(current);
        }
        catch (RegexMatchTimeoutException)
        {
            return "# [REDACTED: line could not be scanned safely]";
        }
    }
}
