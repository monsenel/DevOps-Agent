namespace DevOpsAgent.Application.Discovery;

public sealed record ReverseProxyDiscoveryResult(
    IReadOnlyList<ReverseProxyRouteInfo> Routes)
{
    public IReadOnlyDictionary<string, IReadOnlyList<string>> NameResolutions { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
}

public sealed record ReverseProxyRouteInfo(
    string ProxyType,
    string? FilePath,
    int Line,
    string? FrontendName,
    IReadOnlyList<string> Domains,
    int? ListenPort,
    string ListenProtocol,
    string? BackendName,
    IReadOnlyList<ReverseProxyTargetInfo> Targets);

public sealed record ReverseProxyTargetInfo(
    string Address,
    string? ServerName = null,
    int Line = 0);
