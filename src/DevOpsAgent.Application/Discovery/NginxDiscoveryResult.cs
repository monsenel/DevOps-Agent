namespace DevOpsAgent.Application.Discovery;

public sealed record NginxDiscoveryResult(
    IReadOnlyList<NginxServerInfo> Servers,
    IReadOnlyList<NginxUpstreamInfo> Upstreams);

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
    int Line);

public sealed record NginxSourceValue(string Value, string? FilePath, int Line);
