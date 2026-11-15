namespace DevOpsAgent.Application.Servers;

public sealed record ServerCapabilityResult(
    Guid ServerId,
    bool Reachable,
    string? OsInfo,
    string? Uptime,
    bool? SudoCapable,
    IReadOnlyList<string> AvailableCommands,
    string? Error,
    DateTimeOffset TestedAtUtc,
    bool SudoPasswordUsed = false,
    bool SudoPasswordRejected = false,
    bool SudoProbeSkipped = false,
    bool SudoPasswordOutcomeUnknown = false);
