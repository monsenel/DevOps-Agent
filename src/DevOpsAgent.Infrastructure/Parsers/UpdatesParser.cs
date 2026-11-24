using System.Text.RegularExpressions;
using DevOpsAgent.Application.Updates;

namespace DevOpsAgent.Infrastructure.Parsers;

public static partial class UpdatesParser
{
    [GeneratedRegex("^([^\\s/]+)(?:/([^\\s]+))?\\s+([^\\s]+)\\s+([^\\s]+)(?:\\s+\\[upgradable from:\\s*([^\\]]+)\\])?", RegexOptions.CultureInvariant)]
    private static partial Regex AptLine();

    // "nginx-1.26.3-r0 x86_64 {nginx} (BSD-2-Clause) [upgradable from: nginx-1.26.2-r0]"
    [GeneratedRegex("^(?<name>.+?)-(?<ver>[0-9][^\\s]*)\\s+(?<arch>\\S+)\\s+\\{(?<origin>[^}]+)\\}.*\\[upgradable from:\\s*(?<from>[^\\]]+)\\]", RegexOptions.CultureInvariant)]
    private static partial Regex ApkListLine();

    // "nginx-1.26.2-r0 < 1.26.3-r0"
    [GeneratedRegex("^(?<name>.+?)-(?<cur>[0-9][^\\s]*)\\s+<\\s+(?<cand>\\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex ApkVersionLine();

    public static IReadOnlyList<UpdatePackageDto> ParseApt(string output)
    {
        var packages = new List<UpdatePackageDto>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = AptLine().Match(line);
            if (!match.Success || line.StartsWith("Listing", StringComparison.OrdinalIgnoreCase)) continue;
            var origin = match.Groups[2].Value;
            var security = origin.Contains("security", StringComparison.OrdinalIgnoreCase);
            packages.Add(new(match.Groups[1].Value, match.Groups[5].Success ? match.Groups[5].Value : string.Empty,
                match.Groups[3].Value, origin.Length == 0 ? match.Groups[4].Value : origin, security));
        }
        return packages;
    }

    public static IReadOnlyList<UpdatePackageDto> ParseDnf(string output, string securityOutput)
    {
        var packages = new List<UpdatePackageDto>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("Last metadata expiration", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Obsoleting", StringComparison.OrdinalIgnoreCase)) continue;
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 3 || fields[0].StartsWith("=")) continue;
            var name = fields[0];
            var packageName = name.Contains('.') ? name[..name.LastIndexOf('.')] : name;
            if (!packageName.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '+' or '_' or '-')) continue;
            var securityMatch = Regex.IsMatch(securityOutput,
                $@"(?im)(?<![A-Za-z0-9_-]){Regex.Escape(packageName)}(?:[-.]|\s)", RegexOptions.CultureInvariant);
            packages.Add(new(name, string.Empty, fields[1], fields[2], securityMatch));
        }
        return packages;
    }

    /// <summary>Parses Alpine <c>apk list -u</c> (preferred) or <c>apk version -l '&lt;'</c> output.</summary>
    public static IReadOnlyList<UpdatePackageDto> ParseApk(string output)
    {
        var packages = new List<UpdatePackageDto>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("Installed:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("WARNING:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (ApkListLine().Match(line) is { Success: true } listMatch)
            {
                var name = listMatch.Groups["name"].Value;
                var candidate = listMatch.Groups["ver"].Value;
                var from = listMatch.Groups["from"].Value.Trim();
                var current = from.StartsWith(name + "-", StringComparison.Ordinal)
                    ? from[(name.Length + 1)..]
                    : from;
                packages.Add(new(name, current, candidate, "apk", false));
                continue;
            }

            if (ApkVersionLine().Match(line) is { Success: true } verMatch)
            {
                packages.Add(new(
                    verMatch.Groups["name"].Value,
                    verMatch.Groups["cur"].Value,
                    verMatch.Groups["cand"].Value,
                    "apk",
                    false));
            }
        }

        return packages;
    }
}
