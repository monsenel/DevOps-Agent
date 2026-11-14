using System.Text.Json;

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

public static class ChatContentRedactor
{
    private const System.Text.RegularExpressions.RegexOptions Options =
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant;

    // A name that merely contains a secret word (DB_PASSWORD, AWS_SECRET_ACCESS_KEY, api_key, "password" in JSON),
    // followed by : or =, then a quoted or bare value. An optional Bearer/Basic scheme stays visible.
    private static readonly System.Text.RegularExpressions.Regex SecretAssignment = new(
        "(?i)(?<key>[\"']?[A-Za-z0-9_.\\-]*(?:api[_-]?key|access[_-]?key|private[_-]?key|secret|token|password|passwd|pwd|authorization|credential)[A-Za-z0-9_.\\-]*[\"']?\\s*[:=]\\s*)"
        + "(?<scheme>(?:Bearer|Basic)\\s+)?(?:\"[^\"\\r\\n]*\"|'[^'\\r\\n]*'|[^\\s,;\"']+)",
        Options);
    private static readonly System.Text.RegularExpressions.Regex BearerToken = new(
        "(?i)\\b(?<scheme>Bearer|Basic)\\s+[A-Za-z0-9._~+/=\\-]{8,}", Options);
    private static readonly System.Text.RegularExpressions.Regex PrivateKey = new(
        "-----BEGIN [^-]*PRIVATE KEY-----[\\s\\S]*?-----END [^-]*PRIVATE KEY-----", Options);

    public static string Redact(string value)
    {
        value = PrivateKey.Replace(value, "[REDACTED PRIVATE KEY]");
        value = BearerToken.Replace(value, "${scheme} [REDACTED]");
        return SecretAssignment.Replace(value, "${key}${scheme}[REDACTED]");
    }
}
