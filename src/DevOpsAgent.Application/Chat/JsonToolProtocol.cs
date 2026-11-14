using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevOpsAgent.Application.Chat;

public static class JsonToolProtocol
{
    public static (string Id, string Name, JsonElement Args)[] ParseCalls(string text)
        => ParseCalls(text, null);

    public static (string Id, string Name, JsonElement Args)[] ParseCalls(string assistantText, string? streamedText)
    {
        var text = string.IsNullOrWhiteSpace(assistantText) ? streamedText ?? string.Empty : assistantText;
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return [];
        try
        {
            using var document = JsonDocument.Parse(text[start..(end + 1)]);
            if (!document.RootElement.TryGetProperty("tool_calls", out var calls) || calls.ValueKind != JsonValueKind.Array) return [];
            return calls.EnumerateArray().Select((call, index) =>
            {
                var name = call.GetProperty("name").GetString() ?? string.Empty;
                var args = call.GetProperty("arguments").Clone();
                return (index.ToString(), name, args);
            }).ToArray();
        }
        catch (JsonException) { return []; }
        catch (InvalidOperationException) { return []; }
        catch (KeyNotFoundException) { return []; }
    }
}

public static partial class ChatContentRedactor
{
    private const string Redacted = "[REDACTED]";
    private const string Unscannable = "[REDACTED: content could not be scanned safely]";

    // Secret words are matched as whole name segments (split on _ - . and camelCase), so DB_PASSWORD, api_key,
    // X-Api-Token and "clientSecret" are masked while max_tokens, tokens_used and PWD are not.
    private static readonly HashSet<string> SecretSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "secret", "secrets", "token", "password", "passwd", "passphrase", "credential", "credentials", "authorization", "apikey"
    };

    private static readonly (string First, string Second)[] SecretPairs =
        [("api", "key"), ("access", "key"), ("private", "key"), ("secret", "key")];

    // name, separator, then an optional Authorization scheme and a double-quoted (JSON escapes allowed), single-quoted or
    // bare value. Every quantifier is bounded and the regexes carry a match timeout.
    [GeneratedRegex(
        "(?<name>[\"']?[A-Za-z][A-Za-z0-9_.\\-]{0,127})[\"']?[ \\t]{0,8}[:=][ \\t]{0,8}"
        + "(?<scheme>(?:Bearer|Basic)[ \\t]{1,8})?"
        + "(?:\"(?<dq>(?:[^\"\\\\\\r\\n]|\\\\.){0,4096})\"|'(?<sq>[^'\\r\\n]{0,4096})'|(?<bare>[^\\s,;\"']{1,4096})|(?<loose>[\"'][^\\r\\n]{0,4096}))",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex Assignment();

    [GeneratedRegex("-----BEGIN [^-]{0,64}PRIVATE KEY-----[\\s\\S]{0,16384}?-----END [^-]{0,64}PRIVATE KEY-----",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex PrivateKey();

    [GeneratedRegex("(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])|[_.\\-]", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex NameSegments();

    public static string Redact(string value)
    {
        try
        {
            value = PrivateKey().Replace(value, "[REDACTED PRIVATE KEY]");
            return Assignment().Replace(value, Mask);
        }
        catch (RegexMatchTimeoutException)
        {
            // Never forward text that could not be scanned.
            return Unscannable;
        }
    }

    private static string Mask(Match match)
    {
        var rawName = match.Groups["name"].Value;
        if (!IsSecretName(rawName.TrimStart('"', '\''))) return match.Value;

        var scheme = match.Groups["scheme"];
        var value = match.Groups["dq"].Success ? match.Groups["dq"]
            : match.Groups["sq"].Success ? match.Groups["sq"]
            : match.Groups["bare"].Success ? match.Groups["bare"]
            : match.Groups["loose"]; // an unterminated quote masks the rest of the line
        var quote = match.Groups["dq"].Success ? "\"" : match.Groups["sq"].Success ? "'" : string.Empty;
        // Everything before the value (name, separator) is kept; the scheme stays visible only for Authorization.
        var valueStart = value.Index - match.Index - quote.Length;
        var head = scheme.Success ? match.Value[..(scheme.Index - match.Index)] : match.Value[..valueStart];
        var keepScheme = scheme.Success && rawName.Contains("authorization", StringComparison.OrdinalIgnoreCase) ? scheme.Value : string.Empty;
        // Keep JSON valid: a quoted value stays quoted and a bare value under a quoted key becomes a string.
        if (quote.Length == 0 && rawName.Length > 0 && rawName[0] is '"')
            quote = "\"";
        return $"{head}{keepScheme}{quote}{Redacted}{quote}";
    }

    private static bool IsSecretName(string name)
    {
        var tokens = NameSegments().Split(name).Where(token => token.Length > 0).ToArray();
        for (var index = 0; index < tokens.Length; index++)
        {
            if (SecretSegments.Contains(tokens[index])) return true;
            if (index + 1 < tokens.Length && SecretPairs.Any(pair =>
                    tokens[index].Equals(pair.First, StringComparison.OrdinalIgnoreCase)
                    && tokens[index + 1].Equals(pair.Second, StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        return false;
    }
}
