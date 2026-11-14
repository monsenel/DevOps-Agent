namespace DevOpsAgent.Infrastructure.Collectors;

public sealed class UpdateCollectorOptions
{
    public const string SectionName = "UpdateCollector";

    public int CommandTimeoutSeconds { get; set; } = 300;
    public int RepositoryTimeoutSeconds { get; set; } = 10;

    public TimeSpan CommandTimeout => TimeSpan.FromSeconds(Math.Clamp(CommandTimeoutSeconds, 1, 3600));
    public int RepositoryTimeout => Math.Clamp(RepositoryTimeoutSeconds, 1, 120);
}
