namespace DevOpsAgent.Application.Servers;

public sealed record ServerCapabilityResult(
    Guid ServerId,
    bool Reachable,
    string? OsInfo,
    string? Uptime,
    bool? SudoCapable,
    IReadOnlyList<string> AvailableCommands,
    string? Error,
    DateTimeOffset TestedAtUtc);
