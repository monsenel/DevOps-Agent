using System.Globalization;
using DevOpsAgent.Application.Discovery;

namespace DevOpsAgent.Infrastructure.Collectors;

public static class TlsCollectorParser
{
    public const string CertMarkerPrefix = "__DVA_CERT__:";

    private static readonly string[] OpenSslDateFormats =
    [
        "MMM d HH:mm:ss yyyy 'GMT'",
        "MMM  d HH:mm:ss yyyy 'GMT'",
        "MMM dd HH:mm:ss yyyy 'GMT'",
        "yyyy-MM-dd HH:mm:ss 'UTC'"
    ];

    public static TlsDiscoveryResult Parse(string output)
    {
        var certificates = new List<TlsCertificateInfo>();
        string? currentPath = null;
        var currentLines = new List<string>();

        void Flush()
        {
            if (currentPath is not null && TryParseCertificate(currentPath, currentLines, out var cert))
            {
                certificates.Add(cert);
            }

            currentLines.Clear();
        }

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith(CertMarkerPrefix, StringComparison.Ordinal))
            {
                Flush();
                currentPath = line[CertMarkerPrefix.Length..].Trim();
            }
            else if (currentPath is not null)
            {
                currentLines.Add(line);
            }
        }

        Flush();
        return new TlsDiscoveryResult(certificates);
    }

    private static bool TryParseCertificate(string path, IReadOnlyList<string> lines, out TlsCertificateInfo certificate)
    {
        certificate = default!;
        string? subject = null;
        string? issuer = null;
        DateTimeOffset? notBefore = null;
        DateTimeOffset? notAfter = null;
        string? serial = null;
        var sans = new List<string>();
        var expectingSanLine = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            if (expectingSanLine)
            {
                expectingSanLine = false;
                foreach (var entry in line.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    if (entry.StartsWith("DNS:", StringComparison.OrdinalIgnoreCase))
                    {
                        sans.Add(entry[4..].Trim());
                    }
                    else if (entry.StartsWith("IP Address:", StringComparison.OrdinalIgnoreCase))
                    {
                        sans.Add(entry["IP Address:".Length..].Trim());
                    }
                    else if (entry.StartsWith("IP:", StringComparison.OrdinalIgnoreCase))
                    {
                        sans.Add(entry[3..].Trim());
                    }
                }
                continue;
            }

            if (line.StartsWith("subject=", StringComparison.OrdinalIgnoreCase))
            {
                subject = NormalizeDistinguishedName(line["subject=".Length..]);
            }
            else if (line.StartsWith("issuer=", StringComparison.OrdinalIgnoreCase))
            {
                issuer = NormalizeDistinguishedName(line["issuer=".Length..]);
            }
            else if (line.StartsWith("notBefore=", StringComparison.OrdinalIgnoreCase))
            {
                notBefore = ParseOpenSslDate(line["notBefore=".Length..]);
            }
            else if (line.StartsWith("notAfter=", StringComparison.OrdinalIgnoreCase))
            {
                notAfter = ParseOpenSslDate(line["notAfter=".Length..]);
            }
            else if (line.StartsWith("serial=", StringComparison.OrdinalIgnoreCase))
            {
                serial = line["serial=".Length..].Trim();
            }
            else if (line.StartsWith("X509v3 Subject Alternative Name:", StringComparison.OrdinalIgnoreCase))
            {
                expectingSanLine = true;
            }
        }

        if (string.IsNullOrWhiteSpace(subject) || notBefore is null || notAfter is null)
        {
            return false;
        }

        issuer ??= subject;
        var isSelfSigned = string.Equals(subject, issuer, StringComparison.OrdinalIgnoreCase);
        certificate = new TlsCertificateInfo(
            path,
            subject,
            issuer,
            notBefore.Value,
            notAfter.Value,
            sans.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            isSelfSigned,
            serial);
        return true;
    }

    private static string NormalizeDistinguishedName(string raw) =>
        raw.Trim().TrimStart('/');

    private static DateTimeOffset? ParseOpenSslDate(string raw)
    {
        var value = raw.Trim();
        if (DateTimeOffset.TryParseExact(value, OpenSslDateFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal | DateTimeStyles.AllowWhiteSpaces,
                out var parsed))
        {
            return parsed;
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed)
            ? parsed
            : null;
    }
}
