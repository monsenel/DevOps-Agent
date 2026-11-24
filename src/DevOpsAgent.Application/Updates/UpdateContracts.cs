namespace DevOpsAgent.Application.Updates;

public enum PackageManagerKind
{
    Apt,
    Dnf,
    Apk
}

public sealed record UpdatePackageDto(string Name, string CurrentVersion, string CandidateVersion, string Repository, bool IsSecurity);

/// <summary>A configured package source (T8.6): apt URI, suite, components and signed-by key; or a dnf repo id, name and URL.</summary>
/// <param name="File">The apt file it is configured in, or the dnf repository's name.</param>
public sealed record PackageSourceDto(string Type, string Uri, string Suite, IReadOnlyList<string> Components, string? SignedBy,
    string File, bool Enabled);
/// <param name="ReadOnly">The server's group is read-only (D-027, D-029): updates are listed but cannot be selected or applied.</param>
public sealed record ServerUpdateStatus(Guid ServerId, string ServerName, string Group, PackageManagerKind? PackageManager,
    DateTimeOffset? CheckedAtUtc, IReadOnlyList<UpdatePackageDto> Packages, string? Error,
    DateTimeOffset? PackageListsRefreshedAtUtc = null, bool CacheOnly = false, bool ReadOnly = false,
    IReadOnlyList<PackageSourceDto>? Sources = null)
{
    public const string ReadOnlyReason = "Read-only group: updates can be reviewed but not applied. Change the group's execution mode in Settings › Execution modes.";

    /// <summary>
    /// The line under the server name (T8.1): package manager, scan time and, once scanned, when the package lists were last
    /// refreshed, separated by " · ".
    /// </summary>
    public string MetaLine(DateTimeOffset nowUtc, Func<DateTimeOffset, string> formatTime)
    {
        var parts = new List<string>
        {
            PackageManager?.ToString().ToLowerInvariant() ?? "Not scanned",
            CheckedAtUtc is { } checkedAt ? formatTime(checkedAt) : "No scan yet"
        };
        if (CheckedAtUtc is not null) parts.Add(PackageListProbe.Describe(PackageListsRefreshedAtUtc, CacheOnly, nowUtc));
        return string.Join(" · ", parts);
    }
}
public sealed record UpdateCollectionOutput(string AptOutput, string DnfOutput, string SecurityOutput, string? Error,
    DateTimeOffset? PackageListsRefreshedAtUtc = null, bool CacheOnly = false, string SourcesOutput = "", string AptPolicyOutput = "",
    string ApkOutput = "", PackageManagerKind? PackageManager = null);
public sealed record CollectedUpdateResult(PackageManagerKind PackageManager, string ResultJson,
    DateTimeOffset? PackageListsRefreshedAtUtc = null, bool CacheOnly = false, string SourcesJson = "[]");
