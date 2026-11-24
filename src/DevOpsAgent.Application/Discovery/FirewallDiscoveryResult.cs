namespace DevOpsAgent.Application.Discovery;

public sealed record FirewallDiscoveryResult(
    string Backend,
    bool Enabled,
    string? DefaultInbound,
    string? DefaultOutbound,
    IReadOnlyList<FirewallRuleInfo> Rules);

public sealed record FirewallRuleInfo(
    string Direction,
    string Action,
    string Protocol,
    string? Port,
    string? Source,
    string? Comment = null);
