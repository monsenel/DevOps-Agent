using System.Text.RegularExpressions;
using DevOpsAgent.Application.Chat;

namespace DevOpsAgent.Infrastructure.Collectors;

/// <summary>
/// Removes credentials from raw <c>nginx -T</c> output before it is stored. Only values are masked; directive names,
/// paths and proxy targets stay, so the parser and the graph are unaffected.
/// </summary>
public static partial class NginxConfigRedactor
{
    private const string Masked = "[REDACTED]";

    // proxy_set_header / add_header / fastcgi_param / uwsgi_param / set / more_set_headers whose name looks secret.
    [GeneratedRegex(
        "^(?<head>\\s*(?:proxy_set_header|add_header|more_set_headers|more_set_input_headers|fastcgi_param|uwsgi_param|scgi_param|set)\\s+\"?)(?<name>[^\\s\"';]{1,128})(?<mid>\"?\\s+)(?<value>[^;]*)(?<tail>;.*)$",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ValueDirective();

    // Authorization headers set with a literal value (Basic base64 or Bearer token) in any form.
    [GeneratedRegex("(?<scheme>\\b(?:Basic|Bearer))\\s+[A-Za-z0-9._~+/=\\-]{8,4096}",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AuthScheme();

    public static string Redact(string configuration)
    {
        var lines = configuration.Split('\n');
        for (var index = 0; index < lines.Length; index++) lines[index] = RedactLine(lines[index]);
        return string.Join('\n', lines);
    }

    private static string RedactLine(string line)
    {
        if (line.Length == 0) return line;
        try
        {
            var match = ValueDirective().Match(line);
            if (match.Success)
            {
                var name = match.Groups["name"].Value.TrimStart('$');
                // "X-Api-Key" and "$secret_token" are secret names; the same probe the chat redactor uses.
                if (ChatContentRedactor.Redact($"{name}=x") != $"{name}=x" || name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                {
                    var value = match.Groups["value"].Value;
                    // A variable reference ("$http_authorization", "$token") is not a credential and stays readable.
                    if (value.Trim().Trim('"', '\'') is { Length: > 0 } trimmed && !IsVariableOnly(trimmed))
                        return $"{match.Groups["head"].Value}{match.Groups["name"].Value}{match.Groups["mid"].Value}\"{Masked}\"{match.Groups["tail"].Value}";
                }
            }

            line = AuthScheme().Replace(line, "${scheme} " + Masked);
            var redacted = ChatContentRedactor.Redact(line);
            return redacted == line ? line : redacted;
        }
        catch (RegexMatchTimeoutException)
        {
            return "# [REDACTED: line could not be scanned safely]";
        }
    }

    private static bool IsVariableOnly(string value) =>
        value.StartsWith('$') && value.Skip(1).All(character => char.IsLetterOrDigit(character) || character == '_');
}
