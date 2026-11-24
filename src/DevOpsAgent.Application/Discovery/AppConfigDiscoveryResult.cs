namespace DevOpsAgent.Application.Discovery;

public sealed record AppConfigDiscoveryResult(
    IReadOnlyList<AppDependencyReference> References)
{
    public IReadOnlyDictionary<string, IReadOnlyList<string>> NameResolutions { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
}

public sealed record AppDependencyReference(
    string DependencyType,
    string Host,
    int? Port,
    string Protocol,
    string SourceKind,
    string SourcePath,
    string? SettingKey = null,
    string? ContainerName = null,
    string? DatabaseName = null,
    int? Line = null);
