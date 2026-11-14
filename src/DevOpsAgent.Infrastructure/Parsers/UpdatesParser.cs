using System.Text.RegularExpressions;
using DevOpsAgent.Application.Updates;

namespace DevOpsAgent.Infrastructure.Parsers;

public static partial class UpdatesParser
{
    [GeneratedRegex("^([^\\s/]+)(?:/([^\\s]+))?\\s+([^\\s]+)\\s+([^\\s]+)(?:\\s+\\[upgradable from:\\s*([^\\]]+)\\])?", RegexOptions.CultureInvariant)]
    private static partial Regex AptLine();

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
}
