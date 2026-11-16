namespace DevOpsAgent.Application.Discovery;

public sealed record NginxDiscoveryResult(
    IReadOnlyList<NginxServerInfo> Servers,
    IReadOnlyList<NginxUpstreamInfo> Upstreams)
{
    public IReadOnlyDictionary<string, IReadOnlyList<string>> NameResolutions { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
}

public sealed record NginxServerInfo(
    string Context,
    IReadOnlyList<NginxSourceValue> Listens,
    IReadOnlyList<NginxSourceValue> ServerNames,
    IReadOnlyList<NginxLocationInfo> Locations,
    IReadOnlyList<NginxProxyInfo> Proxies,
    string? FilePath,
    int Line);

public sealed record NginxLocationInfo(
    string Match,
    IReadOnlyList<NginxProxyInfo> Proxies,
    string? FilePath,
    int Line);

public sealed record NginxUpstreamInfo(
    string Context,
    string Name,
    IReadOnlyList<NginxUpstreamServerInfo> Servers,
    string? FilePath,
    int Line);

public sealed record NginxUpstreamServerInfo(
    string Address,
    int? Weight,
    IReadOnlyList<string> Parameters,
    string? FilePath,
    int Line);

public sealed record NginxProxyInfo(
    string Directive,
    string Target,
    bool IsUnresolved,
    string? FilePath,
    int Line)
{
    /// <summary>
    /// Set when <see cref="Target"/> was substituted from a <c>set $variable value;</c> directive.
    /// Optional so results stored before T8.14 still deserialize.
    /// </summary>
    public NginxVariableResolution? ResolvedFrom { get; init; }
}

/// <summary>The original variable form of a proxy target and the <c>set</c> directive that resolved it.</summary>
public sealed record NginxVariableResolution(
    string OriginalTarget,
    string Variable,
    string Value,
    string? FilePath,
    int Line);

public sealed record NginxSourceValue(string Value, string? FilePath, int Line);
