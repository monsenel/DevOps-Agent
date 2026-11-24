using System.Text;
using System.Text.RegularExpressions;

namespace DevOpsAgent.Application.Chat;

/// <summary>
/// Redacts secrets from text before storage or sending to non-local LLM providers (T7.R30).
/// Covers PEM private keys (complete or truncated without END), URL credentials, Digest/Bearer/Basic auth,
/// key=value and key:value assignments (including values over 4096 chars and unquoted values with spaces),
/// CLI secret flags (--password x), and bare provider tokens (AWS AKIA..., GitHub ghp_..., Slack xox..., JWTs).
/// </summary>
public static partial class ChatContentRedactor
{
    private const string Redacted = "[REDACTED]";
    private const string RedactedPrivateKey = "[REDACTED PRIVATE KEY]";
    private const string Unscannable = "[REDACTED: content could not be scanned safely]";

    // Secret words are matched as whole name segments (split on _ - . and camelCase), so DB_PASSWORD, api_key,
    // X-Api-Token and "clientSecret" are masked while max_tokens, tokens_used and PWD are not.
    private static readonly HashSet<string> SecretSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "secret", "secrets", "token", "password", "passwd", "passphrase", "credential", "credentials", "authorization", "apikey"
    };

    // Compound names that run the words together (dbpassword, accesstoken, secretkey, privatekey) end with a secret word.
    private static readonly string[] SecretSuffixes =
        ["password", "passwd", "passphrase", "token", "secret", "apikey", "privatekey", "accesskey", "secretkey", "credential", "credentials"];

    // Ordinary words ending in "pass" that are never credentials (so dbpass= is caught while compass/bypass stay untouched).
    private static readonly HashSet<string> NonSecretPassWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "compass", "encompass", "bypass", "surpass", "trespass", "overpass", "underpass", "impasse",
        "lowpass", "highpass", "bandpass", "allpass", "repass", "outpass", "multipass", "gatepass", "gamepass", "hallpass"
    };

    private static readonly (string First, string Second)[] SecretPairs =
        [("api", "key"), ("access", "key"), ("private", "key"), ("secret", "key")];

    // Free-text bearer credentials (logs, curl output) are masked even without an Authorization name (unbounded length).
    [GeneratedRegex("(?<scheme>\\bBearer)[ \\t]{1,8}[A-Za-z0-9._~+/=\\-]{16,}",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex BearerToken();

    // Free-text HTTP Basic credentials: validated in MatchEvaluator so prose like "Basic monitoring" is untouched.
    [GeneratedRegex("(?<scheme>\\bBasic)[ \\t]{1,8}(?<cred>[A-Za-z0-9+/]{4,}={0,2})(?=$|[\\s,;'\")\\]])",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex BasicCredential();

    // scheme://user:password@host -> scheme://user:[REDACTED]@host (connection strings, clone URLs, unbounded password length).
    [GeneratedRegex("(?<prefix>[A-Za-z][A-Za-z0-9+.\\-]{0,31}://[^\\s:/@]{0,256}):[^\\s@/]+@",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex UrlUserInfo();

    // Digest credentials are a list of quoted parameters, so the rest of the header line is masked (unbounded length).
    [GeneratedRegex("(?<head>Authorization[ \\t]{0,8}[:=][ \\t]{0,8}\\\"?Digest)[ \\t]{1,8}[^\\r\\n]+",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex DigestHeader();

    // Bare provider tokens and JWTs outside assignments.
    [GeneratedRegex(
        "(?:\\b(?:AKIA|ASIA|AIDA|AROA|AIPA|ANPA|ANVA)[A-Z0-9]{16}\\b"
        + "|\\b(?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{12,}\\b"
        + "|\\bgithub_pat_[A-Za-z0-9_]{22,}\\b"
        + "|\\bxox[baprs]-[A-Za-z0-9\\-]{10,}\\b"
        + "|\\beyJ[A-Za-z0-9_\\-]{4,}\\.[A-Za-z0-9_\\-]{4,}\\.[A-Za-z0-9_\\-]{4,}\\b)",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex BareProviderToken();

    // Space-separated CLI options like `--password secret`, `--token "a b"`, `--api-key k`.
    [GeneratedRegex("(?<=^|[\\s\"'(\\[;=,])(?<flag>--[A-Za-z][A-Za-z0-9_.\\-]{0,63})(?<sep>[ \\t]{1,8})",
        RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex CliFlagHead();

    [GeneratedRegex("^(?i:Bearer|Basic|Token|ApiKey)[ \\t]{1,8}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex AuthSchemePrefix();

    [GeneratedRegex("(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])|[_.\\-]", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex NameSegments();

    public static string Redact(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        try
        {
            if (value.Contains("-----BEGIN ", StringComparison.Ordinal))
                value = RedactPrivateKeys(value);
            if (value.Contains("://", StringComparison.Ordinal))
                value = UrlUserInfo().Replace(value, "${prefix}:[REDACTED]@");
            if (value.Contains("Digest", StringComparison.OrdinalIgnoreCase))
                value = DigestHeader().Replace(value, "${head} [REDACTED]");
            if (value.Contains("Bearer", StringComparison.OrdinalIgnoreCase))
                value = BearerToken().Replace(value, "${scheme} [REDACTED]");
            if (value.Contains("Basic", StringComparison.OrdinalIgnoreCase))
                value = BasicCredential().Replace(value, MaskBasic);
            if (value.IndexOfAny(['=', ':']) >= 0)
                value = RedactAssignments(value);
            if (value.Contains("--", StringComparison.Ordinal))
                value = RedactCliFlags(value);
            value = BareProviderToken().Replace(value, Redacted);
            return value;
        }
        catch (RegexMatchTimeoutException)
        {
            // Never forward text that could not be scanned.
            return Unscannable;
        }
    }

    private static string MaskBasic(Match match)
    {
        var cred = match.Groups["cred"].Value;
        return IsLikelyBasicCredential(cred) ? $"{match.Groups["scheme"].Value} {Redacted}" : match.Value;
    }

    private static bool IsLikelyBasicCredential(string token)
    {
        if (token.Length >= 4 && token.Length % 4 == 0 && TryDecodeBase64Printable(token, out var decoded)
            && decoded.Contains(':', StringComparison.Ordinal))
        {
            return true;
        }

        if (token.Length < 12) return false;
        if (token.Contains('=') || token.Contains('+') || token.Contains('/')) return true;

        var hasDigit = false;
        var hasLetter = false;
        var hasLower = false;
        var hasInternalUpper = false;
        for (var i = 0; i < token.Length; i++)
        {
            var c = token[i];
            if (char.IsAsciiDigit(c)) hasDigit = true;
            else if (char.IsAsciiLetterLower(c)) { hasLetter = true; hasLower = true; }
            else if (char.IsAsciiLetterUpper(c))
            {
                hasLetter = true;
                if (i > 0) hasInternalUpper = true;
            }
        }

        return (hasDigit && hasLetter) || (hasInternalUpper && hasLower);
    }

    private static bool TryDecodeBase64Printable(string token, out string decoded)
    {
        decoded = string.Empty;
        try
        {
            var bytes = Convert.FromBase64String(token);
            if (bytes.Length < 3) return false;
            foreach (var b in bytes)
                if (b < 0x20 || b >= 0x7F) return false;
            decoded = Encoding.ASCII.GetString(bytes);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    // Linear-time PEM private key redactor: handles arbitrarily large keys AND truncated PEM blocks without an END line.
    private static string RedactPrivateKeys(string value)
    {
        const string beginPrefix = "-----BEGIN ";
        const string keySuffix = "PRIVATE KEY-----";
        const string endPrefix = "-----END ";

        var first = value.IndexOf(beginPrefix, StringComparison.Ordinal);
        if (first < 0) return value;

        var result = new StringBuilder(value.Length);
        var cursor = 0;
        var search = first;
        while (search >= 0)
        {
            var headerLineEnd = value.IndexOfAny(['\r', '\n'], search);
            var headerSearchLimit = headerLineEnd < 0 ? value.Length : headerLineEnd;
            var suffixIndex = value.IndexOf(keySuffix, search + beginPrefix.Length,
                Math.Min(80, Math.Max(0, headerSearchLimit - (search + beginPrefix.Length))), StringComparison.Ordinal);
            if (suffixIndex < 0)
            {
                search = value.IndexOf(beginPrefix, search + beginPrefix.Length, StringComparison.Ordinal);
                continue;
            }

            var beginEnd = suffixIndex + keySuffix.Length;
            result.Append(value, cursor, search - cursor);
            result.Append(RedactedPrivateKey);

            // Look for matching -----END ... PRIVATE KEY----- anywhere after the BEGIN marker.
            var endMarker = FindEndPrivateKeyMarker(value, beginEnd, endPrefix, keySuffix);
            if (endMarker >= 0)
            {
                cursor = endMarker;
            }
            else
            {
                // Truncated PEM with no END line: consume armor headers and base64/key lines until non-key text or EOF.
                cursor = ScanTruncatedPemBodyEnd(value, beginEnd);
            }

            search = cursor < value.Length ? value.IndexOf(beginPrefix, cursor, StringComparison.Ordinal) : -1;
        }

        if (cursor < value.Length) result.Append(value, cursor, value.Length - cursor);
        return result.ToString();
    }

    private static int FindEndPrivateKeyMarker(string value, int start, string endPrefix, string keySuffix)
    {
        var pos = start;
        while (pos < value.Length)
        {
            var endIdx = value.IndexOf(endPrefix, pos, StringComparison.Ordinal);
            if (endIdx < 0) return -1;
            var lineEnd = value.IndexOfAny(['\r', '\n'], endIdx);
            var limit = lineEnd < 0 ? value.Length : lineEnd;
            var suffixIdx = value.IndexOf(keySuffix, endIdx + endPrefix.Length,
                Math.Min(80, Math.Max(0, limit - (endIdx + endPrefix.Length))), StringComparison.Ordinal);
            if (suffixIdx >= 0) return suffixIdx + keySuffix.Length;
            pos = endIdx + endPrefix.Length;
        }

        return -1;
    }

    private static int ScanTruncatedPemBodyEnd(string value, int pos)
    {
        var end = pos;
        var index = pos;
        while (index < value.Length)
        {
            var nextLineStart = index;
            if (value[nextLineStart] == '\r') nextLineStart++;
            if (nextLineStart < value.Length && value[nextLineStart] == '\n') nextLineStart++;
            else if (index == pos && value[index] is not ('\r' or '\n'))
            {
                // Inline key characters immediately after the BEGIN marker on the same line.
                var sameLineEnd = value.IndexOfAny(['\r', '\n'], index);
                end = sameLineEnd < 0 ? value.Length : sameLineEnd;
                index = end;
                continue;
            }

            if (nextLineStart >= value.Length) break;
            var lineEnd = value.IndexOfAny(['\r', '\n'], nextLineStart);
            var actualEnd = lineEnd < 0 ? value.Length : lineEnd;
            var line = value.AsSpan(nextLineStart, actualEnd - nextLineStart).Trim();
            if (line.IsEmpty)
            {
                // Allow a blank line inside PEM headers/body only if another PEM line follows.
                var peek = actualEnd;
                if (peek < value.Length && value[peek] == '\r') peek++;
                if (peek < value.Length && value[peek] == '\n') peek++;
                if (peek < value.Length)
                {
                    var peekEnd = value.IndexOfAny(['\r', '\n'], peek);
                    var peekLine = value.AsSpan(peek, (peekEnd < 0 ? value.Length : peekEnd) - peek).Trim();
                    if (IsPemBodyOrHeaderLine(peekLine))
                    {
                        index = actualEnd;
                        end = actualEnd;
                        continue;
                    }
                }
                break;
            }

            if (!IsPemBodyOrHeaderLine(line)) break;
            end = actualEnd;
            index = actualEnd;
        }

        return end;
    }

    private static bool IsPemBodyOrHeaderLine(ReadOnlySpan<char> line)
    {
        if (line.IsEmpty) return false;
        if (line.StartsWith("Proc-Type:", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("DEK-Info:", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Comment:", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("-----", StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var c in line)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=' or '_' or '-'))
                return false;
        }

        return true;
    }

    // Scan assignments anchored on '=' / ':' instead of regex-backtracking across every letter (O(n) even on 2 MB files).
    private static string RedactAssignments(string value)
    {
        StringBuilder? builder = null;
        var cursor = 0;
        var searchPos = 0;

        while (searchPos < value.Length)
        {
            var sepIndex = value.IndexOfAny(['=', ':'], searchPos);
            if (sepIndex < 0) break;

            if (sepIndex < cursor)
            {
                searchPos = cursor;
                continue;
            }

            // Walk backward across optional spaces/tabs before the separator.
            var nameEnd = sepIndex;
            var spacesBefore = 0;
            while (nameEnd > cursor && spacesBefore < 8 && value[nameEnd - 1] is ' ' or '\t')
            {
                nameEnd--;
                spacesBefore++;
            }

            // Optional closing quote on key name (e.g. "password": or 'api_key':).
            char? closingQuote = null;
            if (nameEnd > cursor && value[nameEnd - 1] is '"' or '\'')
            {
                closingQuote = value[nameEnd - 1];
                nameEnd--;
            }

            // Walk backward across identifier characters [A-Za-z0-9_.-] (at most 128 chars).
            var nameStart = nameEnd;
            while (nameStart > cursor && nameEnd - nameStart < 128 && IsNameChar(value[nameStart - 1]))
                nameStart--;
            // Skip leading punctuation (such as CLI "--password=...") so the identifier starts at its first letter.
            while (nameStart < nameEnd && !char.IsAsciiLetter(value[nameStart]))
                nameStart++;

            if (nameStart == nameEnd)
            {
                searchPos = sepIndex + 1;
                continue;
            }

            // Optional opening quote immediately before nameStart.
            var matchStart = nameStart;
            char? openQuote = null;
            if (nameStart > cursor && value[nameStart - 1] is '"' or '\'')
            {
                openQuote = value[nameStart - 1];
                matchStart = nameStart - 1;
            }
            else if (closingQuote is not null)
            {
                // Mismatched trailing quote without opening quote is not a valid quoted key.
                searchPos = sepIndex + 1;
                continue;
            }

            var keyName = value[nameStart..nameEnd];
            if (!IsSecretName(keyName))
            {
                searchPos = sepIndex + 1;
                continue;
            }

            // Walk forward across optional spaces/tabs after separator.
            var valueStart = sepIndex + 1;
            var spacesAfter = 0;
            while (valueStart < value.Length && spacesAfter < 8 && value[valueStart] is ' ' or '\t')
            {
                valueStart++;
                spacesAfter++;
            }

            var headEnd = valueStart;
            if (valueStart >= value.Length || value[valueStart] is '\r' or '\n')
            {
                searchPos = sepIndex + 1;
                continue;
            }

            // Optional Authorization scheme prefix (Bearer / Basic / Token / ApiKey).
            var keepScheme = string.Empty;
            var schemeMatch = AuthSchemePrefix().Match(value, valueStart, Math.Min(16, value.Length - valueStart));
            if (schemeMatch.Success && schemeMatch.Index == valueStart)
            {
                if (keyName.Contains("authorization", StringComparison.OrdinalIgnoreCase))
                    keepScheme = schemeMatch.Value;
                valueStart += schemeMatch.Length;
            }

            if (valueStart >= value.Length || value[valueStart] is '\r' or '\n' || StartsWithRedacted(value, valueStart))
            {
                searchPos = valueStart;
                continue;
            }

            if (!TryScanAssignmentValue(value, valueStart, openQuote, out var valueEnd, out var quote))
            {
                searchPos = sepIndex + 1;
                continue;
            }

            builder ??= new StringBuilder(value.Length);
            builder.Append(value, cursor, matchStart - cursor);
            builder.Append(value, matchStart, headEnd - matchStart);
            builder.Append(keepScheme);
            // Keep JSON valid: a quoted value stays quoted and a bare value under a double-quoted key becomes a string.
            if (quote.Length == 0 && openQuote == '"') quote = "\"";
            builder.Append(quote);
            builder.Append(Redacted);
            builder.Append(quote);
            cursor = valueEnd;
            searchPos = valueEnd;
        }

        if (builder is null) return value;
        if (cursor < value.Length) builder.Append(value, cursor, value.Length - cursor);
        return builder.ToString();
    }

    private static bool IsNameChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-';

    private static bool TryScanAssignmentValue(string text, int start, char? enclosingKeyQuote, out int end, out string quote)
    {
        end = start;
        quote = string.Empty;
        if (start >= text.Length) return false;

        var first = text[start];
        if (first == '"')
        {
            quote = "\"";
            var i = start + 1;
            while (i < text.Length && text[i] is not ('\r' or '\n'))
            {
                if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] is not ('\r' or '\n'))
                {
                    i += 2;
                    continue;
                }

                if (text[i] == '"')
                {
                    end = i + 1;
                    return true;
                }

                i++;
            }

            // Unterminated quote: redact through the rest of the line.
            end = i;
            return end > start;
        }

        if (first == '\'')
        {
            quote = "'";
            var i = start + 1;
            while (i < text.Length && text[i] is not ('\r' or '\n'))
            {
                if (text[i] == '\'')
                {
                    end = i + 1;
                    return true;
                }

                i++;
            }

            end = i;
            return end > start;
        }

        if (first is ',' or ';' or '}' or ']' or '\r' or '\n' || char.IsWhiteSpace(first))
            return false;

        // Unquoted value (e.g. DB_PASSWORD=a b c or DB_PASSWORD=s3cr3t host=db1):
        // continue across spaces unless the next token starts another assignment, CLI flag, comment, URL, or delimiter.
        var pos = start;
        var lastNonSpace = start;
        while (pos < text.Length)
        {
            var c = text[pos];
            if (c is '\r' or '\n' or ',' or ';' or '}' or ']') break;
            if (enclosingKeyQuote is '\'' && c == '\'') break;

            if (c is ' ' or '\t')
            {
                var next = pos;
                while (next < text.Length && text[next] is ' ' or '\t') next++;
                if (next >= text.Length || text[next] is '\r' or '\n' or '#' or ',' or ';' or '}' or ']')
                    break;
                if (enclosingKeyQuote is '\'' && text[next] == '\'') break;
                if (IsNextTokenBoundary(text, next)) break;
                pos = next;
                continue;
            }

            pos++;
            lastNonSpace = pos;
        }

        end = lastNonSpace;
        return end > start;
    }

    private static bool IsNextTokenBoundary(string text, int index)
    {
        var c = text[index];
        // CLI flag like -u or --host
        if (c == '-' && index + 1 < text.Length && (text[index + 1] == '-' || char.IsAsciiLetter(text[index + 1])))
            return true;
        // URL like https://...
        if (text.AsSpan(index).StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || text.AsSpan(index).StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Another key=value or key: value assignment (e.g. host=db1, "user": "deploy", password: x)
        return IsAssignmentStart(text, index);
    }

    private static bool IsAssignmentStart(string text, int index)
    {
        var i = index;
        var quoted = false;
        if (i < text.Length && text[i] is '"' or '\'')
        {
            quoted = true;
            i++;
        }

        if (i >= text.Length || !char.IsAsciiLetter(text[i])) return false;
        var nameStart = i;
        while (i < text.Length && i - nameStart < 128 && (char.IsAsciiLetterOrDigit(text[i]) || text[i] is '_' or '.' or '-'))
            i++;
        if (quoted)
        {
            if (i >= text.Length || text[i] != text[index]) return false;
            i++;
        }

        var spaces = 0;
        while (i < text.Length && spaces < 8 && text[i] is ' ' or '\t') { i++; spaces++; }
        return i < text.Length && text[i] is '=' or ':';
    }

    private static string RedactCliFlags(string value)
    {
        var matches = CliFlagHead().Matches(value);
        if (matches.Count == 0) return value;

        StringBuilder? builder = null;
        var cursor = 0;
        foreach (Match match in matches)
        {
            if (match.Index < cursor) continue;
            var flag = match.Groups["flag"].Value;
            if (!IsSecretName(flag[2..])) continue;

            var valueStart = match.Index + match.Length;
            if (valueStart >= value.Length || value[valueStart] is '\r' or '\n') continue;
            if (StartsWithRedacted(value, valueStart)) continue;

            string quote;
            int valueEnd;
            var first = value[valueStart];
            if (first == '"')
            {
                quote = "\"";
                var i = valueStart + 1;
                while (i < value.Length && value[i] is not ('\r' or '\n'))
                {
                    if (value[i] == '\\' && i + 1 < value.Length && value[i + 1] is not ('\r' or '\n')) { i += 2; continue; }
                    if (value[i] == '"') { i++; break; }
                    i++;
                }
                valueEnd = i;
            }
            else if (first == '\'')
            {
                quote = "'";
                var i = valueStart + 1;
                while (i < value.Length && value[i] is not ('\r' or '\n'))
                {
                    if (value[i] == '\'') { i++; break; }
                    i++;
                }
                valueEnd = i;
            }
            else
            {
                // Do not eat the next --long-option if the flag had no argument.
                if (value.AsSpan(valueStart).StartsWith("--", StringComparison.Ordinal)) continue;
                quote = string.Empty;
                var i = valueStart;
                while (i < value.Length && !char.IsWhiteSpace(value[i]) && value[i] is not (';' or ',' or ')' or ']' or '}'))
                    i++;
                valueEnd = i;
            }

            if (valueEnd <= valueStart) continue;
            builder ??= new StringBuilder(value.Length);
            builder.Append(value, cursor, match.Index - cursor);
            builder.Append(match.Value);
            builder.Append(quote);
            builder.Append(Redacted);
            builder.Append(quote);
            cursor = valueEnd;
        }

        if (builder is null) return value;
        if (cursor < value.Length) builder.Append(value, cursor, value.Length - cursor);
        return builder.ToString();
    }

    private static bool StartsWithRedacted(string value, int start)
    {
        var slice = value.AsSpan(start);
        return slice.StartsWith(Redacted, StringComparison.Ordinal)
            || slice.StartsWith($"\"{Redacted}\"", StringComparison.Ordinal)
            || slice.StartsWith($"'{Redacted}'", StringComparison.Ordinal);
    }

    private static bool IsSecretName(string name)
    {
        var tokens = NameSegments().Split(name).Where(token => token.Length > 0).ToArray();
        if (tokens.Length > 0)
        {
            var last = tokens[^1];
            if (last.Equals("pass", StringComparison.OrdinalIgnoreCase)) return true;
            if (last.EndsWith("pass", StringComparison.OrdinalIgnoreCase) && !NonSecretPassWords.Contains(last))
                return true;
        }

        if (SecretSuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))) return true;
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
