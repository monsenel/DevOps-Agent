namespace DevOpsAgent.Infrastructure.Persistence;

public sealed class PersistenceOptions
{
    public const string SectionName = nameof(PersistenceOptions);

    public string ConnectionString { get; set; } = "Data Source=/data/devops-agent.db";

    public string DataProtectionKeysDirectory { get; set; } = "/data/keys";
}
