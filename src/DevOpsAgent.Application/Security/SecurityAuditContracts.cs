namespace DevOpsAgent.Application.Security;

public enum SecurityFindingSeverity
{
    Pass,
    Info,
    Warning,
    High
}

public sealed record SecurityAuditFinding(
    string CheckId,
    string Category,
    SecurityFindingSeverity Severity,
    string Title,
    string Detail,
    string? Recommendation = null);

public sealed record SecurityAuditReport(
    Guid ServerId,
    string ServerName,
    DateTimeOffset AuditedAtUtc,
    IReadOnlyList<SecurityAuditFinding> Findings,
    string? Error = null)
{
    public int HighCount => Findings.Count(finding => finding.Severity == SecurityFindingSeverity.High);
    public int WarningCount => Findings.Count(finding => finding.Severity == SecurityFindingSeverity.Warning);
    public int PassCount => Findings.Count(finding => finding.Severity == SecurityFindingSeverity.Pass);
}
