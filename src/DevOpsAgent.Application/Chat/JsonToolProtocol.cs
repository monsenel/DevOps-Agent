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
    private static readonly System.Text.RegularExpressions.Regex SecretAssignment = new(
        "(?i)(\\b(?:api[_-]?key|token|password|passwd|secret|authorization)\\b\\s*[:=]\\s*)([^\\s,;]+)",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex PrivateKey = new(
        "-----BEGIN [^-]*PRIVATE KEY-----[\\s\\S]*?-----END [^-]*PRIVATE KEY-----",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static string Redact(string value)
    {
        value = PrivateKey.Replace(value, "[REDACTED PRIVATE KEY]");
        return SecretAssignment.Replace(value, "$1[REDACTED]");
    }
}
