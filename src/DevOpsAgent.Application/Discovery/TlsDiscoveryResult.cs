namespace DevOpsAgent.Application.Discovery;

public sealed record TlsDiscoveryResult(IReadOnlyList<TlsCertificateInfo> Certificates);

public sealed record TlsCertificateInfo(
    string Path,
    string Subject,
    string Issuer,
    DateTimeOffset NotBeforeUtc,
    DateTimeOffset NotAfterUtc,
    IReadOnlyList<string> SubjectAlternativeNames,
    bool IsSelfSigned,
    string? SerialNumber = null);
