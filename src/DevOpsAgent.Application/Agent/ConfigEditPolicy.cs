using System.Text;
using System.Text.RegularExpressions;

namespace DevOpsAgent.Application.Agent;

/// <summary>Settings of the edit_config tool (T8.8).</summary>
public sealed class ConfigEditOptions
{
    public const string SectionName = "ConfigEdit";

    /// <summary>
    /// Paths edit_config may change: <c>**</c> matches any number of directories, <c>*</c> part of one name. The sensitive-path
    /// deny list applies on top.
    /// </summary>
    public string[] AllowedPaths { get; set; } = ["/etc/nginx/**"];

    /// <summary>How a changed file is validated and its service reloaded; the first rule whose pattern matches wins.</summary>
    public ConfigServiceRule[] Services { get; set; } = [ConfigServiceRule.Nginx];

    public int MaxContentBytes { get; set; } = 256 * 1024;

    public ConfigServiceRule? RuleFor(string path) => Services.FirstOrDefault(rule => ConfigEditPolicy.Matches(path, rule.PathPattern));
}

/// <param name="Validate">Validation command run as root after the write (empty: none); a failure restores the backup.</param>
/// <param name="Reload">Reload command run as root after validation (empty: none).</param>
public sealed record ConfigServiceRule(string PathPattern, string Service, string Validate, string Reload)
{
    // A ConfigurationBinder needs a parameterless constructor for array items.
    public ConfigServiceRule() : this(string.Empty, string.Empty, string.Empty, string.Empty) { }

    public static ConfigServiceRule Nginx { get; } = new("/etc/nginx/**", "nginx", "nginx -t",
        "if command -v systemctl >/dev/null 2>&1 && systemctl is-active --quiet nginx 2>/dev/null; then systemctl reload nginx; else nginx -s reload; fi");
}

public static partial class ConfigEditPolicy
{
    /// <summary>Null when edit_config may change the path, otherwise why not (never echoes file content).</summary>
    public static string? Check(string? path, IEnumerable<string> allowedPatterns)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith('/')) return "The path must be absolute.";
        if (path.Length > 1024 || path.Any(char.IsControl)) return "The path is too long or contains control characters.";
        if (path.Split('/').Any(segment => segment is "." or "..") || path.Contains("//", StringComparison.Ordinal) || path.EndsWith('/'))
            return "The path must be a plain absolute file path (no '.', '..' or empty segments).";
        if (!SafePath().IsMatch(path)) return "The path contains characters edit_config does not accept.";
        if (SensitivePathPolicy.Check(path) is { } sensitive) return sensitive;
        return allowedPatterns.Any(pattern => Matches(path, pattern))
            ? null
            : $"edit_config may only change allowlisted paths ({string.Join(", ", allowedPatterns)}).";
    }

    public static bool Matches(string path, string pattern)
    {
        var regex = new StringBuilder("\\A");
        for (var index = 0; index < pattern.Length; index++)
        {
            if (pattern[index] == '*' && index + 1 < pattern.Length && pattern[index + 1] == '*')
            {
                regex.Append(".*");
                index++;
            }
            else if (pattern[index] == '*') regex.Append("[^/]*");
            else regex.Append(Regex.Escape(pattern[index].ToString()));
        }

        return Regex.IsMatch(path, regex.Append("\\z").ToString(), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    [GeneratedRegex(@"\A[A-Za-z0-9._/@+-]+\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SafePath();
}

/// <summary>Unified diffs for edit_config (T8.8): applied to the current file, and made for the approval card.</summary>
public static partial class UnifiedDiff
{
    /// <summary>Applies a unified diff; every context and removed line must match the current file exactly.</summary>
    public static string Apply(string original, string diff)
    {
        var eol = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var source = SplitLines(original, out var trailingNewline);
        var result = new List<string>();
        var position = 0;
        var lines = diff.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var hunks = 0;
        var outputTrailingNewline = trailingNewline;
        char? lastOp = null;
        for (var index = 0; index < lines.Length; index++)
        {
            var header = HunkHeader().Match(lines[index]);
            if (!header.Success) continue;
            hunks++;
            if (!int.TryParse(header.Groups[1].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var start))
                throw new ArgumentException("The diff does not fit the current file (hunk position).");
            var oldStart = Math.Max(start - 1, 0);
            if (oldStart < position || oldStart > source.Count) throw new ArgumentException("The diff does not fit the current file (hunk position).");
            result.AddRange(source.Skip(position).Take(oldStart - position));
            position = oldStart;
            lastOp = null;
            for (index++; index < lines.Length && !lines[index].StartsWith("@@", StringComparison.Ordinal); index++)
            {
                var line = lines[index];
                if (line.StartsWith('\\'))
                {
                    // "\ No newline at end of file" applies to the immediately preceding line op.
                    if (lastOp is '+' or ' ') outputTrailingNewline = false;
                    else if (lastOp == '-') outputTrailingNewline = true;
                    continue;
                }

                if (line.Length == 0 && index == lines.Length - 1) continue;
                var kind = line.Length == 0 ? ' ' : line[0];
                var text = line.Length == 0 ? string.Empty : line[1..];
                switch (kind)
                {
                    case ' ' or '-':
                        if (position >= source.Count || source[position] != text)
                            throw new ArgumentException($"The diff does not match the current file at line {position + 1}.");
                        if (kind == ' ') result.Add(text);
                        position++;
                        lastOp = kind;
                        break;
                    case '+':
                        result.Add(text);
                        lastOp = '+';
                        break;
                    default:
                        if (line.StartsWith("---", StringComparison.Ordinal) || line.StartsWith("+++", StringComparison.Ordinal)) break;
                        throw new ArgumentException("The diff contains a line that is not context, removal or addition.");
                }
            }

            index--;
        }

        if (hunks == 0) throw new ArgumentException("The diff has no hunks (@@ -a,b +c,d @@).");
        result.AddRange(source.Skip(position));
        return result.Count == 0
            ? (outputTrailingNewline ? eol : string.Empty)
            : string.Join(eol, result) + (outputTrailingNewline ? eol : string.Empty);
    }

    /// <summary>A unified diff with three lines of context (for review; not minimal on very large files).</summary>
    public static string Create(string original, string updated, string path)
    {
        var a = SplitLines(original, out var origTrailing);
        var b = SplitLines(updated, out var updatedTrailing);
        // Longest common subsequence on lines: fine for configuration files (the size is capped).
        var lcs = new int[a.Count + 1, b.Count + 1];
        for (var i = a.Count - 1; i >= 0; i--)
        for (var j = b.Count - 1; j >= 0; j--)
            lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        var ops = new List<(char Kind, string Text, int OldLine, int NewLine)>();
        int x = 0, y = 0;
        while (x < a.Count || y < b.Count)
        {
            if (x < a.Count && y < b.Count && a[x] == b[y]) { ops.Add((' ', a[x], x, y)); x++; y++; }
            // Removals before additions, as diff(1) prints them.
            else if (x < a.Count && (y >= b.Count || lcs[x + 1, y] >= lcs[x, y + 1])) { ops.Add(('-', a[x], x, y)); x++; }
            else { ops.Add(('+', b[y], x, y)); y++; }
        }

        // If only the final newline changed on the last line, turn that shared last line into a -/+ pair so the diff is non-empty.
        if (origTrailing != updatedTrailing && ops.Count > 0 && ops[^1].Kind == ' ')
        {
            var last = ops[^1];
            ops.RemoveAt(ops.Count - 1);
            ops.Add(('-', last.Text, last.OldLine, last.NewLine));
            ops.Add(('+', last.Text, last.OldLine + 1, last.NewLine));
        }

        var output = new StringBuilder($"--- a{path}\n+++ b{path}\n");
        const int context = 3;
        var changed = ops.Select((op, index) => (op, index)).Where(item => item.op.Kind != ' ').Select(item => item.index).ToList();
        var cursor = 0;
        while (cursor < changed.Count)
        {
            var first = Math.Max(changed[cursor] - context, 0);
            var last = changed[cursor];
            while (cursor + 1 < changed.Count && changed[cursor + 1] - last <= context * 2) last = changed[++cursor];
            last = Math.Min(last + context, ops.Count - 1);
            cursor++;
            var slice = ops.Skip(first).Take(last - first + 1).ToList();
            var oldStart = slice[0].OldLine + 1;
            var newStart = slice[0].NewLine + 1;
            var oldCount = slice.Count(op => op.Kind != '+');
            var newCount = slice.Count(op => op.Kind != '-');
            output.Append($"@@ -{(oldCount == 0 ? oldStart - 1 : oldStart)},{oldCount} +{(newCount == 0 ? newStart - 1 : newStart)},{newCount} @@\n");
            for (var i = 0; i < slice.Count; i++)
            {
                var op = slice[i];
                output.Append(op.Kind).Append(op.Text).Append('\n');
                if (op.Kind == '-' && !origTrailing && op.OldLine == a.Count - 1)
                    output.Append("\\ No newline at end of file\n");
                else if (op.Kind == '+' && !updatedTrailing && op.NewLine == b.Count - 1)
                    output.Append("\\ No newline at end of file\n");
                else if (op.Kind == ' ' && !origTrailing && !updatedTrailing && op.OldLine == a.Count - 1 && op.NewLine == b.Count - 1)
                    output.Append("\\ No newline at end of file\n");
            }
        }

        return output.ToString();
    }

    private static List<string> SplitLines(string text, out bool trailingNewline)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        trailingNewline = normalized.EndsWith('\n');
        if (trailingNewline) normalized = normalized[..^1];
        return normalized.Length == 0 && !trailingNewline ? [] : normalized.Split('\n').ToList();
    }

    [GeneratedRegex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex HunkHeader();
}
