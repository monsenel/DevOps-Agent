using System.Text.RegularExpressions;
using DevOpsAgent.Application.Updates;

namespace DevOpsAgent.Infrastructure.Parsers;

/// <summary>
/// Package sources of a server (T8.6), parsed from read-only output: apt one-line <c>.list</c> files, deb822 <c>.sources</c>
/// stanzas and <c>dnf -C repolist -v</c>; and the origin of each upgradable apt package from <c>apt-cache policy</c>, so a
/// Docker repository and Ubuntu's own "jammy" are told apart.
/// </summary>
public static partial class PackageSourcesParser
{
    public const string FileMarker = "__DVA_SOURCE_FILE__ ";
    public const string PolicyMarker = "__DVA_APT_POLICY_PACKAGES__";
    public const string ApkMarker = "__DVA_APK_REPOS__";

    /// <summary>Prints every apt source file with a marker line before it; reads only world-readable files, no sudo.</summary>
    public const string AptSourcesCommand =
        "for f in /etc/apt/sources.list /etc/apt/sources.list.d/*.list /etc/apt/sources.list.d/*.sources; do "
        + "[ -r \"$f\" ] && { printf '" + FileMarker + "%s\\n' \"$f\"; cat \"$f\"; printf '\\n'; }; done; true";

    /// <summary>From the metadata cache only (<c>-C</c>): never downloads or writes anything.</summary>
    public const string DnfSourcesCommand = "dnf -C repolist -v 2>/dev/null; true";

    /// <summary>Reads Alpine's <c>/etc/apk/repositories</c>; read-only, no sudo.</summary>
    public const string ApkSourcesCommand = "printf '" + ApkMarker + "\\n'; cat /etc/apk/repositories 2>/dev/null; true";

    /// <summary>The global policy (origins and labels of every source) and the version tables of the given packages.</summary>
    public static string AptPolicyCommand(IEnumerable<string> packageNames) =>
        "apt-cache policy 2>/dev/null; printf '\\n" + PolicyMarker + "\\n'; apt-cache policy -- "
        + string.Join(' ', packageNames.Select(name => Application.Abstractions.RemoteCommand.ShellQuote(name))) + " 2>/dev/null; true";

    public static IReadOnlyList<PackageSourceDto> ParseApt(string output)
    {
        var sources = new List<PackageSourceDto>();
        foreach (var (file, content) in SplitFiles(output))
        {
            if (file.EndsWith(".sources", StringComparison.Ordinal)) sources.AddRange(ParseDeb822(file, content));
            else sources.AddRange(ParseOneLine(file, content));
        }

        return sources;
    }

    private static IEnumerable<(string File, string Content)> SplitFiles(string output)
    {
        string? file = null;
        var lines = new List<string>();
        foreach (var line in output.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            if (line.StartsWith(FileMarker, StringComparison.Ordinal))
            {
                if (file is not null) yield return (file, string.Join('\n', lines));
                file = line[FileMarker.Length..].Trim();
                lines.Clear();
            }
            else if (file is not null) lines.Add(line);
        }

        if (file is not null) yield return (file, string.Join('\n', lines));
    }

    // deb [arch=amd64 signed-by=/etc/apt/keyrings/docker.gpg] https://download.docker.com/linux/ubuntu jammy stable
    private static IEnumerable<PackageSourceDto> ParseOneLine(string file, string content)
    {
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            var commented = line.StartsWith('#');
            if (commented || line.Length == 0) continue;
            var match = OneLine().Match(line);
            if (!match.Success) continue;
            var options = match.Groups["options"].Value;
            var signedBy = SignedByOption().Match(options) is { Success: true } signed ? signed.Groups[1].Value : null;
            var parts = match.Groups["rest"].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            yield return new PackageSourceDto(match.Groups["type"].Value, parts[0], parts[1], parts.Skip(2).ToArray(), signedBy, file, Enabled: true);
        }
    }

    private static IEnumerable<PackageSourceDto> ParseDeb822(string file, string content)
    {
        foreach (var stanza in Regex.Split(content, @"\n\s*\n", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
        {
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? last = null;
            foreach (var raw in stanza.Split('\n'))
            {
                if (raw.TrimStart().StartsWith('#')) continue;
                if ((raw.StartsWith(' ') || raw.StartsWith('\t')) && last is not null)
                {
                    // A continuation line, e.g. an inline key block under Signed-By: kept out of the display.
                    if (!string.Equals(last, "Signed-By", StringComparison.OrdinalIgnoreCase)) fields[last] += " " + raw.Trim();
                    continue;
                }

                var separator = raw.IndexOf(':');
                if (separator <= 0) continue;
                last = raw[..separator].Trim();
                fields[last] = raw[(separator + 1)..].Trim();
            }

            if (!fields.TryGetValue("URIs", out var uris) || !fields.TryGetValue("Suites", out var suites)) continue;
            var types = fields.GetValueOrDefault("Types", "deb");
            var components = fields.GetValueOrDefault("Components", string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var signedBy = fields.TryGetValue("Signed-By", out var signed) ? (signed.Length == 0 ? "(inline key)" : signed) : null;
            var enabled = !string.Equals(fields.GetValueOrDefault("Enabled", "yes"), "no", StringComparison.OrdinalIgnoreCase);
            foreach (var type in types.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            foreach (var uri in uris.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            foreach (var suite in suites.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                yield return new PackageSourceDto(type, uri, suite, components, signedBy, file, enabled);
        }
    }

    /// <summary><c>dnf repolist -v</c>: Repo-id, Repo-name and Repo-baseurl or Repo-metalink of each repository.</summary>
    public static IReadOnlyList<PackageSourceDto> ParseDnf(string output)
    {
        var sources = new List<PackageSourceDto>();
        string? id = null, name = null, url = null;
        void Flush()
        {
            if (id is not null) sources.Add(new PackageSourceDto("rpm", url ?? string.Empty, id, [], null, name ?? id, Enabled: true));
            id = name = url = null;
        }

        foreach (var raw in output.Split('\n'))
        {
            var separator = raw.IndexOf(':');
            if (separator <= 0) continue;
            var key = raw[..separator].Trim();
            var value = raw[(separator + 1)..].Trim();
            switch (key)
            {
                case "Repo-id": Flush(); id = value; break;
                case "Repo-name": name = value; break;
                case "Repo-baseurl" or "Repo-metalink" or "Repo-mirrors" when url is null: url = value.Split(',')[0].Trim(); break;
            }
        }

        Flush();
        return sources;
    }

    /// <summary>
    /// Origin of each package's candidate version, e.g. "Docker CE · jammy" or "Ubuntu · jammy-security": the label (or
    /// origin) apt-cache policy reports for the source the candidate comes from, and its suite. Unknown packages are absent.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseAptOrigins(string output)
    {
        var marker = output.IndexOf(PolicyMarker, StringComparison.Ordinal);
        if (marker < 0) return new Dictionary<string, string>();
        var labels = ParseReleaseLabels(output[..marker]);
        var origins = new Dictionary<string, string>(StringComparer.Ordinal);
        string? package = null, candidate = null;
        var inCandidate = false;
        foreach (var raw in output[(marker + PolicyMarker.Length)..].Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && line.EndsWith(':'))
            {
                package = line[..^1];
                candidate = null;
                inCandidate = false;
                continue;
            }

            var trimmed = line.Trim();
            if (trimmed.StartsWith("Candidate:", StringComparison.Ordinal)) { candidate = trimmed["Candidate:".Length..].Trim(); continue; }
            if (package is null || candidate is null) continue;
            if (VersionLine().Match(trimmed) is { Success: true } version)
            {
                inCandidate = version.Groups[1].Value == candidate;
                continue;
            }

            if (inCandidate && !origins.ContainsKey(package) && SourceLine().Match(trimmed) is { Success: true } source)
            {
                var uri = source.Groups[1].Value;
                var dist = source.Groups[2].Value;
                var suite = dist.Split('/')[0];
                var label = labels.GetValueOrDefault($"{uri} {dist}") ?? HostOf(uri);
                origins[package] = $"{label} · {suite}";
            }
        }

        return origins;
    }

    // " 500 https://download.docker.com/linux/ubuntu jammy/stable amd64 Packages" followed by
    // "     release o=Docker,a=jammy,l=Docker CE,c=stable,b=amd64"
    private static Dictionary<string, string> ParseReleaseLabels(string output)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        string? current = null;
        foreach (var raw in output.Split('\n'))
        {
            var trimmed = raw.Trim();
            if (SourceLine().Match(trimmed) is { Success: true } source)
            {
                current = $"{source.Groups[1].Value} {source.Groups[2].Value}";
                continue;
            }

            if (current is null || !trimmed.StartsWith("release ", StringComparison.Ordinal)) continue;
            var fields = trimmed["release ".Length..].Split(',')
                .Select(part => part.Split('=', 2)).Where(part => part.Length == 2)
                .ToDictionary(part => part[0], part => part[1], StringComparer.Ordinal);
            var label = fields.GetValueOrDefault("l") is { Length: > 0 } l ? l : fields.GetValueOrDefault("o");
            if (!string.IsNullOrWhiteSpace(label)) labels[current] = label;
            current = null;
        }

        return labels;
    }

    /// <summary>Parses Alpine <c>/etc/apk/repositories</c> URLs.</summary>
    public static IReadOnlyList<PackageSourceDto> ParseApk(string output)
    {
        var sources = new List<PackageSourceDto>();
        foreach (var raw in output.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line == ApkMarker || line.StartsWith('#')) continue;
            var tag = string.Empty;
            var uri = line;
            if (line.StartsWith('@'))
            {
                var parts = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;
                tag = parts[0];
                uri = parts[1];
            }

            var suite = uri.TrimEnd('/').Split('/').LastOrDefault() ?? uri;
            if (!string.IsNullOrEmpty(tag)) suite = $"{tag} ({suite})";
            sources.Add(new PackageSourceDto("apk", uri, suite, [], null, "/etc/apk/repositories", Enabled: true));
        }

        return sources;
    }

    private static string HostOf(string uri) => Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ? parsed.Host : uri;

    [GeneratedRegex(@"^(?<type>deb|deb-src)\s+(?:\[(?<options>[^\]]*)\]\s+)?(?<rest>.+)$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex OneLine();

    [GeneratedRegex(@"(?:^|\s)signed-by=(\S+)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SignedByOption();

    // "500 https://example/debian bookworm/main amd64 Packages"
    [GeneratedRegex(@"^-?\d+\s+(\S+://\S+)\s+(\S+)\s+\S+\s+Packages$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SourceLine();

    // "5:25.0.0-1~ubuntu.22.04~jammy 500" or "*** 2.5.0-1 100"
    [GeneratedRegex(@"^(?:\*\*\*\s+)?(\S+)\s+-?\d+$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex VersionLine();
}
