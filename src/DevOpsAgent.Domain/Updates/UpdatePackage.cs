namespace DevOpsAgent.Domain.Updates;

public sealed class UpdatePackage
{
    private UpdatePackage() { }

    public UpdatePackage(Guid serverId, string name, string currentVersion, string candidateVersion,
        string repository, bool isSecurity, DateTimeOffset checkedAtUtc)
    {
        Id = Guid.NewGuid();
        ServerId = serverId;
        Name = name;
        CurrentVersion = currentVersion;
        CandidateVersion = candidateVersion;
        Repository = repository;
        IsSecurity = isSecurity;
        CheckedAtUtc = checkedAtUtc;
    }

    public Guid Id { get; private set; }
    public Guid ServerId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string CurrentVersion { get; private set; } = string.Empty;
    public string CandidateVersion { get; private set; } = string.Empty;
    public string Repository { get; private set; } = string.Empty;
    public bool IsSecurity { get; private set; }
    public DateTimeOffset CheckedAtUtc { get; private set; }
}
