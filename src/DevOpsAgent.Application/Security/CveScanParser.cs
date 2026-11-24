using System.Text.Json;

namespace DevOpsAgent.Application.Security;

public sealed record CveFinding(
    string Id,
    string Severity,
    string PackageName,
    string InstalledVersion,
    string? FixedVersion,
    string? Target,
    string? Title);

public sealed record CveScanReport(
    Guid ServerId,
    string ServerName,
    DateTimeOffset ScannedAtUtc,
    string Scanner,
    bool ToolAvailable,
    IReadOnlyList<CveFinding> Findings,
    string? Error = null)
{
    public int CriticalCount => Findings.Count(f => string.Equals(f.Severity, "Critical", StringComparison.OrdinalIgnoreCase));
    public int HighCount => Findings.Count(f => string.Equals(f.Severity, "High", StringComparison.OrdinalIgnoreCase));
    public int MediumCount => Findings.Count(f => string.Equals(f.Severity, "Medium", StringComparison.OrdinalIgnoreCase));
    public int LowCount => Findings.Count(f => string.Equals(f.Severity, "Low", StringComparison.OrdinalIgnoreCase));
    public int TotalCount => Findings.Count;
}

/// <summary>
/// Deterministic read-only parser for Trivy and Grype JSON vulnerability scan output.
/// </summary>
public static class CveScanParser
{
    public const string ScannerMarkerPrefix = "__DVA_CVE_SCANNER__:";

    /// <summary>
    /// Read-only remote command that runs Trivy if installed, else Grype, else reports that neither tool is present.
    /// Never modifies packages or files on the target server.
    /// </summary>
    public const string ScanCommand =
        "if command -v trivy >/dev/null 2>&1; then "
        + "printf '__DVA_CVE_SCANNER__:trivy\\n'; trivy rootfs --scanners vuln --format json --quiet / 2>/dev/null; "
        + "elif command -v grype >/dev/null 2>&1; then "
        + "printf '__DVA_CVE_SCANNER__:grype\\n'; grype dir:/ -o json -q 2>/dev/null; "
        + "else printf '__DVA_CVE_SCANNER__:none\\n'; fi";

    public static CveScanReport Parse(Guid serverId, string serverName, string output, DateTimeOffset scannedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return new CveScanReport(serverId, serverName, scannedAtUtc, "unknown", false, [],
                "CVE scanner produced no output.");
        }

        var (scanner, payload) = SplitMarker(output);

        if (string.Equals(scanner, "none", StringComparison.OrdinalIgnoreCase))
        {
            return new CveScanReport(
                serverId,
                serverName,
                scannedAtUtc,
                "none",
                ToolAvailable: false,
                [],
                "Neither trivy nor grype is installed on this server. Install Trivy or Grype to enable CVE scanning.");
        }

        var jsonStart = payload.IndexOf('{');
        if (jsonStart < 0)
        {
            return new CveScanReport(serverId, serverName, scannedAtUtc, scanner ?? "unknown", true, [],
                "CVE scanner output did not contain a JSON document.");
        }

        var jsonSlice = payload[jsonStart..].Trim();
        try
        {
            using var document = JsonDocument.Parse(jsonSlice);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new CveScanReport(serverId, serverName, scannedAtUtc, scanner ?? "unknown", true, [],
                    "CVE scanner JSON root was not an object.");
            }

            if (string.Equals(scanner, "trivy", StringComparison.OrdinalIgnoreCase)
                || (scanner is null && root.TryGetProperty("Results", out _)))
            {
                var findings = ParseTrivy(root);
                return new CveScanReport(serverId, serverName, scannedAtUtc, "trivy", true, findings);
            }

            if (string.Equals(scanner, "grype", StringComparison.OrdinalIgnoreCase)
                || (scanner is null && root.TryGetProperty("matches", out _)))
            {
                var findings = ParseGrype(root);
                return new CveScanReport(serverId, serverName, scannedAtUtc, "grype", true, findings);
            }

            return new CveScanReport(serverId, serverName, scannedAtUtc, scanner ?? "unknown", true, [],
                "Unrecognized CVE scanner JSON format.");
        }
        catch (JsonException exception)
        {
            return new CveScanReport(serverId, serverName, scannedAtUtc, scanner ?? "unknown", true, [],
                $"Could not parse CVE scanner JSON output ({exception.GetType().Name}).");
        }
    }

    private static (string? Scanner, string Payload) SplitMarker(string output)
    {
        var lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith(ScannerMarkerPrefix, StringComparison.Ordinal))
            {
                var scanner = trimmed[ScannerMarkerPrefix.Length..].Trim().ToLowerInvariant();
                var remainder = string.Join('\n', lines.Skip(i + 1));
                return (scanner, remainder);
            }
        }

        return (null, output);
    }

    private static IReadOnlyList<CveFinding> ParseTrivy(JsonElement root)
    {
        var findings = new List<CveFinding>();
        var seen = new HashSet<(string Id, string Pkg, string Version)>();

        if (!root.TryGetProperty("Results", out var results) || results.ValueKind != JsonValueKind.Array)
            return findings;

        foreach (var result in results.EnumerateArray())
        {
            var target = GetString(result, "Target");
            if (!result.TryGetProperty("Vulnerabilities", out var vulns) || vulns.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var vuln in vulns.EnumerateArray())
            {
                var id = GetString(vuln, "VulnerabilityID");
                var pkg = GetString(vuln, "PkgName");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(pkg)) continue;

                var installed = GetString(vuln, "InstalledVersion") ?? string.Empty;
                var fixedVersion = NullIfBlank(GetString(vuln, "FixedVersion"));
                var severity = NormalizeSeverity(GetString(vuln, "Severity"));
                var title = NullIfBlank(GetString(vuln, "Title"));

                if (seen.Add((id, pkg, installed)))
                {
                    findings.Add(new CveFinding(id, severity, pkg, installed, fixedVersion, target, title));
                }
            }
        }

        return OrderFindings(findings);
    }

    private static IReadOnlyList<CveFinding> ParseGrype(JsonElement root)
    {
        var findings = new List<CveFinding>();
        var seen = new HashSet<(string Id, string Pkg, string Version)>();

        if (!root.TryGetProperty("matches", out var matches) || matches.ValueKind != JsonValueKind.Array)
            return findings;

        foreach (var match in matches.EnumerateArray())
        {
            if (!match.TryGetProperty("vulnerability", out var vuln) || vuln.ValueKind != JsonValueKind.Object)
                continue;
            if (!match.TryGetProperty("artifact", out var artifact) || artifact.ValueKind != JsonValueKind.Object)
                continue;

            var id = GetString(vuln, "id");
            var pkg = GetString(artifact, "name");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(pkg)) continue;

            var installed = GetString(artifact, "version") ?? string.Empty;
            var severity = NormalizeSeverity(GetString(vuln, "severity"));
            var title = NullIfBlank(GetString(vuln, "description"));

            string? fixedVersion = null;
            if (vuln.TryGetProperty("fix", out var fix) && fix.ValueKind == JsonValueKind.Object
                && fix.TryGetProperty("versions", out var versions) && versions.ValueKind == JsonValueKind.Array)
            {
                foreach (var ver in versions.EnumerateArray())
                {
                    if (ver.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(ver.GetString()))
                    {
                        fixedVersion = ver.GetString()!.Trim();
                        break;
                    }
                }
            }

            string? target = null;
            if (artifact.TryGetProperty("locations", out var locations) && locations.ValueKind == JsonValueKind.Array)
            {
                foreach (var loc in locations.EnumerateArray())
                {
                    var path = GetString(loc, "path");
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        target = path;
                        break;
                    }
                }
            }

            if (seen.Add((id, pkg, installed)))
            {
                findings.Add(new CveFinding(id, severity, pkg, installed, fixedVersion, target, title));
            }
        }

        return OrderFindings(findings);
    }

    private static IReadOnlyList<CveFinding> OrderFindings(List<CveFinding> findings) =>
        findings
            .OrderByDescending(f => SeverityRank(f.Severity))
            .ThenBy(f => f.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.PackageName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string NormalizeSeverity(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Unknown";
        return raw.Trim().ToUpperInvariant() switch
        {
            "CRITICAL" => "Critical",
            "HIGH" => "High",
            "MEDIUM" => "Medium",
            "LOW" => "Low",
            "NEGLIGIBLE" => "Low",
            _ => "Unknown"
        };
    }

    private static int SeverityRank(string severity) => severity switch
    {
        "Critical" => 4,
        "High" => 3,
        "Medium" => 2,
        "Low" => 1,
        _ => 0
    };

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()?.Trim()
            : null;

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
