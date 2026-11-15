namespace DevOpsAgent.Domain.Updates;

public sealed class UpdateScan
{
    private UpdateScan() { }

    public UpdateScan(Guid serverId, string? packageManager, DateTimeOffset checkedAtUtc, string? error,
        DateTimeOffset? packageListsRefreshedAtUtc = null, bool cacheOnly = false)
    {
        ServerId = serverId;
        PackageManager = packageManager;
        CheckedAtUtc = checkedAtUtc;
        Error = error;
        PackageListsRefreshedAtUtc = packageListsRefreshedAtUtc;
        CacheOnly = cacheOnly;
    }

    public Guid ServerId { get; private set; }
    public string? PackageManager { get; private set; }
    public DateTimeOffset CheckedAtUtc { get; private set; }
    public string? Error { get; private set; }

    /// <summary>When the server's package lists were last refreshed (read-only probe); null when unknown.</summary>
    public DateTimeOffset? PackageListsRefreshedAtUtc { get; private set; }

    /// <summary>True when the scan used cached lists only (read-only group: no apt-get update, no sudo).</summary>
    public bool CacheOnly { get; private set; }
}
