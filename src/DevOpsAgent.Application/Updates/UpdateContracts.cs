namespace DevOpsAgent.Application.Updates;

public enum PackageManagerKind
{
    Apt,
    Dnf
}

public sealed record UpdatePackageDto(string Name, string CurrentVersion, string CandidateVersion, string Repository, bool IsSecurity);
public sealed record ServerUpdateStatus(Guid ServerId, string ServerName, string Group, PackageManagerKind? PackageManager,
    DateTimeOffset? CheckedAtUtc, IReadOnlyList<UpdatePackageDto> Packages, string? Error,
    DateTimeOffset? PackageListsRefreshedAtUtc = null, bool CacheOnly = false);
public sealed record UpdateCollectionOutput(string AptOutput, string DnfOutput, string SecurityOutput, string? Error,
    DateTimeOffset? PackageListsRefreshedAtUtc = null, bool CacheOnly = false);
public sealed record CollectedUpdateResult(PackageManagerKind PackageManager, string ResultJson,
    DateTimeOffset? PackageListsRefreshedAtUtc = null, bool CacheOnly = false);
