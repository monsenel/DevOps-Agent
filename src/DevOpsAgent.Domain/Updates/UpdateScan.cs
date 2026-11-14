namespace DevOpsAgent.Domain.Updates;

public sealed class UpdateScan
{
    private UpdateScan() { }

    public UpdateScan(Guid serverId, string? packageManager, DateTimeOffset checkedAtUtc, string? error)
    {
        ServerId = serverId;
        PackageManager = packageManager;
        CheckedAtUtc = checkedAtUtc;
        Error = error;
    }

    public Guid ServerId { get; private set; }
    public string? PackageManager { get; private set; }
    public DateTimeOffset CheckedAtUtc { get; private set; }
    public string? Error { get; private set; }
}
